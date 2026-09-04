namespace Nao.Agents

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks

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
      Tags: string list
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

    let private requiredBoolean (name: string) (root: JsonElement) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.True -> Ok true
        | true, value when value.ValueKind = JsonValueKind.False -> Ok false
        | _ -> Error(sprintf "$input.%s must be a boolean." name)

    let decodeSearch value =
        parseObject value (fun root ->
            match requiredString "query" root, requiredString "intent" root, optionalTags root with
            | Ok query, Ok intent, Ok tags ->
                match root.TryGetProperty "limit" with
                | false, _ ->
                    Ok
                        { Query = query
                          Intent = intent
                          Tags = tags
                          Limit = None }
                | true, limit when limit.ValueKind = JsonValueKind.Number ->
                    match limit.TryGetInt32() with
                    | true, count when count > 0 ->
                        Ok
                            { Query = query
                              Intent = intent
                              Tags = tags
                              Limit = Some count }
                    | _ -> Error "$input.limit must be a positive integer."
                | _ -> Error "$input.limit must be a positive integer."
            | Error error, _, _
            | _, Error error, _
            | _, _, Error error -> Error error)

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

    let encodeSearchResult (intent: string) (entries: MemoryEntry list) =
        try
            use stream = new MemoryStream()
            use writer = new Utf8JsonWriter(stream)
            writer.WriteStartObject()
            writer.WriteString("intent", intent)
            writer.WriteStartArray("entries")

            for entry in entries do
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
    let search (config: MemoryToolConfig) (store: MemoryStore) (owner: unit -> string) =
        let input =
            ToolCodec.create
                "object\n  - query (required): string - Concrete words, names, or facts to find.\n  - intent (required): string - Why this prior information is needed for the current task.\n  - tags (optional): string[] - Restrict results to memories carrying at least one tag.\n  - limit (optional): positive integer - Maximum results, bounded by host policy."
                (fun _ -> Error "Tool inputs are not encoded by the runtime.")
                MemoryToolJson.decodeSearch

        let output =
            ToolCodec.create
                "object\n  - intent (required): string\n  - entries (required): array of { key, value, timestamp, tags }"
                (fun (intent, entries) -> MemoryToolJson.encodeSearchResult intent entries)
                (fun _ -> Error "Tool outputs are not decoded by the runtime.")

        let operation =
            ToolOperation.create (fun _ (search: MemorySearchInput) ->
                task {
                    let! all = store.RecallAllAsync(owner ())

                    let filtered =
                        if search.Tags.IsEmpty then
                            all
                        else
                            all
                            |> List.filter (fun entry ->
                                entry.Tags
                                |> List.exists (fun tag ->
                                    search.Tags
                                    |> List.exists (fun requested ->
                                        String.Equals(tag, requested, StringComparison.OrdinalIgnoreCase))))

                    let limit =
                        min config.MaxSearchResults (defaultArg search.Limit config.MaxSearchResults)

                    let results =
                        filtered
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

                    return Ok(search.Intent, results)
                })

        Tool.create
            "memory_search"
            "Deliberately search durable session memory when the request depends on prior preferences, decisions, people, projects, or earlier work not present in the conversation."
            1000
            []
            input
            output
            operation

    let remember (store: MemoryStore) (owner: unit -> string) =
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
            ToolOperation.create (fun _ (memory: MemoryRememberInput) ->
                task {
                    let entry =
                        { Key = memory.Key
                          Value = memory.Value
                          Timestamp = DateTimeOffset.UtcNow
                          Tags = memory.Tags }

                    do! store.SaveAsync (owner ()) entry
                    return Ok memory.Key
                })

        Tool.create
            "memory_remember"
            "Save a durable session fact, preference, or decision when the user explicitly asks to remember it or it will clearly matter in later work; do not store transient task details."
            1000
            []
            input
            output
            operation

    let forget (store: MemoryStore) (owner: unit -> string) =
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
            ToolOperation.create (fun _ (request: MemoryForgetInput) ->
                task {
                    if not request.ConfirmedByUser then
                        return Error(ToolExecError.InvalidInput "Memory deletion requires explicit user confirmation.")
                    else
                        do! store.ForgetAsync (owner ()) request.Key
                        return Ok request.Key
                })

        Tool.create
            "memory_forget"
            "Delete one durable memory by exact key only when the user explicitly requested forgetting it."
            1000
            []
            input
            output
            operation

[<RequireQualifiedAccess>]
module MemoryTools =
    let names =
        Set.ofList [ "memory"; "memory_search"; "memory_remember"; "memory_forget" ]

    let isMemoryTool (tool: Tool) = names.Contains tool.Name

    /// Creates the memory tools enabled by host policy for one runtime owner scope.
    let create (config: MemoryToolConfig) (store: MemoryStore) (owner: unit -> string) : Tool list =
        [ if config.SearchEnabled then
              MemoryTool.search config store owner
          if config.RememberEnabled then
              MemoryTool.remember store owner
          if config.ForgetEnabled then
              MemoryTool.forget store owner ]
