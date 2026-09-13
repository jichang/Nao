namespace Nao.Persistence

open System
open System.Collections.Concurrent
open System.IO
open System.Threading.Tasks
open Nao.Agents

module private FeedbackPersistence =
    let key = AuthorizationScope.identity >> AuthorizationIdentity.storageKey

    let failure operation =
        task {
            try
                return Ok(operation ())
            with ex ->
                return Error(PlatformFailure.fromException PlatformFailureBoundary.Storage None ex)
        }

module InMemoryTurnStore =
    let create () : TurnStore =
        let turns = ConcurrentDictionary<string * string, TurnRecord>()

        let owned scope =
            let owner = FeedbackPersistence.key scope

            turns
            |> Seq.choose (fun pair -> if fst pair.Key = owner then Some pair.Value else None)

        let delete scope predicate =
            task {
                let owner = FeedbackPersistence.key scope
                let mutable deleted = 0

                for pair in turns do
                    if fst pair.Key = owner && predicate pair.Value then
                        match turns.TryRemove pair.Key with
                        | true, _ -> deleted <- deleted + 1
                        | false, _ -> ()

                return Ok deleted
            }

        { SaveAsync =
            fun scope turn ->
                turns.[(FeedbackPersistence.key scope, turn.TurnId)] <- turn
                Task.CompletedTask
          GetAsync =
            fun scope turnId ->
                match turns.TryGetValue((FeedbackPersistence.key scope, turnId)) with
                | true, turn -> Some turn
                | false, _ -> None
                |> Task.FromResult
          GetAllAsync = fun scope -> owned scope |> Seq.toList |> Task.FromResult
          GetForExecutionAsync =
            fun scope executionId ->
                owned scope
                |> Seq.filter (fun turn -> turn.Correlation.ExecutionId = executionId)
                |> Seq.sortBy _.CreatedAt
                |> Seq.toList
                |> Task.FromResult
          DeleteScopeAsync = fun scope -> delete scope (fun _ -> true)
          DeleteExpiredAsync = fun scope before -> delete scope (fun turn -> turn.CreatedAt < before) }

module InMemoryFeedbackStore =
    let create () : FeedbackStore =
        let entries = ConcurrentDictionary<string * Guid, Feedback>()

        let owned scope =
            let owner = FeedbackPersistence.key scope

            entries
            |> Seq.choose (fun pair -> if fst pair.Key = owner then Some pair.Value else None)

        let delete scope predicate =
            task {
                let owner = FeedbackPersistence.key scope
                let mutable deleted = 0

                for pair in entries do
                    if fst pair.Key = owner && predicate pair.Value then
                        match entries.TryRemove pair.Key with
                        | true, _ -> deleted <- deleted + 1
                        | false, _ -> ()

                return Ok deleted
            }

        { SaveAsync =
            fun scope feedback ->
                entries.[(FeedbackPersistence.key scope, feedback.Id)] <- feedback
                Task.CompletedTask
          GetForTurnAsync =
            fun scope turnId ->
                owned scope
                |> Seq.filter (fun feedback -> feedback.TurnId = turnId)
                |> Seq.sortBy _.CreatedAt
                |> Seq.toList
                |> Task.FromResult
          GetAllAsync = fun scope -> owned scope |> Seq.toList |> Task.FromResult
          DeleteScopeAsync = fun scope -> delete scope (fun _ -> true)
          DeleteExpiredAsync = fun scope before -> delete scope (fun feedback -> feedback.CreatedAt < before) }

[<RequireQualifiedAccess>]
type TurnStoreEvent =
    | Accepted of TurnRecord
    | DeleteScope
    | DeleteExpired of DateTimeOffset

type TurnStoreDocument = { Version: int; Event: TurnStoreEvent }

[<RequireQualifiedAccess>]
type FeedbackStoreEvent =
    | Accepted of Feedback
    | DeleteScope
    | DeleteExpired of DateTimeOffset

