namespace Nao.Persistence

open System
open System.Threading.Tasks
open System.Collections.Concurrent
open Nao.Agents

/// In-memory tiered-memory factory with promotion, demotion, and eviction.
module InMemoryTieredMemory =
    let create (config: TieredMemoryConfig) (embeddingProvider: EmbeddingProvider option) : TieredMemory =
        let entries = ConcurrentDictionary<string, TieredMemoryEntry>()

        let tierOf (entry: TieredMemoryEntry) = entry.Tier

        let entriesInTier tier =
            entries.Values
            |> Seq.filter (fun e -> e.Tier = tier)
            |> Seq.toList

        let capacityFor tier =
            match tier with
            | MemoryTier.ShortTerm -> config.ShortTermCapacity
            | MemoryTier.MidTerm -> config.MidTermCapacity
            | MemoryTier.LongTerm -> Int32.MaxValue

        let shouldPromote (entry: TieredMemoryEntry) =
            match config.PromotionPolicy with
            | MemoryPromotionPolicy.AccessThreshold count -> entry.AccessCount >= count
            | MemoryPromotionPolicy.RecencyBased maxAge ->
                (DateTimeOffset.UtcNow - entry.Timestamp) <= maxAge
            | MemoryPromotionPolicy.Manual -> false

        let nextTier tier =
            match tier with
            | MemoryTier.ShortTerm -> Some MemoryTier.MidTerm
            | MemoryTier.MidTerm -> Some MemoryTier.LongTerm
            | MemoryTier.LongTerm -> None

        let isExpired (entry: TieredMemoryEntry) =
            match entry.Tier, config.MidTermTtl with
            | MemoryTier.MidTerm, Some ttl ->
                (DateTimeOffset.UtcNow - entry.Timestamp) > ttl
            | _ -> false

        let evictFromTier tier =
            let inTier = entriesInTier tier
            let capacity = capacityFor tier
            if inTier.Length > capacity then
                let toEvict =
                    inTier
                    |> List.sortBy (fun e -> e.AccessCount, e.Timestamp)
                    |> List.take (inTier.Length - capacity)
                for e in toEvict do
                    entries.TryRemove(e.Key) |> ignore
                toEvict.Length
            else 0

        { StoreAsync = fun (entry: TieredMemoryEntry) ->
            entries.AddOrUpdate(entry.Key, entry, fun _ _ -> entry) |> ignore
            if config.AutoEvict then
                evictFromTier entry.Tier |> ignore
            task { return () }

          RetrieveAsync = fun (query: string) (maxResults: int) ->
            task {
                let allEntries = entries.Values |> Seq.toList
                let! ranked =
                    task {
                        match embeddingProvider with
                        | Some provider ->
                            let! queryEmbed = provider.EmbedAsync query
                            let! results =
                                allEntries
                                |> List.map (fun e ->
                                    task {
                                        let! eEmbed = provider.EmbedAsync e.Value
                                        let sim = SemanticSimilarity.cosineSimilarity queryEmbed eEmbed
                                        return (e, sim)
                                    })
                                |> List.toArray
                                |> Task.WhenAll
                            return results |> Array.sortByDescending snd |> Array.map fst |> Array.toList
                        | None ->
                            // Fallback: keyword overlap scoring
                            let queryWords =
                                query.ToLowerInvariant().Split(' ')
                                |> Set.ofArray
                            return
                                allEntries
                                |> List.map (fun e ->
                                    let entryWords = e.Value.ToLowerInvariant().Split(' ') |> Set.ofArray
                                    let overlap = Set.intersect queryWords entryWords |> Set.count
                                    (e, float overlap))
                                |> List.sortByDescending snd
                                |> List.map fst
                    }

                let results = ranked |> List.truncate maxResults
                // Update access counts
                for e in results do
                    let updated = { e with AccessCount = e.AccessCount + 1; Timestamp = DateTimeOffset.UtcNow }
                    entries.TryUpdate(e.Key, updated, e) |> ignore
                    // Check promotion
                    if shouldPromote updated then
                        match nextTier updated.Tier with
                        | Some target ->
                            entries.TryUpdate(e.Key, { updated with Tier = target }, updated) |> ignore
                        | None -> ()
                return results
            }

          RetrieveFromTierAsync = fun (tier: MemoryTier) (maxResults: int) ->
            entriesInTier tier
            |> List.sortByDescending (fun e -> e.Timestamp)
            |> List.truncate maxResults
            |> Task.FromResult

          PromoteAsync = fun (key: string) (targetTier: MemoryTier) ->
            match entries.TryGetValue(key) with
            | true, entry ->
                let promoted = { entry with Tier = targetTier; Timestamp = DateTimeOffset.UtcNow }
                entries.TryUpdate(key, promoted, entry) |> ignore
            | false, _ -> ()
            task { return () }

          EvictAsync = fun () ->
            let mutable totalEvicted = 0
            // Evict expired mid-term entries
            let expired =
                entries.Values
                |> Seq.filter isExpired
                |> Seq.toList
            for e in expired do
                entries.TryRemove(e.Key) |> ignore
                totalEvicted <- totalEvicted + 1
            // Evict overflow from each tier
            totalEvicted <- totalEvicted + evictFromTier MemoryTier.ShortTerm
            totalEvicted <- totalEvicted + evictFromTier MemoryTier.MidTerm
            task { return totalEvicted } }
