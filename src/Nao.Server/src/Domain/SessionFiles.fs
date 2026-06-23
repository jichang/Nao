namespace Nao.Assistant

open System
open System.IO
open System.Collections.Generic
open System.Collections.Concurrent
open System.Text.Json

/// Per-session temporary file storage. Every session gets its own folder on disk that
/// holds the files a user uploads and the files tools/agents generate during a turn, so
/// the user can list and download them at any time. The descriptor index is persisted
/// alongside the bytes so the listing survives an app restart.
module SessionFiles =

    let private jsonOpts = JsonSerializerOptions(WriteIndented = true)

    /// Root that mirrors the app data directory (override with NAO_DATA_DIR), kept in
    /// sync with `Database.dataDir` without taking a compile dependency on it.
    let private baseRoot =
        let dataDir =
            match Environment.GetEnvironmentVariable("NAO_DATA_DIR") with
            | path when not (String.IsNullOrWhiteSpace path) -> path
            | _ -> Path.Combine(Environment.CurrentDirectory, ".nao-data")
        Path.Combine(dataDir, "sessions")

    /// Make a session key (e.g. "dev/75f1ff2b") safe to use as a folder name. The same
    /// mapping is mirrored by FileConversationStore so a session's conversations, files,
    /// observability and feedback all nest under the SAME sessions/<key>/ folder.
    let private sanitize (key: string) =
        key
        |> Seq.map (fun c -> if Char.IsLetterOrDigit c || c = '-' || c = '_' then c else '_')
        |> Seq.toArray
        |> String

    /// Absolute path to a session's data folder: <data>/sessions/<sanitized key>.
    /// Conversations, files, observability and feedback for the session all nest here.
    let sessionDir (key: string) = Path.Combine(baseRoot, sanitize key)

    /// File store scoped to a single session (grain key "userId/sessionId").
    type SessionFileStore internal (sessionKey: string) =
        let dir = sessionDir sessionKey
        let filesDir = Path.Combine(dir, "files")
        let indexPath = Path.Combine(dir, "files.json")
        let gate = obj ()

        let load () : List<SessionFileDto> =
            try
                if File.Exists indexPath then
                    let json = File.ReadAllText indexPath
                    match JsonSerializer.Deserialize<SessionFileDto[]>(json, jsonOpts) with
                    | null -> List<SessionFileDto>()
                    | arr -> List<SessionFileDto>(arr)
                else List<SessionFileDto>()
            with _ -> List<SessionFileDto>()

        let index = load ()

        let persist () =
            Directory.CreateDirectory dir |> ignore
            File.WriteAllText(indexPath, JsonSerializer.Serialize(index.ToArray(), jsonOpts))

        /// Resolve a stored file's path from its (relative) name, kept safely inside the
        /// files folder so a malicious name cannot escape the session via "..".
        let safeResolve (name: string) =
            let cleaned = (if isNull name then "" else name).Replace('\\', '/').TrimStart('/')
            let full = Path.GetFullPath(Path.Combine(filesDir, cleaned))
            let root = Path.GetFullPath(filesDir)
            if full = root || full.StartsWith(root + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
            then full
            else Path.Combine(filesDir, Path.GetFileName cleaned)

        /// Forward-slashed name of an on-disk file relative to the files folder.
        let relName (fullPath: string) =
            Path.GetRelativePath(filesDir, fullPath).Replace('\\', '/')

        /// True if a file with this (relative) name already exists, either tracked in the
        /// index or sitting on disk (both compared case-insensitively). Must hold the gate.
        let nameTaken (rel: string) =
            (index |> Seq.exists (fun d -> String.Equals(d.Name, rel, StringComparison.OrdinalIgnoreCase)))
            || File.Exists(safeResolve rel)

        /// Return a name that does not collide with an existing file by appending " (n)"
        /// before the extension (e.g. "report.pdf" -> "report (1).pdf"). Must hold the gate.
        let uniqueName (requested: string) =
            if not (nameTaken requested) then requested
            else
                let dirPart = (Path.GetDirectoryName requested |> Option.ofObj |> Option.defaultValue "").Replace('\\', '/')
                let stem = Path.GetFileNameWithoutExtension requested
                let ext = Path.GetExtension requested
                let build n =
                    let fileName = sprintf "%s (%d)%s" stem n ext
                    if String.IsNullOrEmpty dirPart then fileName else dirPart + "/" + fileName
                let mutable n = 1
                while nameTaken (build n) do n <- n + 1
                build n

        /// Best-effort media type from a file extension (for files written directly by tools).
        let guessMediaType (name: string) =
            match Path.GetExtension(name).ToLowerInvariant() with
            | ".md" | ".markdown" -> "text/markdown"
            | ".txt" | ".text" -> "text/plain"
            | ".html" | ".htm" -> "text/html"
            | ".pdf" -> "application/pdf"
            | ".json" -> "application/json"
            | ".csv" -> "text/csv"
            | ".docx" -> "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            | ".xlsx" -> "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            | ".pptx" -> "application/vnd.openxmlformats-officedocument.presentationml.presentation"
            | _ -> ""

        /// Reconcile the descriptor index with the files actually on disk: pick up files
        /// written directly by tools (not through Save), refresh sizes, and drop entries
        /// whose file was deleted. Ids/metadata are preserved by name. The files folder is
        /// the source of truth; the index is its descriptor view. Must hold the gate.
        let reconcile () =
            let onDisk =
                if Directory.Exists filesDir then
                    Directory.EnumerateFiles(filesDir, "*", SearchOption.AllDirectories)
                    |> Seq.map (fun f -> relName f, f)
                    |> List.ofSeq
                else []
            let byName =
                index
                |> Seq.map (fun d -> d.Name.Replace('\\', '/').TrimStart('/'), d)
                |> Seq.distinctBy fst
                |> dict
            let result = List<SessionFileDto>()
            let mutable changed = false
            for (name, full) in onDisk do
                let size = try (FileInfo full).Length with _ -> 0L
                match byName.TryGetValue name with
                | true, d ->
                    if d.Size <> size then changed <- true
                    result.Add(if d.Size <> size then { d with Size = size } else d)
                | _ ->
                    changed <- true
                    result.Add
                        { Id = Guid.NewGuid().ToString("N").[..11]
                          Name = name
                          MediaType = guessMediaType name
                          Size = size
                          Source = "generated"
                          TurnId = ""
                          CreatedAt = (try DateTimeOffset((FileInfo full).LastWriteTimeUtc) with _ -> DateTimeOffset.UtcNow) }
            let onDiskNames = onDisk |> List.map fst |> Set.ofList
            if index |> Seq.exists (fun d -> not (onDiskNames.Contains(d.Name.Replace('\\', '/').TrimStart('/')))) then
                changed <- true
            if changed then
                index.Clear()
                index.AddRange result
                persist ()

        /// The files folder backing this session — the unified working directory shared by
        /// uploads, tool output and generated files.
        member _.FilesDir = filesDir

        /// Save bytes into the session folder under their real name, returning the
        /// descriptor. Re-saving the same name keeps its id and creation time (so tools that
        /// rewrite a file overwrite it in place). Pass `ensureUnique = true` (used for user
        /// uploads) to instead keep an existing file and store the new one under a
        /// disambiguated name like "report (1).pdf".
        member _.Save(name: string, mediaType: string, source: string, turnId: string, bytes: byte[], ?ensureUnique: bool) : SessionFileDto =
            lock gate (fun () ->
                let requested =
                    let n = (if String.IsNullOrWhiteSpace name then "file" else name).Replace('\\', '/').TrimStart('/')
                    if String.IsNullOrWhiteSpace n then "file" else n
                let requested = if defaultArg ensureUnique false then uniqueName requested else requested
                let full = safeResolve requested
                Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
                File.WriteAllBytes(full, bytes)
                let stored = relName full
                let existing = index |> Seq.tryFind (fun d -> String.Equals(d.Name, stored, StringComparison.OrdinalIgnoreCase))
                let dto =
                    { Id = (match existing with Some d -> d.Id | None -> Guid.NewGuid().ToString("N").[..11])
                      Name = stored
                      MediaType = (if isNull mediaType then "" else mediaType)
                      Size = int64 bytes.Length
                      Source = source
                      TurnId = (if isNull turnId then "" else turnId)
                      CreatedAt = (match existing with Some d -> d.CreatedAt | None -> DateTimeOffset.UtcNow) }
                existing |> Option.iter (fun d -> index.Remove d |> ignore)
                index.Add dto
                persist ()
                dto)

        /// Save UTF-8 text content (convenience for uploaded text files). Pass
        /// `ensureUnique = true` to disambiguate a name that already exists instead of
        /// overwriting it.
        member this.SaveText(name: string, source: string, turnId: string, content: string, ?ensureUnique: bool) : SessionFileDto =
            this.Save(name, "text/plain", source, turnId, System.Text.Encoding.UTF8.GetBytes(content |> Option.ofObj |> Option.defaultValue ""), ?ensureUnique = ensureUnique)

        /// All stored files, newest first (reconciled with the files folder).
        member _.List() : SessionFileDto list =
            lock gate (fun () ->
                reconcile ()
                index |> Seq.sortByDescending (fun f -> f.CreatedAt) |> List.ofSeq)

        /// Resolve a stored file's descriptor by id.
        member _.TryGet(id: string) : SessionFileDto option =
            lock gate (fun () ->
                reconcile ()
                index |> Seq.tryFind (fun f -> f.Id = id))

        /// Read a stored file's descriptor + bytes by id.
        member this.TryOpen(id: string) : (SessionFileDto * byte[]) option =
            match this.TryGet id with
            | Some dto ->
                let path = safeResolve dto.Name
                if File.Exists path then Some(dto, File.ReadAllBytes path) else None
            | None -> None

    let private stores = ConcurrentDictionary<string, SessionFileStore>()

    /// Get (or create) the file store for a session key ("userId/sessionId").
    let forKey (sessionKey: string) : SessionFileStore =
        stores.GetOrAdd(sessionKey, fun k -> SessionFileStore(k))