type FeedbackStoreDocument =
    { Version: int
      Event: FeedbackStoreEvent }

module private FeedbackStream =
    let private decode stream deserialize getVersion getEvent eventNumber line =
        try
            let document = deserialize line

            if isNull (box document) || getVersion document <> 1 then
                raise (System.Text.Json.JsonException(sprintf "Expected %s schema version 1." stream))

            getEvent document
        with ex ->
            raise (
                InvalidDataException(
                    sprintf
                        "%s stream is invalid at event %d. Follow docs/migrations before writing."
                        stream
                        eventNumber,
                    ex
                )
            )

    let decodeTurn stream eventNumber line =
        decode
            stream
            FeedbackJson.deserialize<TurnStoreDocument>
            (fun document -> document.Version)
            (fun document -> document.Event)
            eventNumber
            line

    let decodeFeedback stream eventNumber line =
        decode
            stream
            FeedbackJson.deserialize<FeedbackStoreDocument>
            (fun document -> document.Version)
            (fun document -> document.Event)
            eventNumber
            line

module PersistentTurnStore =
    let create (openStore: string -> EventStore) : TurnStore =
        let sync = obj ()

        let load scope =
            let key = FeedbackPersistence.key scope
            let context = "turn-store/" + key
            let store = openStore key
            let inner = InMemoryTurnStore.create ()

            store.LoadAll()
            |> List.iteri (fun index line ->
                let event = FeedbackStream.decodeTurn context (index + 1) line

                match event with
                | TurnStoreEvent.Accepted turn -> inner.SaveAsync scope turn |> _.GetAwaiter().GetResult()
                | TurnStoreEvent.DeleteScope -> inner.DeleteScopeAsync scope |> _.GetAwaiter().GetResult() |> ignore
                | TurnStoreEvent.DeleteExpired before ->
                    inner.DeleteExpiredAsync scope before |> _.GetAwaiter().GetResult() |> ignore)

            store, inner

        let read scope operation =
            lock sync (fun () ->
                let _, inner = load scope
                operation inner)

        let mutate scope operation (event: TurnStoreEvent) =
            lock sync (fun () ->
                let store, inner = load scope
                let result = operation inner
                let document: TurnStoreDocument = { Version = 1; Event = event }
                store.Append(FeedbackJson.serialize document)
                result)

        { SaveAsync =
            fun scope turn ->
                task {
                    mutate
                        scope
                        (fun inner -> inner.SaveAsync scope turn |> _.GetAwaiter().GetResult())
                        (TurnStoreEvent.Accepted turn)
                }
          GetAsync = fun scope turnId -> read scope (fun inner -> inner.GetAsync scope turnId)
          GetAllAsync = fun scope -> read scope (fun inner -> inner.GetAllAsync scope)
          GetForExecutionAsync =
            fun scope executionId -> read scope (fun inner -> inner.GetForExecutionAsync scope executionId)
          DeleteScopeAsync =
            fun scope ->
                FeedbackPersistence.failure (fun () ->
                    mutate
                        scope
                        (fun inner ->
                            inner.DeleteScopeAsync scope
                            |> _.GetAwaiter().GetResult()
                            |> Result.defaultWith (fun failure -> raise (InvalidOperationException failure.Message)))
                        TurnStoreEvent.DeleteScope)
          DeleteExpiredAsync =
            fun scope before ->
                FeedbackPersistence.failure (fun () ->
                    mutate
                        scope
                        (fun inner ->
                            inner.DeleteExpiredAsync scope before
                            |> _.GetAwaiter().GetResult()
                            |> Result.defaultWith (fun failure -> raise (InvalidOperationException failure.Message)))
                        (TurnStoreEvent.DeleteExpired before)) }

