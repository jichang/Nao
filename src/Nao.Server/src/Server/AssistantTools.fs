namespace Nao.Assistant

open System
open System.IO
open System.Net.WebSockets
open System.Net.Sockets
open System.Data.Common
open System.Text
open System.Text.RegularExpressions
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Nao.Core
open Nao.Agents
open Nao.Loader
open Nao.Providers
open Nao.Persistence
open Nao.Feedback
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains

module AssistantTools =

    /// Shared fallback workspace used only when no session turn is active (e.g. tests).
    /// Lives UNDER the single app data directory (`.nao-data`, override with NAO_DATA_DIR)
    /// so there is only one base folder — the legacy `~/.nao-workspace` is no longer used.
    let private globalWorkDir =
        let dataDir =
            match Environment.GetEnvironmentVariable("NAO_DATA_DIR") with
            | path when not (String.IsNullOrWhiteSpace path) -> path
            | _ -> Path.Combine(Environment.CurrentDirectory, ".nao-data")
        Path.Combine(dataDir, "workspace")

    /// The directory all file tools operate in. File storage is unified on the current
    /// session's files folder — the same place uploads and generated files live and the UI
    /// lists — so a user's attachments and a tool's output share one location. Falls back to
    /// the shared workspace when there is no active session. The directory is ensured.
    let private currentWorkDir () =
        let dir =
            match SessionExecution.current () with
            | Some scope -> (SessionFiles.forKey scope.FilesKey).FilesDir
            | None -> globalWorkDir
        Directory.CreateDirectory dir |> ignore
        dir

    /// Resolve a user-supplied relative path inside the current working directory,
    /// preventing traversal outside it via "..".
    let private resolvePath (input: string) =
        let root = currentWorkDir ()
        let cleaned = input.Trim().Replace("\\", "/").TrimStart('/')
        let full = Path.GetFullPath(Path.Combine(root, cleaned))
        let rootFull = Path.GetFullPath(root)
        if full = rootFull || full.StartsWith(rootFull + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
        then full
        else Path.GetFullPath(Path.Combine(root, Path.GetFileName cleaned))

    // ─── Conversation-budget guards ───
    // Large file content must stay on disk, not flood the LLM conversation. read_file
    // returns a bounded window (page through with offset/length); write_file rejects a
    // single oversized blob (build large files with several append calls); and every
    // tool result is clamped before it is handed back to the model.

    /// Max characters any single tool result may contribute to the conversation.
    let private maxToolResultChars = 24000
    /// Max characters read_file returns in one call (a page of a large file).
    let private maxReadWindowChars = 20000
    /// Max characters write_file accepts in a single call (use append for more).
    let private maxWriteChars = 200000

    /// Clamp text to a budget, appending a note that records the original size.
    let private clampText (max: int) (s: string) =
        if not (isNull s) && s.Length > max then
            s.Substring(0, max) + sprintf "\n…(truncated to %d of %d chars)" max s.Length
        else s

    /// Serialize a value (typically an anonymous record) to a compact JSON tool result.
    /// Preferred over hand-writing JSON so strings are always correctly escaped.
    let private json (value: 'T) = JsonSerializer.Serialize value

    /// Hook into the per-workspace knowledge base, set by the embedded server at startup.
    /// Given a query and a result count, returns up to that many (fileName, passage) matches
    /// from files the user explicitly uploaded. The knowledge base is NEVER injected into the
    /// conversation automatically — the agent must call the search_knowledge tool to consult it.
    let mutable knowledgeSearch: (string -> int -> (string * string) list) option = None

    let ensureWorkDir () =
        Directory.CreateDirectory(globalWorkDir) |> ignore
        globalWorkDir

    let createFolder: Tool =
        Tool.Create("create_folder", "Create a new folder. Input: relative folder path.",
            fun input -> task {
                let path = resolvePath input
                Directory.CreateDirectory(path) |> ignore
                return json {| created = path.Replace("\\", "/"); exists = true |}
            })

    let writeFile: Tool =
        Tool.Create("write_file",
            "Write text to a workspace file. Input: 'relative/path|content' (overwrites), or \
            \'relative/path|append|content' to append. Content is written straight to disk; for very \
            \large files, build them up with several append calls instead of one huge write.",
            fun input -> task {
                let parts = input.Split('|', 2)
                if parts.Length < 2 then return json {| error = "Expected 'path|content' or 'path|append|content'" |}
                else
                    let path = resolvePath parts.[0]
                    let rest = parts.[1]
                    let isAppend = rest.StartsWith("append|")
                    let content = if isAppend then rest.Substring("append|".Length) else rest
                    if content.Length > maxWriteChars then
                        return json {| error = sprintf "Content too large (%d chars); write in smaller pieces using 'path|append|...'." content.Length
                                       maxChars = maxWriteChars |}
                    else
                        let dir = Path.GetDirectoryName(path)
                        if not (Directory.Exists(dir)) then Directory.CreateDirectory(dir) |> ignore
                        if isAppend then do! File.AppendAllTextAsync(path, content)
                        else do! File.WriteAllTextAsync(path, content)
                        let total = (FileInfo path).Length
                        return json {| written = path.Replace("\\", "/")
                                       mode = (if isAppend then "append" else "overwrite")
                                       bytes = content.Length
                                       totalBytes = total |}
            })

    let readFile: Tool =
        Tool.Create("read_file",
            "Read a text file's contents. Input: 'name-or-path' (returns up to ~20k chars), or \
            \'name-or-path|offset' / 'name-or-path|offset|length' to read a character window so you can \
            \page through large files. This is also how you read a file the user attached: pass the \
            \attachment's file name. Attachments are NOT included in the conversation automatically — call \
            \this tool to read one only when you actually need its contents. Oversized files are truncated \
            \with a note showing the total size and the offset to continue from.",
            fun input -> task {
                let parts = input.Split('|')
                let nameOrPath = parts.[0].Trim()
                let path = resolvePath nameOrPath
                if not (File.Exists path) then
                    return json {| error = sprintf "File not found: %s" nameOrPath |}
                else
                    let content = File.ReadAllText path
                    let total = content.Length
                    let offset =
                        if parts.Length > 1 then
                            match Int32.TryParse(parts.[1].Trim()) with
                            | true, n when n >= 0 -> min n total
                            | _ -> 0
                        else 0
                    let window =
                        if parts.Length > 2 then
                            match Int32.TryParse(parts.[2].Trim()) with
                            | true, n when n > 0 -> min n maxReadWindowChars
                            | _ -> maxReadWindowChars
                        else maxReadWindowChars
                    let take = min window (total - offset)
                    let slice = content.Substring(offset, take)
                    let nextOffset = offset + take
                    if nextOffset < total then
                        return slice + sprintf "\n…(showing chars %d–%d of %d; read '%s|%d' to continue)"
                                            offset nextOffset total nameOrPath nextOffset
                    elif offset > 0 then
                        return slice + sprintf "\n…(showing chars %d–%d of %d; end of file)" offset nextOffset total
                    else
                        return slice
            })

    let listFolder: Tool =
        Tool.Create("list_folder", "List directory contents. Input: relative path (empty for root).",
            fun input -> task {
                let path = if String.IsNullOrWhiteSpace(input) then currentWorkDir () else resolvePath input
                if not (Directory.Exists(path)) then
                    return json {| error = sprintf "Directory not found: %s" input |}
                else
                    let entries =
                        Directory.GetFileSystemEntries(path)
                        |> Array.map (fun e ->
                            {| name = Path.GetFileName(e); ``type`` = (if Directory.Exists(e) then "dir" else "file") |})
                    return json {| path = path.Replace("\\", "/"); entries = entries |}
            })

    let delete: Tool =
        Tool.Create("delete", "Delete a file or folder. Input: relative path.",
            fun input -> task {
                let path = resolvePath input
                if File.Exists(path) then
                    File.Delete(path)
                    return json {| deleted = input; ``type`` = "file" |}
                elif Directory.Exists(path) then
                    Directory.Delete(path, true)
                    return json {| deleted = input; ``type`` = "dir" |}
                else
                    return json {| error = sprintf "Not found: %s" input |}
            })

    let dateTime: Tool =
        Tool.Create("get_datetime", "Get the current date and time.",
            fun _ -> task {
                return DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")
            })

    let calculator: Tool =
        Tool.Create("calculator", "Evaluate a simple math expression. Input: expression like '2 + 3'.",
            fun input -> task {
                try
                    let parts = input.Trim().Split(' ')
                    if parts.Length = 3 then
                        let a = Double.Parse(parts.[0])
                        let b = Double.Parse(parts.[2])
                        let result =
                            match parts.[1] with
                            | "+" -> a + b | "-" -> a - b
                            | "*" -> a * b | "/" -> if b <> 0.0 then a / b else Double.NaN
                            | _ -> Double.NaN
                        if Double.IsNaN result then return json {| error = "Invalid expression" |}
                        else return json {| result = result |}
                    else
                        return json {| error = "Expected format: 'a op b'" |}
                with ex ->
                    return json {| error = ex.Message |}
            })

    /// Shared HTTP client for the web tools (connection pooling, sane timeout).
    let private httpClient =
        let c = new System.Net.Http.HttpClient()
        c.Timeout <- TimeSpan.FromSeconds(20.0)
        c.DefaultRequestHeaders.Add("User-Agent", "Nao-Assistant/1.0")
        c

    let private truncate (max: int) (s: string) =
        if s.Length > max then s.Substring(0, max) + "\n...(truncated)" else s

    let httpRequest: Tool =
        Tool.Create("http_request",
            "Make an HTTP request to any URL. Input: 'METHOD URL' or 'METHOD URL|body' (METHOD defaults to GET). Returns the status code and response body.",
            fun input -> task {
                try
                    let trimmed = input.Trim()
                    let reqPart, body =
                        match trimmed.IndexOf('|') with
                        | -1 -> trimmed, None
                        | i -> trimmed.Substring(0, i).Trim(), Some (trimmed.Substring(i + 1))
                    let parts = reqPart.Split([| ' ' |], 2, StringSplitOptions.RemoveEmptyEntries)
                    let methodStr, url =
                        if parts.Length = 2 && not (parts.[0].StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        then parts.[0].ToUpperInvariant(), parts.[1].Trim()
                        else "GET", reqPart
                    use req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod(methodStr), url)
                    match body with
                    | Some b -> req.Content <- new System.Net.Http.StringContent(b, Encoding.UTF8, "application/json")
                    | None -> ()
                    let! resp = httpClient.SendAsync(req)
                    let! content = resp.Content.ReadAsStringAsync()
                    return json {| status = int resp.StatusCode; body = truncate 8000 content |}
                with ex ->
                    return json {| error = ex.Message |}
            })

    let webFetch: Tool =
        Tool.Create("web_fetch",
            "Fetch a web page and return its readable text content (HTML tags stripped). Input: a URL.",
            fun input -> task {
                try
                    let url = input.Trim()
                    let! html = httpClient.GetStringAsync(url)
                    let noScript = Regex.Replace(html, "(?is)<(script|style)[^>]*>.*?</\\1>", " ")
                    let noTags = Regex.Replace(noScript, "(?s)<[^>]+>", " ")
                    let decoded = System.Net.WebUtility.HtmlDecode(noTags)
                    let collapsed = Regex.Replace(decoded, "\\s+", " ").Trim()
                    return truncate 8000 collapsed
                with ex ->
                    return json {| error = ex.Message |}
            })

    let searchFiles: Tool =
        Tool.Create("search_files",
            "Search workspace files for a regex pattern. Input: 'pattern' or 'pattern|subdir'. Returns up to 200 'relative/path:line: text' matches.",
            fun input -> task {
                try
                    let trimmed = input.Trim()
                    let pattern, sub =
                        match trimmed.IndexOf('|') with
                        | -1 -> trimmed, ""
                        | i -> trimmed.Substring(0, i).Trim(), trimmed.Substring(i + 1).Trim()
                    let baseDir = currentWorkDir ()
                    let root = if String.IsNullOrWhiteSpace sub then baseDir else resolvePath sub
                    if not (Directory.Exists root) then return json {| error = sprintf "Directory not found: %s" sub |}
                    else
                        let regex = Regex(pattern, RegexOptions.IgnoreCase)
                        let matches = ResizeArray<string>()
                        for file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) do
                            if matches.Count < 200 then
                                try
                                    let lines = File.ReadAllLines(file)
                                    lines |> Array.iteri (fun i line ->
                                        if matches.Count < 200 && regex.IsMatch(line) then
                                            let rel = Path.GetRelativePath(baseDir, file).Replace("\\", "/")
                                            matches.Add(sprintf "%s:%d: %s" rel (i + 1) (line.Trim())))
                                with _ -> ()
                        return (if matches.Count = 0 then "(no matches)" else String.Join("\n", matches))
                with ex ->
                    return json {| error = ex.Message |}
            })

    let findFiles: Tool =
        Tool.Create("find_files",
            "Find files in the workspace by glob pattern (supports *, ?, and ** for any depth). Input: e.g. '**/*.json'.",
            fun input -> task {
                try
                    let glob = input.Trim().Replace("\\", "/").TrimStart('/')
                    let escaped =
                        Regex.Escape(glob)
                            .Replace("\\*\\*/", "(.*/)?")
                            .Replace("\\*\\*", ".*")
                            .Replace("\\*", "[^/]*")
                            .Replace("\\?", "[^/]")
                    let regex = Regex("^" + escaped + "$", RegexOptions.IgnoreCase)
                    let baseDir = currentWorkDir ()
                    if not (Directory.Exists baseDir) then return "(workspace empty)"
                    else
                        let results =
                            Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories)
                            |> Seq.map (fun f -> Path.GetRelativePath(baseDir, f).Replace("\\", "/"))
                            |> Seq.filter regex.IsMatch
                            |> Seq.truncate 500
                            |> List.ofSeq
                        return (if results.IsEmpty then "(no matches)" else String.Join("\n", results))
                with ex ->
                    return json {| error = ex.Message |}
            })

    /// Map a file extension to the IANA media type understood by the document registry.
    let private mediaTypeForExt (ext: string) =
        match ext.ToLowerInvariant() with
        | ".md" | ".markdown" -> Some Nao.Documents.Markdown.MediaType
        | ".txt" | ".text" -> Some Nao.Documents.PlainText.MediaType
        | ".html" | ".htm" -> Some Nao.Documents.Html.MediaType
        | ".pdf" -> Some Nao.Documents.Pdf.MediaType
        | ".docx" -> Some Nao.Documents.Docx.MediaType
        | ".xlsx" -> Some Nao.Documents.Xlsx.MediaType
        | ".pptx" -> Some Nao.Documents.Pptx.MediaType
        | _ -> None

    /// Map a bare format token — a format NAME ("pdf", "word", "excel") or an extension
    /// ("pdf", ".pdf") — to its canonical file extension. Lets the converter accept a target
    /// expressed as a format ("README.md|pdf") rather than a full filename, so a request like
    /// "convert README.md to pdf" doesn't get mis-saved to a file literally named "pdf".
    let private canonicalExt (token: string) : string option =
        match token.Trim().TrimStart('.').ToLowerInvariant() with
        | "md" | "markdown" -> Some ".md"
        | "txt" | "text" | "plaintext" | "plain" -> Some ".txt"
        | "html" | "htm" -> Some ".html"
        | "pdf" -> Some ".pdf"
        | "docx" | "word" -> Some ".docx"
        | "xlsx" | "excel" | "spreadsheet" -> Some ".xlsx"
        | "pptx" | "powerpoint" | "presentation" | "slides" -> Some ".pptx"
        | _ -> None

    /// Resolve the target the caller asked for into a concrete output path. If the target
    /// already carries a supported extension it is used as-is; otherwise the target is treated
    /// as a format name and the output filename is derived from the SOURCE's base name plus the
    /// format's canonical extension (e.g. "README.md" + pdf -> "README.pdf").
    let private resolveTargetName (sourceName: string) (targetToken: string) : string option =
        let ext = Path.GetExtension targetToken
        if ext <> "" && (mediaTypeForExt ext).IsSome then Some targetToken
        else canonicalExt targetToken |> Option.map (fun e -> Path.GetFileNameWithoutExtension sourceName + e)

    let private documentRegistry = Nao.Documents.Formats.fullRegistry ()

    let convertDocument: Tool =
        Tool.Create("convert_document",
            "Convert a workspace document from one format to another via the unified document model. \
            \Input format: 'source|target'. The source is a file path/name; the target is either a \
            \destination filename (e.g. 'README.pdf') or just the desired format ('pdf', 'docx', 'html', \
            \'xlsx', 'pptx', 'md', 'txt') in which case the output is named after the source. Reads \
            \.md/.markdown, .txt, .html and .docx; writes those plus .pdf, .xlsx and .pptx.",
            fun input -> task {
                try
                    let parts = input.Split('|', 2)
                    if parts.Length < 2 then
                        return json {| error = "Expected 'source|target' (target is a filename or a format like 'pdf')" |}
                    else
                        let sourceRaw = parts.[0].Trim()
                        let sourcePath = resolvePath sourceRaw
                        if not (File.Exists sourcePath) then
                            return json {| error = sprintf "Source not found: %s" sourceRaw |}
                        else
                            match resolveTargetName sourceRaw (parts.[1].Trim()) with
                            | None ->
                                return json {| error = sprintf "Unsupported target format: %s" (parts.[1].Trim()) |}
                            | Some targetName ->
                                let targetPath = resolvePath targetName
                                let srcMt = mediaTypeForExt (Path.GetExtension sourcePath)
                                let tgtMt = mediaTypeForExt (Path.GetExtension targetPath)
                                match srcMt, tgtMt with
                                | None, _ ->
                                    return json {| error = sprintf "Unsupported source format: %s" (Path.GetExtension sourcePath) |}
                                | _, None ->
                                    return json {| error = sprintf "Unsupported target format: %s" (Path.GetExtension targetPath) |}
                                | Some src, Some tgt ->
                                    let dir = Path.GetDirectoryName(targetPath)
                                    if not (Directory.Exists dir) then Directory.CreateDirectory(dir) |> ignore
                                    Nao.Documents.Converter.convertFile documentRegistry src tgt sourcePath targetPath
                                    let bytes = (FileInfo targetPath).Length
                                    return json {| converted = targetPath.Replace("\\", "/")
                                                   from = src
                                                   ``to`` = tgt
                                                   bytes = bytes |}
                with ex ->
                    return json {| error = ex.Message |}
            })

    /// Search the user's uploaded knowledge base on demand. The base is not loaded into the
    /// conversation by default; the agent calls this tool (after asking the user) only when it
    /// genuinely needs information from files the user uploaded.
    let searchKnowledge: Tool =
        Tool.Create("search_knowledge",
            "Search the user's knowledge base — documents the user explicitly uploaded — for passages \
            \relevant to a query, returning the top matches as { file, text } snippets. The knowledge \
            \base is NOT loaded automatically: only call this when you actually need information from the \
            \user's uploaded files, and ASK THE USER FOR PERMISSION before each search. \
            \Input: 'query' or 'query|topK' (topK defaults to 4, max 10).",
            fun input -> task {
                match knowledgeSearch with
                | None -> return json {| error = "Knowledge base is not available." |}
                | Some search ->
                    let parts = input.Split('|')
                    let query = parts.[0].Trim()
                    if String.IsNullOrWhiteSpace query then
                        return json {| error = "Expected a search query." |}
                    else
                        let topK =
                            if parts.Length > 1 then
                                match Int32.TryParse(parts.[1].Trim()) with
                                | true, n when n > 0 -> min n 10
                                | _ -> 4
                            else 4
                        let hits = search query topK
                        if List.isEmpty hits then
                            return json {| matches = ([||]: obj[]); note = "No relevant passages found in the knowledge base." |}
                        else
                            let matches =
                                hits
                                |> List.map (fun (f, t) -> {| file = f; text = t |})
                            return json {| matches = matches |}
            })

    let allTools =
        [ createFolder; writeFile; readFile; listFolder; delete; dateTime; calculator
          httpRequest; webFetch; searchFiles; findFiles; searchKnowledge; convertDocument ]
        // Final safety net: clamp every tool result so no tool (current or future) can
        // flood the conversation regardless of the file or response size it produces.
        |> List.map (fun tool ->
            { tool with Execute = fun input -> task { let! r = tool.Execute input in return clampText maxToolResultChars r } })


