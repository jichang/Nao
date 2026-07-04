namespace Nao.Persistence

open Nao.Agents

/// Mutating events for episodic memory persistence.
[<RequireQualifiedAccess>]
type EpisodicEvent =
    | Record of Episode
    | Link of fromId: string * toId: string
    | Forget of importanceThreshold: float

/// Event-sourced IEpisodicMemory. Delegates all query logic to an in-memory
/// instance rebuilt by replaying the event log, so similarity/graph behaviour is
/// identical to InMemoryEpisodicMemory.
type PersistentEpisodicMemory(store: IEventStore, ?embeddingProvider: IEmbeddingProvider) =
    let inner =
        match embeddingProvider with
        | Some p -> InMemoryEpisodicMemory(p) :> IEpisodicMemory
        | None -> InMemoryEpisodicMemory() :> IEpisodicMemory

    do
        for line in store.LoadAll() do
            match FSharpJson.deserialize<EpisodicEvent> line with
            | EpisodicEvent.Record ep -> inner.RecordAsync(ep).GetAwaiter().GetResult()
            | EpisodicEvent.Link(f, t) -> (inner.LinkAsync f t).GetAwaiter().GetResult()
            | EpisodicEvent.Forget th -> (inner.ForgetBelowAsync th).GetAwaiter().GetResult() |> ignore

    interface IEpisodicMemory with
        member _.RecordAsync(episode: Episode) =
            task {
                do! inner.RecordAsync episode
                store.Append(FSharpJson.serialize (EpisodicEvent.Record episode))
            }

        member _.QueryAsync(query: EpisodeQuery) = inner.QueryAsync query

        member _.LinkAsync (fromId: string) (toId: string) =
            task {
                do! inner.LinkAsync fromId toId
                store.Append(FSharpJson.serialize (EpisodicEvent.Link(fromId, toId)))
            }

        member _.GetChainAsync(episodeId: string) = inner.GetChainAsync episodeId

        member _.SynthesizeAsync(context: string) = inner.SynthesizeAsync context

        member _.ForgetBelowAsync(importanceThreshold: float) =
            task {
                let! removed = inner.ForgetBelowAsync importanceThreshold
                store.Append(FSharpJson.serialize (EpisodicEvent.Forget importanceThreshold))
                return removed
            }

/// Factory helpers for episodic memory persistence.
module EpisodicMemories =
    /// ADO.NET-backed episodic memory over any provider supplied via the connection factory.
    let ado (factory: IDbConnectionFactory) (embeddingProvider: IEmbeddingProvider option) : IEpisodicMemory =
        PersistentEpisodicMemory(EventStore.db factory "episodic", ?embeddingProvider = embeddingProvider)
        :> IEpisodicMemory

    /// FileSystem-backed episodic memory rooted at the given directory.
    let file (baseDir: string) (embeddingProvider: IEmbeddingProvider option) : IEpisodicMemory =
        PersistentEpisodicMemory(
            EventStore.file (System.IO.Path.Combine(baseDir, "episodic.jsonl")),
            ?embeddingProvider = embeddingProvider
        )
        :> IEpisodicMemory