module PersistentFeedbackStore =
    let create (openStore: string -> EventStore) : FeedbackStore =
        let sync = obj ()

        let load scope =
            let key = FeedbackPersistence.key scope
            let context = "feedback-store/" + key
            let store = openStore key
            let inner = InMemoryFeedbackStore.create ()

            store.LoadAll()
            |> List.iteri (fun index line ->
                let event = FeedbackStream.decodeFeedback context (index + 1) line

                match event with
                | FeedbackStoreEvent.Accepted feedback -> inner.SaveAsync scope feedback |> _.GetAwaiter().GetResult()
                | FeedbackStoreEvent.DeleteScope ->
                    inner.DeleteScopeAsync scope |> _.GetAwaiter().GetResult() |> ignore
                | FeedbackStoreEvent.DeleteExpired before ->
                    inner.DeleteExpiredAsync scope before |> _.GetAwaiter().GetResult() |> ignore)

            store, inner

        let read scope operation =
            lock sync (fun () ->
                let _, inner = load scope
                operation inner)

        let mutate scope operation (event: FeedbackStoreEvent) =
            lock sync (fun () ->
                let store, inner = load scope
                let result = operation inner
                let document: FeedbackStoreDocument = { Version = 1; Event = event }
                store.Append(FeedbackJson.serialize document)
                result)

        { SaveAsync =
            fun scope feedback ->
                task {
                    mutate
                        scope
                        (fun inner -> inner.SaveAsync scope feedback |> _.GetAwaiter().GetResult())
                        (FeedbackStoreEvent.Accepted feedback)
                }
          GetForTurnAsync = fun scope turnId -> read scope (fun inner -> inner.GetForTurnAsync scope turnId)
          GetAllAsync = fun scope -> read scope (fun inner -> inner.GetAllAsync scope)
          DeleteScopeAsync =
            fun scope ->
                FeedbackPersistence.failure (fun () ->
                    mutate
                        scope
                        (fun inner ->
                            inner.DeleteScopeAsync scope
                            |> _.GetAwaiter().GetResult()
                            |> Result.defaultWith (fun failure -> raise (InvalidOperationException failure.Message)))
                        FeedbackStoreEvent.DeleteScope)
          DeleteExpiredAsync =
            fun scope before ->
                FeedbackPersistence.failure (fun () ->
                    mutate
                        scope
                        (fun inner ->
                            inner.DeleteExpiredAsync scope before
                            |> _.GetAwaiter().GetResult()
                            |> Result.defaultWith (fun failure -> raise (InvalidOperationException failure.Message)))
                        (FeedbackStoreEvent.DeleteExpired before)) }

module TurnStores =
    let inMemory () = InMemoryTurnStore.create ()

    let file baseDir =
        PersistentTurnStore.create (fun key -> EventStore.file (Path.Combine(baseDir, "turn-store", key + ".jsonl")))

    let ado factory =
        PersistentTurnStore.create (fun key -> EventStore.db factory ("turn-store/" + key))

module FeedbackStores =
    let inMemory () = InMemoryFeedbackStore.create ()

    let file baseDir =
        PersistentFeedbackStore.create (fun key ->
            EventStore.file (Path.Combine(baseDir, "feedback-store", key + ".jsonl")))

    let ado factory =
        PersistentFeedbackStore.create (fun key -> EventStore.db factory ("feedback-store/" + key))

module FileTurnStore =
    let create = TurnStores.file

module AdoTurnStore =
    let create = TurnStores.ado

module FileFeedbackStore =
    let create = FeedbackStores.file

module AdoFeedbackStore =
    let create = FeedbackStores.ado

module FeedbackDb =
    let file baseDir =
        Directory.CreateDirectory baseDir |> ignore
        FeedbackService.create (TurnStores.file baseDir) (FeedbackStores.file baseDir)

    let database factory =
        FeedbackService.create (TurnStores.ado factory) (FeedbackStores.ado factory)

    let create mode =
        match mode with
        | PersistenceMode.File baseDir -> file baseDir
        | PersistenceMode.Database factory -> database factory
