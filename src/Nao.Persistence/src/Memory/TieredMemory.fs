namespace Nao.Persistence

open Nao.Agents

/// Mutating events for tiered memory persistence.
[<RequireQualifiedAccess>]
type TieredEvent =
    | Store of TieredMemoryEntry
    | Promote of key: string * targetTier: MemoryTier
    | Evict

/// Event-sourced tiered memory. Note: time-relative eviction is re-evaluated at
/// load time when an Evict event replays, which is the desired durability behaviour.
module PersistentTieredMemory =
    let create
            (store: EventStore)
            (config: TieredMemoryConfig)
            (embeddingProvider: EmbeddingProvider option)
            : TieredMemory =
        let inner = InMemoryTieredMemory.create config embeddingProvider

        do
            for line in store.LoadAll() do
                match FSharpJson.deserialize<TieredEvent> line with
                | TieredEvent.Store e -> inner.StoreAsync(e).GetAwaiter().GetResult()
                | TieredEvent.Promote(k, t) -> (inner.PromoteAsync k t).GetAwaiter().GetResult()
                | TieredEvent.Evict -> inner.EvictAsync().GetAwaiter().GetResult() |> ignore

        { StoreAsync = fun (entry: TieredMemoryEntry) ->
            task {
                do! inner.StoreAsync entry
                store.Append(FSharpJson.serialize (TieredEvent.Store entry))
            }

          RetrieveAsync = fun (query: string) (maxResults: int) -> inner.RetrieveAsync query maxResults

          RetrieveFromTierAsync = fun (tier: MemoryTier) (maxResults: int) ->
            inner.RetrieveFromTierAsync tier maxResults

          PromoteAsync = fun (key: string) (targetTier: MemoryTier) ->
            task {
                do! inner.PromoteAsync key targetTier
                store.Append(FSharpJson.serialize (TieredEvent.Promote(key, targetTier)))
            }

          EvictAsync = fun () ->
            task {
                let! removed = inner.EvictAsync()
                store.Append(FSharpJson.serialize TieredEvent.Evict)
                return removed
            } }

/// Factory helpers for tiered memory persistence.
module TieredMemories =
    /// ADO.NET-backed tiered memory over any provider supplied via the connection factory.
    let ado
        (factory: DbConnectionFactory)
        (config: TieredMemoryConfig)
        (embeddingProvider: EmbeddingProvider option)
        : TieredMemory =
        PersistentTieredMemory.create (EventStore.db factory "tiered") config embeddingProvider

    /// FileSystem-backed tiered memory rooted at the given directory.
    let file
        (baseDir: string)
        (config: TieredMemoryConfig)
        (embeddingProvider: EmbeddingProvider option)
        : TieredMemory =
        PersistentTieredMemory.create
            (EventStore.file (System.IO.Path.Combine(baseDir, "tiered.jsonl")))
            config
            embeddingProvider
