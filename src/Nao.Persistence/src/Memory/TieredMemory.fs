namespace Nao.Persistence

open Nao.Agents

/// Mutating events for tiered memory persistence.
[<RequireQualifiedAccess>]
type TieredEvent =
    | Store of TieredMemoryEntry
    | Promote of key: string * targetTier: MemoryTier
    | Evict

/// Event-sourced ITieredMemory. Note: time-relative eviction is re-evaluated at
/// load time when an Evict event replays, which is the desired durability behaviour.
type PersistentTieredMemory(store: IEventStore, config: TieredMemoryConfig, ?embeddingProvider: IEmbeddingProvider) =
    let inner =
        match embeddingProvider with
        | Some p -> InMemoryTieredMemory(config, p) :> ITieredMemory
        | None -> InMemoryTieredMemory(config) :> ITieredMemory

    do
        for line in store.LoadAll() do
            match FSharpJson.deserialize<TieredEvent> line with
            | TieredEvent.Store e -> inner.StoreAsync(e).GetAwaiter().GetResult()
            | TieredEvent.Promote(k, t) -> (inner.PromoteAsync k t).GetAwaiter().GetResult()
            | TieredEvent.Evict -> inner.EvictAsync().GetAwaiter().GetResult() |> ignore

    interface ITieredMemory with
        member _.StoreAsync(entry: TieredMemoryEntry) =
            task {
                do! inner.StoreAsync entry
                store.Append(FSharpJson.serialize (TieredEvent.Store entry))
            }

        member _.RetrieveAsync (query: string) (maxResults: int) = inner.RetrieveAsync query maxResults

        member _.RetrieveFromTierAsync (tier: MemoryTier) (maxResults: int) =
            inner.RetrieveFromTierAsync tier maxResults

        member _.PromoteAsync (key: string) (targetTier: MemoryTier) =
            task {
                do! inner.PromoteAsync key targetTier
                store.Append(FSharpJson.serialize (TieredEvent.Promote(key, targetTier)))
            }

        member _.EvictAsync() =
            task {
                let! removed = inner.EvictAsync()
                store.Append(FSharpJson.serialize TieredEvent.Evict)
                return removed
            }

/// Factory helpers for tiered memory persistence.
module TieredMemories =
    /// ADO.NET-backed tiered memory over any provider supplied via the connection factory.
    let ado
        (factory: IDbConnectionFactory)
        (config: TieredMemoryConfig)
        (embeddingProvider: IEmbeddingProvider option)
        : ITieredMemory =
        PersistentTieredMemory(EventStore.db factory "tiered", config, ?embeddingProvider = embeddingProvider)
        :> ITieredMemory

    /// FileSystem-backed tiered memory rooted at the given directory.
    let file
        (baseDir: string)
        (config: TieredMemoryConfig)
        (embeddingProvider: IEmbeddingProvider option)
        : ITieredMemory =
        PersistentTieredMemory(
            EventStore.file (System.IO.Path.Combine(baseDir, "tiered.jsonl")),
            config,
            ?embeddingProvider = embeddingProvider
        )
        :> ITieredMemory
