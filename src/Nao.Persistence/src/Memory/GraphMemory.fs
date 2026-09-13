namespace Nao.Persistence

open System
open System.IO
open System.Data.Common
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
type GraphEvent =
    | UpsertNode of GraphNode
    | AddRelations of GraphRelation list
    | RemoveNode of nodeId: string
    | RemoveRelation of subject: string * predicate: string * object': string
    | DeleteOwner
    | DeleteExpired of before: DateTimeOffset

type GraphDocument = { Version: int; Event: GraphEvent }

module PersistentGraphMemory =
    /// The host supplies a separate event stream for each claim-free partition key.
    let create
        (openStore: string -> EventStore)
        (relationExtractor: (string -> Task<GraphRelation list>) option)
        : GraphMemory =
        let sync = obj ()

        let load scope =
            let key = AuthorizationScope.identity scope |> AuthorizationIdentity.storageKey
            let store = openStore key
            let inner = InMemoryGraphMemory.create None

            let events =
                EventStream.loadCurrent
                    ("graph/" + key)
                    1
                    FSharpJson.deserialize<GraphDocument>
                    (fun document -> document.Version)
                    (fun document -> document.Event)
                    store

            for event in events do
                match event with
                | GraphEvent.UpsertNode node -> inner.UpsertNodeAsync scope node |> _.GetAwaiter().GetResult()
                | GraphEvent.AddRelations relations ->
                    for relation in relations do
                        inner.AddRelationAsync scope relation |> _.GetAwaiter().GetResult()
                | GraphEvent.RemoveNode nodeId -> inner.RemoveNodeAsync scope nodeId |> _.GetAwaiter().GetResult()
                | GraphEvent.RemoveRelation(subject, predicate, object') ->
                    inner.RemoveRelationAsync scope subject predicate object'
                    |> _.GetAwaiter().GetResult()
                | GraphEvent.DeleteOwner -> inner.DeleteOwnerAsync scope |> _.GetAwaiter().GetResult() |> ignore
                | GraphEvent.DeleteExpired before ->
                    inner.DeleteExpiredAsync scope before |> _.GetAwaiter().GetResult() |> ignore

            store, inner

        let append (store: EventStore) event =
            store.Append(FSharpJson.serialize { Version = 1; Event = event })

        // Replay is local to an operation: a failed append never publishes speculative state.
        let mutate scope operation event =
            task {
                return
                    lock sync (fun () ->
                        let store, inner = load scope
                        let result = operation inner
                        append store event
                        result)
            }

        let read scope operation =
            task {
                return
                    lock sync (fun () ->
                        let _, inner = load scope
                        operation inner)
            }

        let protect operation =
            let failure error =
                Error(PlatformFailure.fromException PlatformFailureBoundary.Storage None error)

            task {
                try
                    return! operation ()
                with
                | :? IOException as error -> return failure error
                | :? InvalidDataException as error -> return failure error
                | :? DbException as error -> return failure error
                | :? UnauthorizedAccessException as error -> return failure error
            }

        { UpsertNodeAsync =
            fun scope node ->
                mutate
                    scope
                    (fun inner -> inner.UpsertNodeAsync scope node |> _.GetAwaiter().GetResult())
                    (GraphEvent.UpsertNode node)
          AddRelationAsync =
            fun scope relation ->
                mutate
                    scope
                    (fun inner -> inner.AddRelationAsync scope relation |> _.GetAwaiter().GetResult())
                    (GraphEvent.AddRelations [ relation ])
          QueryAsync =
            fun scope query -> read scope (fun inner -> inner.QueryAsync scope query |> _.GetAwaiter().GetResult())
          RemoveNodeAsync =
            fun scope nodeId ->
                mutate
                    scope
                    (fun inner -> inner.RemoveNodeAsync scope nodeId |> _.GetAwaiter().GetResult())
                    (GraphEvent.RemoveNode nodeId)
          RemoveRelationAsync =
            fun scope subject predicate object' ->
                mutate
                    scope
                    (fun inner ->
                        inner.RemoveRelationAsync scope subject predicate object'
                        |> _.GetAwaiter().GetResult())
                    (GraphEvent.RemoveRelation(subject, predicate, object'))
          GetByTypeAsync =
            fun scope entityType ->
                read scope (fun inner -> inner.GetByTypeAsync scope entityType |> _.GetAwaiter().GetResult())
          ExtractRelationsAsync =
            fun scope text ->
                task {
                    do! read scope (fun _ -> ())
                    let extractor = InMemoryGraphMemory.create relationExtractor
                    let! relations = extractor.ExtractRelationsAsync scope text
                    return! mutate scope (fun _ -> relations) (GraphEvent.AddRelations relations)
                }
          DeleteOwnerAsync =
            fun scope ->
                protect (fun () ->
                    mutate
                        scope
                        (fun inner -> inner.DeleteOwnerAsync scope |> _.GetAwaiter().GetResult())
                        GraphEvent.DeleteOwner)
          DeleteExpiredAsync =
            fun scope before ->
                protect (fun () ->
                    mutate
                        scope
                        (fun inner -> inner.DeleteExpiredAsync scope before |> _.GetAwaiter().GetResult())
                        (GraphEvent.DeleteExpired before)) }

module GraphMemories =
    let ado (factory: DbConnectionFactory) relationExtractor : GraphMemory =
        PersistentGraphMemory.create (fun key -> EventStore.db factory ("graph/" + key)) relationExtractor

    let file (baseDir: string) relationExtractor : GraphMemory =
        PersistentGraphMemory.create
            (fun key -> EventStore.file (Path.Combine(baseDir, "graph", key + ".jsonl")))
            relationExtractor
