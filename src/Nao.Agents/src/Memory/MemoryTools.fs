namespace Nao.Agents

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type MemoryRecallSource =
    | Memory
    | Knowledge
    | All

type KnowledgeMemoryRecallRequest =
    private
        { Query: string
          Intent: string
          Limit: int }

[<RequireQualifiedAccess>]
module KnowledgeMemoryRecallRequest =
    let query value = value.Query
    let intent value = value.Intent
    let limit value = value.Limit

type KnowledgeMemoryRecallEvidence =
    private
        { SourceId: string
          DocumentId: string
          DocumentVersionId: string
          ChunkId: string
          Uri: Uri
          ContentHash: string }

[<RequireQualifiedAccess>]
module KnowledgeMemoryRecallEvidence =
    let create sourceId documentId documentVersionId chunkId (uri: Uri) contentHash =
        let canonical name value =
            if String.IsNullOrWhiteSpace value || value <> value.Trim() then
                invalidArg name "Knowledge recall provenance values must be non-blank and trimmed."

            value

        if isNull uri || not uri.IsAbsoluteUri then
            invalidArg (nameof uri) "Knowledge recall provenance requires an absolute URI."

        { SourceId = canonical (nameof sourceId) sourceId
          DocumentId = canonical (nameof documentId) documentId
          DocumentVersionId = canonical (nameof documentVersionId) documentVersionId
          ChunkId = canonical (nameof chunkId) chunkId
          Uri = uri
          ContentHash = canonical (nameof contentHash) contentHash }

    let sourceId value = value.SourceId
    let documentId value = value.DocumentId
    let documentVersionId value = value.DocumentVersionId
    let chunkId value = value.ChunkId
    let uri value = value.Uri
    let contentHash value = value.ContentHash

type KnowledgeMemoryRecallEntry =
    private
        { Content: string
          Score: float
          Evidence: KnowledgeMemoryRecallEvidence }

[<RequireQualifiedAccess>]
module KnowledgeMemoryRecallEntry =
    let create content score evidence =
        if String.IsNullOrWhiteSpace content || content <> content.Trim() then
            invalidArg (nameof content) "Knowledge recall content must be non-blank and trimmed."

        if not (Double.IsFinite score) then
            invalidArg (nameof score) "Knowledge recall scores must be finite."

        if isNull (box evidence) then
            nullArg (nameof evidence)

        { Content = content
          Score = score
          Evidence = evidence }

    let content value = value.Content
    let score value = value.Score
    let evidence value = value.Evidence

type MemoryKnowledgeRecall =
    private
        { Permission: ResourceAccess
          RecallAsync:
              AuthorizationScope
                  -> KnowledgeMemoryRecallRequest
                  -> CancellationToken
                  -> Task<KnowledgeMemoryRecallEntry list> }

[<RequireQualifiedAccess>]
module MemoryKnowledgeRecall =
    let create permission recallAsync =
        match permission with
        | ResourceAccess.Knowledge(KnowledgeAction.Retrieve, _) -> ()
        | _ -> invalidArg (nameof permission) "Knowledge recall requires a concrete knowledge-retrieve permission."

        if isNull (box recallAsync) then
            nullArg (nameof recallAsync)

        { Permission = permission
          RecallAsync = recallAsync }

    let permission value = value.Permission

/// Host-owned policy for exposing deliberate memory access to an agent.
type MemoryToolConfig =
    { SearchEnabled: bool
      RememberEnabled: bool
      ForgetEnabled: bool
      MaxSearchResults: int
      MaxEntryChars: int }

    static member Default =
        { SearchEnabled = true
          RememberEnabled = true
          ForgetEnabled = false
          MaxSearchResults = 5
          MaxEntryChars = 2000 }

type private MemorySearchInput =
    { Query: string
      Intent: string
      Source: MemoryRecallSource
      MemoryTags: string list
      Limit: int option }

type private MemoryRememberInput =
    { Key: string
      Value: string
      Tags: string list }

type private MemoryForgetInput =
    { Key: string
      Reason: string
      ConfirmedByUser: bool }

