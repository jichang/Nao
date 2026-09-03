namespace Nao.Persistence

open Nao.Agents

/// Mutating events for episodic memory persistence.
[<RequireQualifiedAccess>]
type EpisodicEvent =
    | Record of Episode
    | Link of fromId: string * toId: string
    | Forget of importanceThreshold: float

/// Event-sourced episodic memory. Delegates all query logic to an in-memory
/// instance rebuilt by replaying the event log, so similarity/graph behaviour is
/// identical to `InMemoryEpisodicMemory`.
module PersistentEpisodicMemory =
    let create (store: EventStore) (embeddingProvider: EmbeddingProvider option) : EpisodicMemory =
        let inner = InMemoryEpisodicMemory.create embeddingProvider

        do
            for line in store.LoadAll() do
                match FSharpJson.deserialize<EpisodicEvent> line with
                | EpisodicEvent.Record ep -> inner.RecordAsync(ep).GetAwaiter().GetResult()
                | EpisodicEvent.Link(f, t) -> (inner.LinkAsync f t).GetAwaiter().GetResult()
                | EpisodicEvent.Forget th -> (inner.ForgetBelowAsync th).GetAwaiter().GetResult() |> ignore

        { RecordAsync = fun (episode: Episode) ->
            task {
                do! inner.RecordAsync episode
                store.Append(FSharpJson.serialize (EpisodicEvent.Record episode))
            }

          QueryAsync = fun (query: EpisodeQuery) -> inner.QueryAsync query

          LinkAsync = fun (fromId: string) (toId: string) ->
            task {
                do! inner.LinkAsync fromId toId
                store.Append(FSharpJson.serialize (EpisodicEvent.Link(fromId, toId)))
            }

          GetChainAsync = fun (episodeId: string) -> inner.GetChainAsync episodeId

          SynthesizeAsync = fun (context: string) -> inner.SynthesizeAsync context

          ForgetBelowAsync = fun (importanceThreshold: float) ->
            task {
                let! removed = inner.ForgetBelowAsync importanceThreshold
                store.Append(FSharpJson.serialize (EpisodicEvent.Forget importanceThreshold))
                return removed
            } }

/// Factory helpers for episodic memory persistence.
module EpisodicMemories =
    /// ADO.NET-backed episodic memory over any provider supplied via the connection factory.
    let ado (factory: DbConnectionFactory) (embeddingProvider: EmbeddingProvider option) : EpisodicMemory =
        PersistentEpisodicMemory.create (EventStore.db factory "episodic") embeddingProvider

    /// FileSystem-backed episodic memory rooted at the given directory.
    let file (baseDir: string) (embeddingProvider: EmbeddingProvider option) : EpisodicMemory =
        PersistentEpisodicMemory.create
            (EventStore.file (System.IO.Path.Combine(baseDir, "episodic.jsonl")))
            embeddingProvider
