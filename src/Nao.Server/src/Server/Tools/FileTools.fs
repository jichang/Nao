namespace Nao.Assistant

open System
open System.IO
open Nao.Agents

/// Workspace file tools: create folders, read/write/list/delete files. Every path is
/// resolved inside the current session's working directory (traversal-guarded), and large
/// content is kept on disk rather than flooded into the conversation.
module FileTools =

    /// Max characters read_file returns in one call (a page of a large file).
    let private maxReadWindowChars = 20000
    /// Max characters write_file accepts in a single call (use append for more).
    let private maxWriteChars = 200000

    let createFolder: Tool =
        { Tool.Create("create_folder", "Create a new folder. Input: JSON {\"path\":\"relative/folder\"}.",
            fun input -> task {
                let a = parseArgs input
                let path = resolvePath (a.StringOrRaw "path")
                Directory.CreateDirectory(path) |> ignore
                return json {| created = path.Replace("\\", "/"); exists = true |}
            }) with
            Schema = [ reqParam "path" "string" "Relative path of the folder to create." ] }

    let writeFile: Tool =
        { Tool.Create("write_file",
            "Write text to a workspace file. Input: JSON {\"path\":\"relative/path\",\"content\":\"...\",\"mode\":\"overwrite\"|\"append\"}. 'mode' defaults to overwrite. Content is written straight to disk; for very large files, build them up with several append calls instead of one huge write.",
            fun input -> task {
                let a = parseArgs input
                let pathRaw = a.StringOrRaw "path"
                if String.IsNullOrWhiteSpace pathRaw then return json {| error = "Expected a 'path'." |}
                else
                    let path = resolvePath pathRaw
                    let content = a.StringOr("content", "")
                    let isAppend = (a.StringOr("mode", "overwrite")).Trim().ToLowerInvariant() = "append"
                    if content.Length > maxWriteChars then
                        return json {| error = sprintf "Content too large (%d chars); write in smaller pieces using mode 'append'." content.Length
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
            }) with
            Schema =
                [ reqParam "path" "string" "Relative path of the file to write."
                  optParam "content" "string" (Some "") "Text content to write."
                  optParam "mode" "string" (Some "overwrite") "'overwrite' (default) replaces the file; 'append' adds to the end." ] }

    let readFile: Tool =
        { Tool.Create("read_file",
            "Read a text file's contents. Input: JSON {\"path\":\"name-or-path\",\"offset\":0,\"length\":20000}. 'offset'/'length' are optional and let you page through large files (returns up to ~20k chars per call). This is also how you read a file the user attached: pass the attachment's stored file name as 'path'. Attachments are NOT included in the conversation automatically — call this tool to read one only when you actually need its contents. Oversized files are truncated with a note showing the total size and the offset to continue from.",
            fun input -> task {
                let a = parseArgs input
                let nameOrPath = (a.StringOrRaw "path").Trim()
                let path = resolvePath nameOrPath
                if not (File.Exists path) then
                    return json {| error = sprintf "File not found: %s" nameOrPath |}
                else
                    let content = File.ReadAllText path
                    let total = content.Length
                    let offset =
                        match a.TryInt "offset" with
                        | Some n when n >= 0 -> min n total
                        | _ -> 0
                    let window =
                        match a.TryInt "length" with
                        | Some n when n > 0 -> min n maxReadWindowChars
                        | _ -> maxReadWindowChars
                    let take = min window (total - offset)
                    let slice = content.Substring(offset, take)
                    let nextOffset = offset + take
                    if nextOffset < total then
                        return slice + sprintf "\n…(showing chars %d–%d of %d; read again with offset %d to continue)"
                                            offset nextOffset total nextOffset
                    elif offset > 0 then
                        return slice + sprintf "\n…(showing chars %d–%d of %d; end of file)" offset nextOffset total
                    else
                        return slice
            }) with
            Schema =
                [ reqParam "path" "string" "File name or relative path to read (use an attachment's stored file name to read it)."
                  optParam "offset" "int" (Some "0") "Character offset to start reading from (for paging large files)."
                  optParam "length" "int" (Some "20000") "Maximum number of characters to return (capped at ~20k)." ] }

    let listFolder: Tool =
        { Tool.Create("list_folder", "List directory contents. Input: JSON {\"path\":\"relative/path\"} (omit or empty 'path' for the workspace root).",
            fun input -> task {
                let a = parseArgs input
                let rel = a.StringOrRaw "path"
                let path = if String.IsNullOrWhiteSpace(rel) then currentWorkDir () else resolvePath rel
                if not (Directory.Exists(path)) then
                    return json {| error = sprintf "Directory not found: %s" rel |}
                else
                    let entries =
                        Directory.GetFileSystemEntries(path)
                        |> Array.map (fun e ->
                            {| name = Path.GetFileName(e); ``type`` = (if Directory.Exists(e) then "dir" else "file") |})
                    return json {| path = path.Replace("\\", "/"); entries = entries |}
            }) with
            Schema = [ optParam "path" "string" None "Relative folder path to list (omit or empty for the workspace root)." ] }

    let delete: Tool =
        { Tool.Create("delete", "Delete a file or folder. Input: JSON {\"path\":\"relative/path\"}.",
            fun input -> task {
                let a = parseArgs input
                let rel = a.StringOrRaw "path"
                let path = resolvePath rel
                if File.Exists(path) then
                    File.Delete(path)
                    return json {| deleted = rel; ``type`` = "file" |}
                elif Directory.Exists(path) then
                    Directory.Delete(path, true)
                    return json {| deleted = rel; ``type`` = "dir" |}
                else
                    return json {| error = sprintf "Not found: %s" rel |}
            }) with
            Schema = [ reqParam "path" "string" "Relative path of the file or folder to delete." ] }
