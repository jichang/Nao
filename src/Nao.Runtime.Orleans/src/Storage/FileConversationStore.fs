namespace Nao.Runtime.Orleans

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open FSharp.SystemTextJson

/// File-based conversation store.
/// Layout:
///   {baseDir}/
///     {sessionDir}/
///       conversations.json                  — index: every conversation's metadata
///       conversations/
///         {conversationId}/
///           messages.json                   — the full message history (JSON array)
///           meta.json                       — conversation-level metadata
///
/// A conversation's id is its (possibly hierarchical) name flattened to a single safe
/// folder name, so sub-agent delegations — named "parent/child-turn-n" — each get their
/// own folder listed in the index rather than nesting physically. The full name is kept in
/// each meta's `ConversationName`.
///
/// {baseDir} is the shared `sessions/` root and paths resolve through `SessionPaths.sessionDir`,
/// so a session's conversations nest alongside its files, observability and feedback under
/// one folder.
module FileConversationStore =

    [<Literal>]
    let private CurrentSchemaVersion = 1

    let jsonOptions =
        let opts = JsonSerializerOptions(WriteIndented = false)
        opts.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull

        opts.Converters.Add(
            JsonFSharpConverter(JsonUnionEncoding.InternalTag ||| JsonUnionEncoding.UnwrapFieldlessTags)
        )

        opts

    let private sessionDir (baseDir: string) (sessionId: string) =
        SessionPaths.sessionDir baseDir sessionId

    let private conversationsDir baseDir (sessionId: string) =
        Path.Combine(sessionDir baseDir sessionId, "conversations")

    let private indexPath baseDir (sessionId: string) =
        Path.Combine(sessionDir baseDir sessionId, "conversations.json")

    /// Flatten a (possibly hierarchical) conversation name to a single folder id.
    let conversationId (conversationName: string) =
        let id = SessionPaths.sanitizeSegment conversationName
        if String.IsNullOrWhiteSpace id then "default" else id

    let private conversationDir baseDir (sessionId: string) (conversationName: string) =
        Path.Combine(conversationsDir baseDir sessionId, conversationId conversationName)

    let private messagesFile baseDir (sessionId: string) (conversationName: string) =
        Path.Combine(conversationDir baseDir sessionId conversationName, "messages.json")

    let private metaFile baseDir (sessionId: string) (conversationName: string) =
        Path.Combine(conversationDir baseDir sessionId conversationName, "meta.json")

    let private deserializeDocument<'value> kind path =
        try
            use document = JsonDocument.Parse(File.ReadAllText path)
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                raise (JsonException("The document must be a JSON object."))

            let version = root.GetProperty("schemaVersion").GetInt32()

            if version <> CurrentSchemaVersion then
                raise (
                    InvalidDataException(
                        sprintf
                            "%s at '%s' uses unsupported schema version %d; expected %d. Follow docs/migrations before writing."
                            kind
                            path
                            version
                            CurrentSchemaVersion
                    )
                )

            root.GetProperty("value").Deserialize<'value>(jsonOptions)
        with
        | :? InvalidDataException -> reraise ()
        | ex ->
            raise (
                InvalidDataException(
                    sprintf
                        "%s at '%s' is invalid. Restore or remove the session data by following docs/migrations before writing."
                        kind
                        path,
                    ex
                )
            )

    let private serializeDocument value =
        use stream = new MemoryStream()

        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", CurrentSchemaVersion)
        writer.WritePropertyName("value")
        JsonSerializer.Serialize(writer, value, jsonOptions)
        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    let private readMessages baseDir (sessionId: string) (conversationName: string) : PersistedMessage array =
        let path = messagesFile baseDir sessionId conversationName

        if File.Exists path then
            deserializeDocument<PersistedMessage array> "Conversation messages" path
        else
            [||]

    let private writeMessages
        baseDir
        (sessionId: string)
        (conversationName: string)
        (messages: PersistedMessage array)
        =
        Directory.CreateDirectory(conversationDir baseDir sessionId conversationName)
        |> ignore

        File.WriteAllText(messagesFile baseDir sessionId conversationName, serializeDocument messages)

    let readMeta (path: string) =
        Some(deserializeDocument<ConversationMeta> "Conversation metadata" path)

    let private readIndex baseDir sessionId =
        let path = indexPath baseDir sessionId

        if File.Exists path then
            deserializeDocument<ConversationMeta array> "Conversation index" path
        else
            Array.empty

    let private readMetas baseDir sessionId =
        let dir = conversationsDir baseDir sessionId

        if Directory.Exists dir then
            Directory.GetDirectories(dir)
            |> Array.choose (fun directory ->
                let path = Path.Combine(directory, "meta.json")
                if File.Exists path then readMeta path else None)
        else
            Array.empty

    let private preflightSession baseDir sessionId =
        readMetas baseDir sessionId |> ignore
        readIndex baseDir sessionId |> ignore

    /// Rebuild the session-level conversations.json index from the per-conversation metas.
    let private rebuildIndex baseDir (sessionId: string) =
        let metas = readMetas baseDir sessionId |> Array.sortBy (fun meta -> meta.CreatedAt)

        Directory.CreateDirectory(sessionDir baseDir sessionId) |> ignore
        File.WriteAllText(indexPath baseDir sessionId, serializeDocument metas)

    /// Write a conversation's meta.json (preserving its original CreatedAt) and refresh the index.
    let private writeMeta baseDir (sessionId: string) (conversationName: string) (messageCount: int) =
        let metaPath = metaFile baseDir sessionId conversationName
        let existing = if File.Exists metaPath then readMeta metaPath else None
        let now = DateTimeOffset.UtcNow

        let meta =
            match existing with
            | Some m ->
                { m with
                    LastMessageAt = now
                    MessageCount = messageCount }
            | None ->
                { SessionId = sessionId
                  ConversationName = conversationName
                  AgentName = ""
                  CreatedAt = now
                  LastMessageAt = now
                  MessageCount = messageCount }

        Directory.CreateDirectory(conversationDir baseDir sessionId conversationName)
        |> ignore

        File.WriteAllText(metaPath, serializeDocument meta)
        rebuildIndex baseDir sessionId

    let create (baseDir: string) : ConversationStore =
        let gate = obj ()

        let appendAsync (sessionId: string) (conversationName: string) (messages: PersistedMessage array) =
            task {
                if messages.Length = 0 then
                    return ()
                else
                    lock gate (fun () ->
                        let merged = Array.append (readMessages baseDir sessionId conversationName) messages
                        preflightSession baseDir sessionId
                        writeMessages baseDir sessionId conversationName merged
                        writeMeta baseDir sessionId conversationName merged.Length)
            }
            :> Task

        let saveAsync (sessionId: string) (conversationName: string) (messages: PersistedMessage array) =
            task {
                lock gate (fun () ->
                    readMessages baseDir sessionId conversationName |> ignore
                    preflightSession baseDir sessionId
                    writeMessages baseDir sessionId conversationName messages
                    writeMeta baseDir sessionId conversationName messages.Length)
            }
            :> Task

        let loadAsync (sessionId: string) (conversationName: string) =
            task { return readMessages baseDir sessionId conversationName }

        let loadByExecutionAsync executionId =
            task {
                if Directory.Exists baseDir then
                    return
                        Directory.GetFiles(baseDir, "messages.json", SearchOption.AllDirectories)
                        |> Array.collect (deserializeDocument<PersistedMessage array> "Conversation messages")
                        |> Array.filter (fun message -> message.Correlation.ExecutionId = executionId)
                        |> Array.sortBy _.Timestamp
                else
                    return [||]
            }

        let listConversationsAsync (sessionId: string) =
            task {
                let path = indexPath baseDir sessionId

                if File.Exists path then
                    return readIndex baseDir sessionId
                else
                    return Array.empty
            }

        let listSessionsAsync () =
            task {
                if Directory.Exists baseDir then
                    return
                        Directory.GetDirectories(baseDir)
                        // Only top-level sessions that actually hold a conversation; a folder may
                        // exist for files/observability before a first message is ever sent.
                        |> Array.filter (fun d ->
                            File.Exists(Path.Combine(d, "conversations.json"))
                            || Directory.Exists(Path.Combine(d, "conversations")))
                        |> Array.map Path.GetFileName
                else
                    return Array.empty
            }

        let deleteConversationAsync (sessionId: string) (conversationName: string) =
            task {
                lock gate (fun () ->
                    let dir = conversationDir baseDir sessionId conversationName

                    if Directory.Exists dir then
                        Directory.Delete(dir, recursive = true)

                    rebuildIndex baseDir sessionId)
            }
            :> Task

        let deleteSessionAsync (sessionId: string) =
            task {
                // Remove the entire session folder (conversations, tasks, files, observability
                // and feedback) so deleting a session leaves nothing behind under sessions/<key>/.
                let dir = sessionDir baseDir sessionId

                if Directory.Exists dir then
                    Directory.Delete(dir, recursive = true)
            }
            :> Task

        { AppendAsync = appendAsync
          SaveAsync = saveAsync
          LoadAsync = loadAsync
          LoadByExecutionAsync = loadByExecutionAsync
          ListConversationsAsync = listConversationsAsync
          ListSessionsAsync = listSessionsAsync
          DeleteConversationAsync = deleteConversationAsync
          DeleteSessionAsync = deleteSessionAsync }
