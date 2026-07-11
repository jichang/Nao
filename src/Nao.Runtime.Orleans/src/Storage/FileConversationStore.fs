namespace Nao.Runtime.Orleans

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks

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
/// so a session's conversations nest alongside its tasks, files, observability and feedback
/// under one folder, and a task-spawned sub-session's conversations nest beneath that task.
type FileConversationStore(baseDir: string) =

    let jsonOptions =
        let opts = JsonSerializerOptions(WriteIndented = true)
        opts.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        opts

    let gate = obj ()

    let sessionDir (sessionId: string) = SessionPaths.sessionDir baseDir sessionId
    let conversationsDir (sessionId: string) = Path.Combine(sessionDir sessionId, "conversations")
    let indexPath (sessionId: string) = Path.Combine(sessionDir sessionId, "conversations.json")

    /// Flatten a (possibly hierarchical) conversation name to a single folder id.
    let conversationId (conversationName: string) =
        let id = SessionPaths.sanitizeSegment conversationName
        if String.IsNullOrWhiteSpace id then "default" else id

    let conversationDir (sessionId: string) (conversationName: string) =
        Path.Combine(conversationsDir sessionId, conversationId conversationName)

    let messagesFile (sessionId: string) (conversationName: string) =
        Path.Combine(conversationDir sessionId conversationName, "messages.json")

    let metaFile (sessionId: string) (conversationName: string) =
        Path.Combine(conversationDir sessionId conversationName, "meta.json")

    let readMessages (sessionId: string) (conversationName: string) : PersistedMessage array =
        let path = messagesFile sessionId conversationName
        if File.Exists path then
            try
                match JsonSerializer.Deserialize<PersistedMessage array>(File.ReadAllText path, jsonOptions) with
                | null -> [||]
                | arr -> arr
            with _ -> [||]
        else [||]

    let writeMessages (sessionId: string) (conversationName: string) (messages: PersistedMessage array) =
        Directory.CreateDirectory(conversationDir sessionId conversationName) |> ignore
        File.WriteAllText(messagesFile sessionId conversationName, JsonSerializer.Serialize(messages, jsonOptions))

    let readMeta (path: string) =
        try Some(JsonSerializer.Deserialize<ConversationMeta>(File.ReadAllText path, jsonOptions))
        with _ -> None

    /// Rebuild the session-level conversations.json index from the per-conversation metas.
    let rebuildIndex (sessionId: string) =
        let dir = conversationsDir sessionId
        let metas =
            if Directory.Exists dir then
                Directory.GetDirectories(dir)
                |> Array.choose (fun d ->
                    let m = Path.Combine(d, "meta.json")
                    if File.Exists m then readMeta m else None)
                |> Array.sortBy (fun m -> m.CreatedAt)
            else [||]
        Directory.CreateDirectory(sessionDir sessionId) |> ignore
        File.WriteAllText(indexPath sessionId, JsonSerializer.Serialize(metas, jsonOptions))

    /// Write a conversation's meta.json (preserving its original CreatedAt) and refresh the index.
    let writeMeta (sessionId: string) (conversationName: string) (messageCount: int) =
        let metaPath = metaFile sessionId conversationName
        let existing = if File.Exists metaPath then readMeta metaPath else None
        let now = DateTimeOffset.UtcNow
        let meta =
            match existing with
            | Some m -> { m with LastMessageAt = now; MessageCount = messageCount }
            | None ->
                { SessionId = sessionId
                  ConversationName = conversationName
                  AgentName = ""
                  CreatedAt = now
                  LastMessageAt = now
                  MessageCount = messageCount }
        Directory.CreateDirectory(conversationDir sessionId conversationName) |> ignore
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, jsonOptions))
        rebuildIndex sessionId

    interface IConversationStore with
        member _.AppendAsync (sessionId: string) (conversationName: string) (messages: PersistedMessage array) =
            task {
                if messages.Length = 0 then return ()
                else
                    lock gate (fun () ->
                        let merged = Array.append (readMessages sessionId conversationName) messages
                        writeMessages sessionId conversationName merged
                        writeMeta sessionId conversationName merged.Length)
            }

        member _.SaveAsync (sessionId: string) (conversationName: string) (messages: PersistedMessage array) =
            task {
                lock gate (fun () ->
                    writeMessages sessionId conversationName messages
                    writeMeta sessionId conversationName messages.Length)
            }

        member _.LoadAsync (sessionId: string) (conversationName: string) =
            task { return readMessages sessionId conversationName }

        member _.ListConversationsAsync(sessionId: string) =
            task {
                let path = indexPath sessionId
                if File.Exists path then
                    return
                        (try
                            match JsonSerializer.Deserialize<ConversationMeta array>(File.ReadAllText path, jsonOptions) with
                            | null -> [||]
                            | arr -> arr
                         with _ -> [||])
                else
                    return Array.empty
            }

        member _.ListSessionsAsync() =
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

        member _.DeleteConversationAsync (sessionId: string) (conversationName: string) =
            task {
                lock gate (fun () ->
                    let dir = conversationDir sessionId conversationName
                    if Directory.Exists dir then Directory.Delete(dir, recursive = true)
                    rebuildIndex sessionId)
            }

        member _.DeleteSessionAsync(sessionId: string) =
            task {
                // Remove the entire session folder (conversations, tasks, files, observability
                // and feedback) so deleting a session leaves nothing behind under sessions/<key>/.
                let dir = sessionDir sessionId
                if Directory.Exists dir then Directory.Delete(dir, recursive = true)
            }