module private MemoryToolJson =
    let private parseObject (value: string) read =
        try
            use document = JsonDocument.Parse value

            if document.RootElement.ValueKind <> JsonValueKind.Object then
                Error "$input must be an object."
            else
                read document.RootElement
        with :? JsonException as ex ->
            Error(sprintf "$input must be valid JSON: %s" ex.Message)

    let private requiredString (name: string) (root: JsonElement) =
        match root.TryGetProperty name with
        | true, value when
            value.ValueKind = JsonValueKind.String
            && not (String.IsNullOrWhiteSpace(value.GetString()))
            ->
            Ok(value.GetString().Trim())
        | _ -> Error(sprintf "$input.%s must be a non-empty string." name)

    let private optionalMemoryTags (root: JsonElement) =
        match root.TryGetProperty "memoryTags" with
        | false, _ -> Ok []
        | true, value when value.ValueKind = JsonValueKind.Array ->
            let values = value.EnumerateArray() |> Seq.toList

            if values |> List.forall (fun item -> item.ValueKind = JsonValueKind.String) then
                values
                |> List.map (fun item -> item.GetString().Trim())
                |> List.filter (String.IsNullOrWhiteSpace >> not)
                |> Ok
            else
                Error "$input.memoryTags must contain only strings."
        | _ -> Error "$input.memoryTags must be an array of strings."

    let private optionalTags (root: JsonElement) =
        match root.TryGetProperty "tags" with
        | false, _ -> Ok []
        | true, value when value.ValueKind = JsonValueKind.Array ->
            let values = value.EnumerateArray() |> Seq.toList

            if values |> List.forall (fun item -> item.ValueKind = JsonValueKind.String) then
                values
                |> List.map (fun item -> item.GetString().Trim())
                |> List.filter (String.IsNullOrWhiteSpace >> not)
                |> Ok
            else
                Error "$input.tags must contain only strings."
        | _ -> Error "$input.tags must be an array of strings."

    let private recallSource (root: JsonElement) =
        match requiredString "source" root with
        | Ok "memory" -> Ok MemoryRecallSource.Memory
        | Ok "knowledge" -> Ok MemoryRecallSource.Knowledge
        | Ok "all" -> Ok MemoryRecallSource.All
        | Ok _ -> Error "$input.source must be \"memory\", \"knowledge\", or \"all\"."
        | Error error -> Error error

    let private requiredBoolean (name: string) (root: JsonElement) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.True -> Ok true
        | true, value when value.ValueKind = JsonValueKind.False -> Ok false
        | _ -> Error(sprintf "$input.%s must be a boolean." name)

    let decodeSearch value =
        parseObject value (fun root ->
            match
                requiredString "query" root, requiredString "intent" root, recallSource root, optionalMemoryTags root
            with
            | Ok query, Ok intent, Ok source, Ok memoryTags ->
                match root.TryGetProperty "limit" with
                | false, _ ->
                    Ok
                        { Query = query
                          Intent = intent
                          Source = source
                          MemoryTags = memoryTags
                          Limit = None }
                | true, limit when limit.ValueKind = JsonValueKind.Number ->
                    match limit.TryGetInt32() with
                    | true, count when count > 0 ->
                        Ok
                            { Query = query
                              Intent = intent
                              Source = source
                              MemoryTags = memoryTags
                              Limit = Some count }
                    | _ -> Error "$input.limit must be a positive integer."
                | _ -> Error "$input.limit must be a positive integer."
            | Error error, _, _, _
            | _, Error error, _, _
            | _, _, Error error, _
            | _, _, _, Error error -> Error error)

    let decodeRemember value =
        parseObject value (fun root ->
            match requiredString "key" root, requiredString "value" root, optionalTags root with
            | Ok key, Ok memoryValue, Ok tags ->
                Ok
                    { Key = key
                      Value = memoryValue
                      Tags = tags }
            | Error error, _, _
            | _, Error error, _
            | _, _, Error error -> Error error)

    let decodeForget value =
        parseObject value (fun root ->
            match requiredString "key" root, requiredString "reason" root, requiredBoolean "confirmedByUser" root with
            | Ok key, Ok reason, Ok confirmed ->
                Ok
                    { Key = key
                      Reason = reason
                      ConfirmedByUser = confirmed }
            | Error error, _, _
            | _, Error error, _
            | _, _, Error error -> Error error)

    let encodeSearchResult (intent: string) (memories: MemoryEntry list) (knowledge: KnowledgeMemoryRecallEntry list) =
        try
            use stream = new MemoryStream()
            use writer = new Utf8JsonWriter(stream)
            writer.WriteStartObject()
            writer.WriteString("intent", intent)
            writer.WriteStartArray("memories")

            for entry in memories do
                writer.WriteStartObject()
                writer.WriteString("key", entry.Key)
                writer.WriteString("value", entry.Value)
                writer.WriteString("timestamp", entry.Timestamp)
                writer.WriteStartArray("tags")

                for tag in entry.Tags do
                    writer.WriteStringValue tag

                writer.WriteEndArray()
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteStartArray("knowledge")

            for entry in knowledge do
                let evidence = KnowledgeMemoryRecallEntry.evidence entry
                writer.WriteStartObject()
                writer.WriteString("content", KnowledgeMemoryRecallEntry.content entry)
                writer.WriteNumber("score", KnowledgeMemoryRecallEntry.score entry)
                writer.WriteStartObject("provenance")
                writer.WriteString("sourceId", KnowledgeMemoryRecallEvidence.sourceId evidence)
                writer.WriteString("documentId", KnowledgeMemoryRecallEvidence.documentId evidence)
                writer.WriteString("documentVersionId", KnowledgeMemoryRecallEvidence.documentVersionId evidence)
                writer.WriteString("chunkId", KnowledgeMemoryRecallEvidence.chunkId evidence)
                writer.WriteString("uri", (KnowledgeMemoryRecallEvidence.uri evidence).AbsoluteUri)
                writer.WriteString("contentHash", KnowledgeMemoryRecallEvidence.contentHash evidence)
                writer.WriteEndObject()
                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteEndObject()
            writer.Flush()
            Ok(Encoding.UTF8.GetString(stream.ToArray()))
        with ex ->
            Error ex.Message

    let encodeRemembered (key: string) =
        try
            use stream = new MemoryStream()
            use writer = new Utf8JsonWriter(stream)
            writer.WriteStartObject()
            writer.WriteString("key", key)
            writer.WriteEndObject()
            writer.Flush()
            Ok(Encoding.UTF8.GetString(stream.ToArray()))
        with ex ->
            Error ex.Message

