namespace Nao.Persistence

open Nao.Agents

/// Mutating events for knowledge-graph persistence.
[<RequireQualifiedAccess>]
type GraphEvent =
    | UpsertNode of GraphNode
    | AddRelation of GraphRelation
    | RemoveNode of nodeId: string

/// Event-sourced IGraphMemory. Query/traversal logic is delegated to an in-memory
/// instance rebuilt by replaying the event log. Relations produced by
/// ExtractRelationsAsync are persisted as concrete AddRelation events so reloads
/// never re-run a (possibly external) extractor.
type PersistentGraphMemory(store: IEventStore, ?relationExtractor: string -> System.Threading.Tasks.Task<GraphRelation list>) =
    let inner =
        match relationExtractor with
        | Some ex -> InMemoryGraphMemory(ex) :> IGraphMemory
        | None -> InMemoryGraphMemory() :> IGraphMemory

    do
        for line in store.LoadAll() do
            match FSharpJson.deserialize<GraphEvent> line with
            | GraphEvent.UpsertNode n -> inner.UpsertNodeAsync(n).GetAwaiter().GetResult()
            | GraphEvent.AddRelation r -> inner.AddRelationAsync(r).GetAwaiter().GetResult()
            | GraphEvent.RemoveNode id -> inner.RemoveNodeAsync(id).GetAwaiter().GetResult()

    interface IGraphMemory with
        member _.UpsertNodeAsync(node: GraphNode) =
            task {
                do! inner.UpsertNodeAsync node
                store.Append(FSharpJson.serialize (GraphEvent.UpsertNode node))
            }

        member _.AddRelationAsync(relation: GraphRelation) =
            task {
                do! inner.AddRelationAsync relation
                store.Append(FSharpJson.serialize (GraphEvent.AddRelation relation))
            }

        member _.QueryAsync(query: GraphQuery) = inner.QueryAsync query

        member _.RemoveNodeAsync(nodeId: string) =
            task {
                do! inner.RemoveNodeAsync nodeId
                store.Append(FSharpJson.serialize (GraphEvent.RemoveNode nodeId))
            }

        member _.RemoveRelationAsync (subject: string) (predicate: string) (object': string) =
            // Underlying in-memory store cannot remove individual relations; no state changes to persist.
            inner.RemoveRelationAsync subject predicate object'

        member _.GetByTypeAsync(entityType: string) = inner.GetByTypeAsync entityType

        member _.ExtractRelationsAsync(text: string) =
            task {
                let! extracted = inner.ExtractRelationsAsync text
                for rel in extracted do
                    store.Append(FSharpJson.serialize (GraphEvent.AddRelation rel))
                return extracted
            }

/// Factory helpers for graph memory persistence.
module GraphMemories =
    /// ADO.NET-backed graph memory over any provider supplied via the connection factory.
    let ado
        (factory: IDbConnectionFactory)
        (relationExtractor: (string -> System.Threading.Tasks.Task<GraphRelation list>) option)
        : IGraphMemory =
        PersistentGraphMemory(EventStore.db factory "graph", ?relationExtractor = relationExtractor)
        :> IGraphMemory

    /// FileSystem-backed graph memory rooted at the given directory.
    let file
        (baseDir: string)
        (relationExtractor: (string -> System.Threading.Tasks.Task<GraphRelation list>) option)
        : IGraphMemory =
        PersistentGraphMemory(
            EventStore.file (System.IO.Path.Combine(baseDir, "graph.jsonl")),
            ?relationExtractor = relationExtractor
        )
        :> IGraphMemory
