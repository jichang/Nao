namespace Nao.Persistence

open System
open System.Threading.Tasks
open System.Collections.Concurrent
open Nao.Agents

module InMemoryTieredMemory =
    let internal validateConfig (config: TieredMemoryConfig) =
        if config.ShortTermCapacity < 0 then
            invalidArg (nameof config) "Short-term capacity cannot be negative."

        if config.MidTermCapacity < 0 then
            invalidArg (nameof config) "Mid-term capacity cannot be negative."

        if config.MidTermTtl |> Option.exists (fun ttl -> ttl < TimeSpan.Zero) then
            invalidArg (nameof config) "Mid-term TTL cannot be negative."

    let create (config: TieredMemoryConfig) (embeddingProvider: EmbeddingProvider option) : TieredMemory =
        validateConfig config
        let sync = obj ()
        let entries = ConcurrentDictionary<string * string, TieredMemoryEntry>()
        let key owner entryKey = owner, entryKey
        let ownerKey = AuthorizationScope.identity >> AuthorizationIdentity.storageKey

        let owned owner =
            entries
            |> Seq.choose (fun pair -> if fst pair.Key = owner then Some pair.Value else None)

        let inTier owner tier =
            owned owner |> Seq.filter (fun entry -> entry.Tier = tier)

        let capacityFor tier =
            match tier with
            | MemoryTier.ShortTerm -> config.ShortTermCapacity
            | MemoryTier.MidTerm -> config.MidTermCapacity
            | MemoryTier.LongTerm -> Int32.MaxValue

        let remove owner (entry: TieredMemoryEntry) =
            entries.TryRemove(key owner entry.Key) |> ignore

        let evictOverflow owner tier =
            let tierEntries = inTier owner tier |> Seq.toList
            let excess = tierEntries.Length - capacityFor tier

            if excess <= 0 then
                0
            else
                let victims =
                    tierEntries
                    |> List.sortBy (fun entry -> entry.AccessCount, entry.Timestamp, entry.Key)
                    |> List.take excess

                victims |> List.iter (remove owner)
                victims.Length

        let storeAsync scope (entry: TieredMemoryEntry) =
            let owner = ownerKey scope
            entries.[key owner entry.Key] <- entry

            if config.AutoEvict then
                evictOverflow owner entry.Tier |> ignore

            Task.FromResult()

        let rankAsync scope query maxResults =
            let owner = ownerKey scope

            task {
                let candidates = lock sync (fun () -> owned owner |> Seq.toList)

                match embeddingProvider with
                | Some provider ->
                    let! queryEmbedding = provider.EmbedAsync query

                    let! ranked =
                        candidates
                        |> List.map (fun entry ->
                            task {
                                let! embedding = provider.EmbedAsync entry.Value
                                return entry, SemanticSimilarity.cosineSimilarity queryEmbedding embedding
                            })
                        |> List.toArray
                        |> Task.WhenAll

                    return
                        ranked
                        |> Array.sortByDescending (fun (entry, score) ->
                            score, entry.Relevance, entry.Timestamp, entry.Key)
                        |> Array.truncate maxResults
                        |> Array.map fst
                        |> Array.toList
                | None ->
                    let queryWords = query.ToLowerInvariant().Split(' ') |> Set.ofArray

                    return
                        candidates
                        |> List.map (fun entry ->
                            let words = entry.Value.ToLowerInvariant().Split(' ') |> Set.ofArray
                            entry, Set.intersect queryWords words |> Set.count)
                        |> List.sortByDescending (fun (entry, score) ->
                            score, entry.Relevance, entry.Timestamp, entry.Key)
                        |> List.truncate maxResults
                        |> List.map fst
            }

        let retrieveFromTierAsync scope tier maxResults =
            let owner = ownerKey scope

            inTier owner tier
            |> Seq.sortByDescending (fun entry -> entry.Timestamp, entry.Key)
            |> Seq.truncate maxResults
            |> Seq.toList
            |> Task.FromResult

        let nextTier tier =
            match tier with
            | MemoryTier.ShortTerm -> Some MemoryTier.MidTerm
            | MemoryTier.MidTerm -> Some MemoryTier.LongTerm
            | MemoryTier.LongTerm -> None

        let shouldPromote asOf entry =
            match config.PromotionPolicy with
            | MemoryPromotionPolicy.AccessThreshold count -> entry.AccessCount >= count
            | MemoryPromotionPolicy.RecencyBased maxAge -> asOf - entry.Timestamp <= maxAge
            | MemoryPromotionPolicy.Manual -> false

        let recordAccessAsync scope entryKeys asOf =
            let owner = ownerKey scope

            for entryKey in entryKeys |> List.distinct do
                match entries.TryGetValue(key owner entryKey) with
                | true, entry ->
                    let accessed =
                        { entry with
                            AccessCount = entry.AccessCount + 1 }

                    let updated =
                        match shouldPromote asOf accessed, nextTier accessed.Tier with
                        | true, Some target -> { accessed with Tier = target }
                        | _ -> accessed

                    entries.[key owner entryKey] <- updated

                    if config.AutoEvict then
                        evictOverflow owner updated.Tier |> ignore
                | false, _ -> ()

            Task.FromResult()

        let promoteAsync scope entryKey targetTier =
            let owner = ownerKey scope

            match entries.TryGetValue(key owner entryKey) with
            | true, entry ->
                entries.[key owner entryKey] <- { entry with Tier = targetTier }

                if config.AutoEvict then
                    evictOverflow owner targetTier |> ignore
            | false, _ -> ()

            Task.FromResult()

        let evictAsync scope asOf =
            let owner = ownerKey scope

            let expired =
                match config.MidTermTtl with
                | Some ttl ->
                    inTier owner MemoryTier.MidTerm
                    |> Seq.filter (fun entry -> entry.Timestamp + ttl < asOf)
                    |> Seq.toArray
                | None -> Array.empty

            expired |> Array.iter (remove owner)

            let overflow =
                evictOverflow owner MemoryTier.ShortTerm
                + evictOverflow owner MemoryTier.MidTerm

            Task.FromResult(expired.Length + overflow)

        let delete scope predicate =
            let owner = ownerKey scope
            let matches = owned owner |> Seq.filter predicate |> Seq.toArray
            matches |> Array.iter (remove owner)
            Task.FromResult matches.Length

        let deleteOwnerAsync scope =
            task {
                let! count = delete scope (fun _ -> true)
                return Ok count
            }

        let deleteExpiredAsync scope before =
            task {
                let! count = delete scope (fun entry -> entry.Timestamp < before)
                return Ok count
            }

        { StoreAsync = fun scope entry -> lock sync (fun () -> storeAsync scope entry)
          RetrieveAsync = rankAsync
          RetrieveFromTierAsync = fun scope tier count -> lock sync (fun () -> retrieveFromTierAsync scope tier count)
          RecordAccessAsync = fun scope keys asOf -> lock sync (fun () -> recordAccessAsync scope keys asOf)
          PromoteAsync = fun scope key tier -> lock sync (fun () -> promoteAsync scope key tier)
          EvictAsync = fun scope asOf -> lock sync (fun () -> evictAsync scope asOf)
          DeleteOwnerAsync = fun scope -> lock sync (fun () -> deleteOwnerAsync scope)
          DeleteExpiredAsync = fun scope before -> lock sync (fun () -> deleteExpiredAsync scope before) }
