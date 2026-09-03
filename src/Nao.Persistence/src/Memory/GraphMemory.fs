namespace Nao.Persistence

open Nao.Agents

/// Mutating events for knowledge-graph persistence.
[<RequireQualifiedAccess>]
type GraphEvent =
    | UpsertNode of GraphNode
    | AddRelation of GraphRelation
    | RemoveNode of nodeId: string

/// Event-sourced graph memory. Query/traversal logic is delegated to an in-memory
/// instance rebuilt by replaying the event log. Relations produced by
/// ExtractRelationsAsync are persisted as concrete AddRelation events so reloads
/// never re-run a (possibly external) extractor.
module PersistentGraphMemory =
    let create
            (store: EventStore)
            (relationExtractor: (string -> System.Threading.Tasks.Task<GraphRelation list>) option)
            : GraphMemory =
        let inner = InMemoryGraphMemory.create relationExtractor

        do
            for line in store.LoadAll() do
                match FSharpJson.deserialize<GraphEvent> line with
                | GraphEvent.UpsertNode n -> inner.UpsertNodeAsync(n).GetAwaiter().GetResult()
                | GraphEvent.AddRelation r -> inner.AddRelationAsync(r).GetAwaiter().GetResult()
                | GraphEvent.RemoveNode id -> inner.RemoveNodeAsync(id).GetAwaiter().GetResult()

        { UpsertNodeAsync = fun (node: GraphNode) ->
            task {
                do! inner.UpsertNodeAsync node
                store.Append(FSharpJson.serialize (GraphEvent.UpsertNode node))
            }

          AddRelationAsync = fun (relation: GraphRelation) ->
            task {
                do! inner.AddRelationAsync relation
                store.Append(FSharpJson.serialize (GraphEvent.AddRelation relation))
            }

          QueryAsync = fun (query: GraphQuery) -> inner.QueryAsync query

          RemoveNodeAsync = fun (nodeId: string) ->
            task {
                do! inner.RemoveNodeAsync nodeId
                store.Append(FSharpJson.serialize (GraphEvent.RemoveNode nodeId))
            }

          RemoveRelationAsync = fun (subject: string) (predicate: string) (object': string) ->
            // Underlying in-memory store cannot remove individual relations; no state changes to persist.
            inner.RemoveRelationAsync subject predicate object'

          GetByTypeAsync = fun (entityType: string) -> inner.GetByTypeAsync entityType

          ExtractRelationsAsync = fun (text: string) ->
            task {
                let! extracted = inner.ExtractRelationsAsync text
                for rel in extracted do
                    store.Append(FSharpJson.serialize (GraphEvent.AddRelation rel))
                return extracted
            } }

/// Factory helpers for graph memory persistence.
module GraphMemories =
    /// ADO.NET-backed graph memory over any provider supplied via the connection factory.
    let ado
        (factory: DbConnectionFactory)
        (relationExtractor: (string -> System.Threading.Tasks.Task<GraphRelation list>) option)
        : GraphMemory =
        PersistentGraphMemory.create (EventStore.db factory "graph") relationExtractor

    /// FileSystem-backed graph memory rooted at the given directory.
    let file
        (baseDir: string)
        (relationExtractor: (string -> System.Threading.Tasks.Task<GraphRelation list>) option)
        : GraphMemory =
        PersistentGraphMemory.create
            (EventStore.file (System.IO.Path.Combine(baseDir, "graph.jsonl")))
            relationExtractor