module private MemorySearch =
    let private words (value: string) =
        Text.RegularExpressions.Regex.Split(value.ToLowerInvariant(), "[^a-z0-9]+")
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> Set.ofSeq

    let rank (input: MemorySearchInput) (entry: MemoryEntry) =
        let wanted = words (input.Query + " " + input.Intent)
        let keyScore = Set.intersect wanted (words entry.Key) |> Set.count |> (*) 4

        let tagScore =
            Set.intersect wanted (entry.Tags |> String.concat " " |> words)
            |> Set.count
            |> (*) 2

        let valueScore = Set.intersect wanted (words entry.Value) |> Set.count
        keyScore + tagScore + valueScore

module private MemoryTool =
    let search (config: MemoryToolConfig) (store: MemoryStore) (knowledgeRecall: MemoryKnowledgeRecall option) =
        let input =
            ToolCodec.create
                "object\n  - query (required): string - Concrete words, names, or facts to find.\n  - intent (required): string - Why prior information is needed for the current task.\n  - source (required): \"memory\", \"knowledge\", or \"all\" - Select learned memory, source-backed knowledge, or both without merging their semantics.\n  - memoryTags (optional): string[] - Restrict only learned-memory results to entries carrying at least one tag.\n  - limit (optional): positive integer - Per-source maximum results, bounded by host policy."
                (fun _ -> Error "Tool inputs are not encoded by the runtime.")
                MemoryToolJson.decodeSearch

        let output =
            ToolCodec.create
                "object\n  - intent (required): string\n  - memories (required): learned-memory entries { key, value, timestamp, tags }\n  - knowledge (required): source-backed passages { content, score, provenance }"
                (fun (intent, memories, knowledge) -> MemoryToolJson.encodeSearchResult intent memories knowledge)
                (fun _ -> Error "Tool outputs are not decoded by the runtime.")

        let operation =
            ToolOperation.create ToolReplaySafety.RetrySafe (fun context (search: MemorySearchInput) ->
                task {
                    let authorization = AgentContext.requireAuthorization context

                    let limit =
                        min config.MaxSearchResults (defaultArg search.Limit config.MaxSearchResults)

                    let! memories =
                        task {
                            match search.Source with
                            | MemoryRecallSource.Knowledge -> return []
                            | MemoryRecallSource.Memory
                            | MemoryRecallSource.All ->
                                let! all = store.RecallAllAsync authorization

                                return
                                    all
                                    |> List.filter (fun entry ->
                                        search.MemoryTags.IsEmpty
                                        || entry.Tags
                                           |> List.exists (fun tag ->
                                               search.MemoryTags
                                               |> List.exists (fun requested ->
                                                   String.Equals(tag, requested, StringComparison.OrdinalIgnoreCase))))
                                    |> List.map (fun entry -> entry, MemorySearch.rank search entry)
                                    |> List.filter (fun (_, score) -> score > 0)
                                    |> List.sortByDescending (fun (entry, score) -> score, entry.Timestamp)
                                    |> List.truncate limit
                                    |> List.map (fun (entry, _) ->
                                        let value =
                                            if entry.Value.Length <= config.MaxEntryChars then
                                                entry.Value
                                            else
                                                entry.Value.Substring(0, config.MaxEntryChars)

                                        { entry with Value = value })
                        }

                    let! knowledge =
                        task {
                            match search.Source, knowledgeRecall with
                            | MemoryRecallSource.Memory, _ -> return Ok []
                            | (MemoryRecallSource.Knowledge | MemoryRecallSource.All), None ->
                                return
                                    Error(
                                        ToolExecError.InvalidInput
                                            "Source-backed knowledge recall is not configured for this memory tool."
                                    )
                            | (MemoryRecallSource.Knowledge | MemoryRecallSource.All), Some recall ->
                                let request =
                                    { Query = search.Query
                                      Intent = search.Intent
                                      Limit = limit }

                                let! entries = recall.RecallAsync authorization request context.CancellationToken

                                if isNull (box entries) then
                                    return
                                        Error(
                                            ToolExecError.Failed
                                                "The knowledge recall capability returned null results."
                                        )
                                elif entries.Length > limit then
                                    return
                                        Error(
                                            ToolExecError.Failed
                                                "The knowledge recall capability exceeded the requested result limit."
                                        )
                                else
                                    return
                                        entries
                                        |> List.map (fun entry ->
                                            let content = KnowledgeMemoryRecallEntry.content entry

                                            if content.Length <= config.MaxEntryChars then
                                                entry
                                            else
                                                { entry with
                                                    Content = content.Substring(0, config.MaxEntryChars) })
                                        |> Ok
                        }

                    return knowledge |> Result.map (fun passages -> search.Intent, memories, passages)
                })

        let permissions =
            [ ResourceAccess.memory "durable" MemoryAction.Read ]
            @ (knowledgeRecall
               |> Option.map (MemoryKnowledgeRecall.permission >> List.singleton)
               |> Option.defaultValue [])

        Tool.create
            "memory_search"
            "Deliberately search learned durable memory, source-backed knowledge, or both. Results remain separated by origin; knowledge passages carry provenance and are never writable or forgettable through memory operations."
            1000
            permissions
            input
            output
            operation

    let remember (store: MemoryStore) =
        let input =
            ToolCodec.create
                "object\n  - key (required): string - Stable descriptive identifier.\n  - value (required): string - Durable fact, preference, or decision to retain.\n  - tags (optional): string[] - Retrieval classifications."
                (fun _ -> Error "Tool inputs are not encoded by the runtime.")
                MemoryToolJson.decodeRemember

        let output =
            ToolCodec.create
                "object\n  - key (required): string - Stored memory key."
                MemoryToolJson.encodeRemembered
                (fun _ -> Error "Tool outputs are not decoded by the runtime.")

        let operation =
            ToolOperation.create ToolReplaySafety.ExternallySideEffecting (fun context (memory: MemoryRememberInput) ->
                task {
                    let authorization = AgentContext.requireAuthorization context

                    let entry =
                        { Key = memory.Key
                          Value = memory.Value
                          Timestamp = DateTimeOffset.UtcNow
                          Tags = memory.Tags }

                    do! store.SaveAsync authorization entry
                    return Ok memory.Key
                })

        Tool.create
            "memory_remember"
            "Save a durable session fact, preference, or decision when the user explicitly asks to remember it or it will clearly matter in later work; do not store transient task details."
            1000
            [ ResourceAccess.memory "durable" MemoryAction.Write ]
            input
            output
            operation

    let forget (store: MemoryStore) =
        let input =
            ToolCodec.create
                "object\n  - key (required): string - Exact stored key to delete.\n  - reason (required): string - Why deletion is requested.\n  - confirmedByUser (required): boolean - Must be true only when the user explicitly requested this deletion."
                (fun _ -> Error "Tool inputs are not encoded by the runtime.")
                MemoryToolJson.decodeForget

        let output =
            ToolCodec.create
                "object\n  - key (required): string - Deleted memory key."
                MemoryToolJson.encodeRemembered
                (fun _ -> Error "Tool outputs are not decoded by the runtime.")

        let operation =
            ToolOperation.create ToolReplaySafety.ExternallySideEffecting (fun context (request: MemoryForgetInput) ->
                task {
                    let authorization = AgentContext.requireAuthorization context

                    if not request.ConfirmedByUser then
                        return Error(ToolExecError.InvalidInput "Memory deletion requires explicit user confirmation.")
                    else
                        do! store.ForgetAsync authorization request.Key
                        return Ok request.Key
                })

        Tool.create
            "memory_forget"
            "Delete one durable memory by exact key only when the user explicitly requested forgetting it."
            1000
            [ ResourceAccess.memory "durable" MemoryAction.Delete ]
            input
            output
            operation

[<RequireQualifiedAccess>]
module MemoryTools =
    let names =
        Set.ofList [ "memory"; "memory_search"; "memory_remember"; "memory_forget" ]

    let isMemoryTool (tool: Tool) = names.Contains tool.Name

    /// Creates tools that derive ownership from each invocation's authenticated context.
    let create
        (config: MemoryToolConfig)
        (store: MemoryStore)
        (knowledgeRecall: MemoryKnowledgeRecall option)
        : Tool list =
        [ if config.SearchEnabled then
              MemoryTool.search config store knowledgeRecall
          if config.RememberEnabled then
              MemoryTool.remember store
          if config.ForgetEnabled then
              MemoryTool.forget store ]
