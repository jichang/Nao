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
        (store: EventStore)
        (relationExtractor: (string -> System.Threading.Tasks.Task<GraphRelation list>) option)
        : GraphMemory =
        let inner = InMemoryGraphMemory.create relationExtractor

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

        do
            store.LoadAll()
            |> Seq.map FSharpJson.deserialize<GraphDocument>
            |> Seq.iter (fun document ->
                if document.Version <> 1 then
                    invalidOp (sprintf "Unsupported graph-memory document version: %d." document.Version)

                replay document.Event)

        let append event =
            store.Append(FSharpJson.serialize { Version = 1; Event = event })

        let appendAfter operation event =
            task {
                do! operation
                append event
            }

        let deleteAfter operation event =
            task {
                let! result = operation

                match result with
                | Ok _ -> append event
                | Error _ -> ()

                return result
            }

        let extractRelationsAsync owner text =
            task {
                let! extracted = inner.ExtractRelationsAsync owner text
                extracted |> List.iter (GraphEvent.AddRelation >> append)
                return extracted
            }

        { UpsertNodeAsync = fun node -> appendAfter (inner.UpsertNodeAsync node) (GraphEvent.UpsertNode node)
          AddRelationAsync =
            fun relation -> appendAfter (inner.AddRelationAsync relation) (GraphEvent.AddRelation relation)
          QueryAsync = inner.QueryAsync
          RemoveNodeAsync =
            fun owner nodeId -> appendAfter (inner.RemoveNodeAsync owner nodeId) (GraphEvent.RemoveNode(owner, nodeId))
          RemoveRelationAsync =
            fun owner subject predicate object' ->
                appendAfter
                    (inner.RemoveRelationAsync owner subject predicate object')
                    (GraphEvent.RemoveRelation(owner, subject, predicate, object'))
          GetByTypeAsync = inner.GetByTypeAsync
          ExtractRelationsAsync = extractRelationsAsync
          DeleteOwnerAsync = fun owner -> deleteAfter (inner.DeleteOwnerAsync owner) (GraphEvent.DeleteOwner owner)
          DeleteExpiredAsync =
            fun owner before ->
                deleteAfter (inner.DeleteExpiredAsync owner before) (GraphEvent.DeleteExpired(owner, before)) }

module GraphMemories =
    let ado
        (factory: DbConnectionFactory)
        (relationExtractor: (string -> System.Threading.Tasks.Task<GraphRelation list>) option)
        : GraphMemory =
        PersistentGraphMemory.create (EventStore.db factory "graph") relationExtractor

    let file
        (baseDir: string)
        (relationExtractor: (string -> System.Threading.Tasks.Task<GraphRelation list>) option)
        : GraphMemory =
        PersistentGraphMemory.create
            (EventStore.file (System.IO.Path.Combine(baseDir, "graph.jsonl")))
            relationExtractor
