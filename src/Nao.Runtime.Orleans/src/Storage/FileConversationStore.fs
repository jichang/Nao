namespace Nao.Runtime.Orleans

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open FSharp.SystemTextJson
open Nao.Agents

[<RequireQualifiedAccess>]
type ConversationStoreEvent =
    | Saved of conversationName: string * messages: PersistedMessage array * recordedAt: DateTimeOffset
    | Appended of conversationName: string * messages: PersistedMessage array * recordedAt: DateTimeOffset
    | ConversationDeleted of conversationName: string
    | DeleteScope

type ConversationStoreDocument =
    { Version: int
      Sequence: int64
      Owner: PersistedOwnerScope
      Event: ConversationStoreEvent }

type ConversationEventStore =
    { Append: int64 -> string -> unit
      LoadAll: unit -> string list }

exception ConversationSequenceConflictException of expected: int64 * actual: int64

type private ConversationProjection =
    { Meta: ConversationMeta
      Messages: PersistedMessage array }

module FileConversationStore =
    let private jsonOptions =
        let options = JsonSerializerOptions(WriteIndented = false)
        options.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull

        options.Converters.Add(
            JsonFSharpConverter(JsonUnionEncoding.InternalTag ||| JsonUnionEncoding.UnwrapFieldlessTags)
        )

        options

    let private serialize value =
        JsonSerializer.Serialize(value, jsonOptions)

    let private decode (path: string) expectedOwner eventNumber (line: string) =
        try
            let document =
                JsonSerializer.Deserialize<ConversationStoreDocument>(line, jsonOptions)

            if
                isNull (box document)
                || document.Version <> 1
                || document.Sequence <> int64 eventNumber
                || document.Owner <> expectedOwner
            then
                raise (JsonException("Expected conversation-store schema version 1."))

            document.Event
        with ex ->
            raise (
                InvalidDataException(
                    sprintf
                        "Conversation stream '%s' is invalid at event %d. Follow docs/migrations before writing."
                        path
                        eventNumber,
                    ex
                )
            )

    let private streamPath baseDir scope =
        let key = AuthorizationScope.identity scope |> AuthorizationIdentity.storageKey
        Path.Combine(baseDir, "conversation", key + ".jsonl")

    let private applyEvent (conversations: Dictionary<string, ConversationProjection>) (event: ConversationStoreEvent) =
        let save (conversationName: string) (messages: PersistedMessage array) (recordedAt: DateTimeOffset) =
            let createdAt =
                match conversations.TryGetValue conversationName with
                | true, conversation -> conversation.Meta.CreatedAt
                | false, _ -> recordedAt

            conversations.[conversationName] <-
                { Meta =
                    { ConversationName = conversationName
                      AgentName = ""
                      CreatedAt = createdAt
                      LastMessageAt = recordedAt
                      MessageCount = messages.Length }
                  Messages = messages }

        match event with
        | ConversationStoreEvent.Saved(conversationName, messages, recordedAt) ->
            save conversationName messages recordedAt
        | ConversationStoreEvent.Appended(conversationName, messages, recordedAt) ->
            let existing =
                match conversations.TryGetValue conversationName with
                | true, conversation -> conversation.Messages
                | false, _ -> Array.empty

            save conversationName (Array.append existing messages) recordedAt
        | ConversationStoreEvent.ConversationDeleted conversationName -> conversations.Remove conversationName |> ignore
        | ConversationStoreEvent.DeleteScope -> conversations.Clear()

    let private load context owner (store: ConversationEventStore) =
        let conversations = Dictionary<string, ConversationProjection>()
        let lines = store.LoadAll()

        lines
        |> List.iteri (fun index line -> decode context owner (index + 1) line |> applyEvent conversations)

        conversations, int64 lines.Length

    let createPersistent (openStore: AuthorizationScope -> ConversationEventStore) : ConversationStore =
        let gate = obj ()

        let mutate scope event =
            lock gate (fun () ->
                let key = AuthorizationScope.identity scope |> AuthorizationIdentity.storageKey
                let context = "conversation/" + key
                let store = openStore scope
                let owner = PersistedOwnerScope.fromAuthorization scope
                let _, sequence = load context owner store

                let document: ConversationStoreDocument =
                    { Version = 1
                      Sequence = sequence + 1L
                      Owner = owner
                      Event = event }

                store.Append sequence (serialize document))

        let read scope operation =
            lock gate (fun () ->
                let key = AuthorizationScope.identity scope |> AuthorizationIdentity.storageKey

                load ("conversation/" + key) (PersistedOwnerScope.fromAuthorization scope) (openStore scope)
                |> fst
                |> operation)

        { AppendAsync =
            fun scope conversationName messages ->
                task {
                    if messages.Length > 0 then
                        mutate
                            scope
                            (ConversationStoreEvent.Appended(conversationName, messages, DateTimeOffset.UtcNow))
                }
          SaveAsync =
            fun scope conversationName messages ->
                task { mutate scope (ConversationStoreEvent.Saved(conversationName, messages, DateTimeOffset.UtcNow)) }
          LoadAsync =
            fun scope conversationName ->
                read scope (fun conversations ->
                    match conversations.TryGetValue conversationName with
                    | true, conversation -> conversation.Messages
                    | false, _ -> Array.empty)
                |> Task.FromResult
          LoadByExecutionAsync =
            fun scope executionId ->
                read scope (fun conversations ->
                    conversations.Values
                    |> Seq.collect _.Messages
                    |> Seq.filter (fun message -> message.Correlation.ExecutionId = executionId)
                    |> Seq.sortBy _.Timestamp
                    |> Seq.toArray)
                |> Task.FromResult
          ListConversationsAsync =
            fun scope ->
                read scope (fun conversations ->
                    conversations.Values |> Seq.map _.Meta |> Seq.sortBy _.CreatedAt |> Seq.toArray)
                |> Task.FromResult
          DeleteConversationAsync =
            fun scope conversationName ->
                task { mutate scope (ConversationStoreEvent.ConversationDeleted conversationName) }
          DeleteScopeAsync = fun scope -> task { mutate scope ConversationStoreEvent.DeleteScope } }

    let create (baseDir: string) : ConversationStore =
        createPersistent (fun scope ->
            let path = streamPath baseDir scope

            { Append =
                fun expectedSequence line ->
                    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

                    use stream =
                        new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)

                    use reader = new StreamReader(stream, leaveOpen = true)
                    let mutable actualSequence = 0L

                    while not reader.EndOfStream do
                        if reader.ReadLine() |> String.IsNullOrWhiteSpace |> not then
                            actualSequence <- actualSequence + 1L

                    if actualSequence <> expectedSequence then
                        raise (ConversationSequenceConflictException(expectedSequence, actualSequence))

                    stream.Seek(0L, SeekOrigin.End) |> ignore
                    use writer = new StreamWriter(stream, leaveOpen = true)
                    writer.WriteLine line
                    writer.Flush()
                    stream.Flush(true)
              LoadAll =
                fun () ->
                    if File.Exists path then
                        File.ReadAllLines path
                        |> Array.filter (String.IsNullOrWhiteSpace >> not)
                        |> Array.toList
                    else
                        [] })
