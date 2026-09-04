namespace Nao.Persistence

open System
open Nao.Agents

[<RequireQualifiedAccess>]
type GraphEvent =
    | UpsertNode of GraphNode
    | AddRelation of GraphRelation
    | RemoveNode of owner: string * nodeId: string
    | RemoveRelation of owner: string * subject: string * predicate: string * object': string
    | DeleteOwner of owner: string
    | DeleteExpired of owner: string * before: DateTimeOffset

type GraphDocument = { Version: int; Event: GraphEvent }

module PersistentGraphMemory =
    let create
        context
        (store: EventStore)
        (relationExtractor: (string -> System.Threading.Tasks.Task<GraphRelation list>) option)
        : GraphMemory =
        let inner = InMemoryGraphMemory.create relationExtractor

        let loadEvents () =
            EventStream.loadCurrent
                context
                1
                FSharpJson.deserialize<GraphDocument>
                (fun document -> document.Version)
                (fun document -> document.Event)
                store

        let replay event =
            match event with
            | GraphEvent.UpsertNode node -> inner.UpsertNodeAsync node |> _.GetAwaiter().GetResult()
            | GraphEvent.AddRelation relation -> inner.AddRelationAsync relation |> _.GetAwaiter().GetResult()
            | GraphEvent.RemoveNode(owner, nodeId) -> inner.RemoveNodeAsync owner nodeId |> _.GetAwaiter().GetResult()
            | GraphEvent.RemoveRelation(owner, subject, predicate, object') ->
                inner.RemoveRelationAsync owner subject predicate object'
                |> _.GetAwaiter().GetResult()
            | GraphEvent.DeleteOwner owner -> inner.DeleteOwnerAsync owner |> _.GetAwaiter().GetResult() |> ignore
            | GraphEvent.DeleteExpired(owner, before) ->
                inner.DeleteExpiredAsync owner before |> _.GetAwaiter().GetResult() |> ignore

        do loadEvents () |> List.iter replay

        let append event =
            store.Append(FSharpJson.serialize { Version = 1; Event = event })

        let appendAfter operation event =
            task {
                loadEvents () |> ignore
                do! operation ()
                append event
            }

        let deleteAfter operation event =
            task {
                loadEvents () |> ignore
                let! result = operation ()

                match result with
                | Ok _ -> append event
                | Error _ -> ()

                return result
            }

        let extractRelationsAsync owner text =
            task {
                loadEvents () |> ignore
                let! extracted = inner.ExtractRelationsAsync owner text
                extracted |> List.iter (GraphEvent.AddRelation >> append)
                return extracted
            }

        let upsertNodeAsync node =
            appendAfter (fun () -> inner.UpsertNodeAsync node) (GraphEvent.UpsertNode node)

        let addRelationAsync relation =
            appendAfter (fun () -> inner.AddRelationAsync relation) (GraphEvent.AddRelation relation)

        let removeNodeAsync owner nodeId =
            appendAfter (fun () -> inner.RemoveNodeAsync owner nodeId) (GraphEvent.RemoveNode(owner, nodeId))

        let removeRelationAsync owner subject predicate object' =
            appendAfter
                (fun () -> inner.RemoveRelationAsync owner subject predicate object')
                (GraphEvent.RemoveRelation(owner, subject, predicate, object'))

        let deleteOwnerAsync owner =
            deleteAfter (fun () -> inner.DeleteOwnerAsync owner) (GraphEvent.DeleteOwner owner)

        let deleteExpiredAsync owner before =
            deleteAfter (fun () -> inner.DeleteExpiredAsync owner before) (GraphEvent.DeleteExpired(owner, before))

        { UpsertNodeAsync = upsertNodeAsync
          AddRelationAsync = addRelationAsync
          QueryAsync = inner.QueryAsync
          RemoveNodeAsync = removeNodeAsync
          RemoveRelationAsync = removeRelationAsync
          GetByTypeAsync = inner.GetByTypeAsync
          ExtractRelationsAsync = extractRelationsAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }

module GraphMemories =
    let ado
        (factory: DbConnectionFactory)
        (relationExtractor: (string -> System.Threading.Tasks.Task<GraphRelation list>) option)
        : GraphMemory =
        PersistentGraphMemory.create "graph" (EventStore.db factory "graph") relationExtractor

    let file
        (baseDir: string)
        (relationExtractor: (string -> System.Threading.Tasks.Task<GraphRelation list>) option)
        : GraphMemory =
        let path = System.IO.Path.Combine(baseDir, "graph.jsonl")
        PersistentGraphMemory.create path (EventStore.file path) relationExtractor
