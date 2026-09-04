namespace Nao.Persistence

open System
open System.Threading.Tasks
open System.Collections.Concurrent
open Nao.Agents

module InMemoryTieredMemory =
    let create (config: TieredMemoryConfig) (embeddingProvider: EmbeddingProvider option) : TieredMemory =
        if config.ShortTermCapacity < 0 then
            invalidArg (nameof config) "Short-term capacity cannot be negative."

        if config.MidTermCapacity < 0 then
            invalidArg (nameof config) "Mid-term capacity cannot be negative."

        if config.MidTermTtl |> Option.exists (fun ttl -> ttl < TimeSpan.Zero) then
            invalidArg (nameof config) "Mid-term TTL cannot be negative."

        let entries = ConcurrentDictionary<string * string, TieredMemoryEntry>()
        let key owner entryKey = owner, entryKey

        let requireOwner owner =
            if String.IsNullOrWhiteSpace owner then
                invalidArg (nameof owner) "Tiered-memory owner cannot be blank."

        let owned owner =
            entries.Values |> Seq.filter (fun entry -> entry.Owner = owner)

        let inTier owner tier =
            owned owner |> Seq.filter (fun entry -> entry.Tier = tier)

        let capacityFor tier =
            match tier with
            | MemoryTier.ShortTerm -> config.ShortTermCapacity
            | MemoryTier.MidTerm -> config.MidTermCapacity
            | MemoryTier.LongTerm -> Int32.MaxValue

        let remove entry =
            entries.TryRemove(key entry.Owner entry.Key) |> ignore

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

                victims |> List.iter remove
                victims.Length

        let storeAsync (entry: TieredMemoryEntry) =
            requireOwner entry.Owner
            entries.[key entry.Owner entry.Key] <- entry

            if config.AutoEvict then
                evictOverflow entry.Owner entry.Tier |> ignore

            Task.FromResult()

        let rankAsync owner query maxResults =
            requireOwner owner

            task {
                let candidates = owned owner |> Seq.toList

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

        let retrieveFromTierAsync owner tier maxResults =
            requireOwner owner

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

        let recordAccessAsync owner entryKeys asOf =
            requireOwner owner

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

        let promoteAsync owner entryKey targetTier =
            requireOwner owner

            match entries.TryGetValue(key owner entryKey) with
            | true, entry ->
                entries.[key owner entryKey] <- { entry with Tier = targetTier }

                if config.AutoEvict then
                    evictOverflow owner targetTier |> ignore
            | false, _ -> ()

            Task.FromResult()

        let evictAsync owner asOf =
            requireOwner owner

            let expired =
                match config.MidTermTtl with
                | Some ttl ->
                    inTier owner MemoryTier.MidTerm
                    |> Seq.filter (fun entry -> entry.Timestamp + ttl < asOf)
                    |> Seq.toArray
                | None -> Array.empty

            expired |> Array.iter remove

            let overflow =
                evictOverflow owner MemoryTier.ShortTerm
                + evictOverflow owner MemoryTier.MidTerm

            Task.FromResult(expired.Length + overflow)

        let delete predicate =
            let matches = entries.Values |> Seq.filter predicate |> Seq.toArray
            matches |> Array.iter remove
            Task.FromResult matches.Length

        let protect owner operation =
            task {
                if String.IsNullOrWhiteSpace owner then
                    return
                        Error(
                            PlatformFailure.create
                                PlatformErrorCategory.InvalidInput
                                "Tiered-memory owner cannot be blank."
                                false
                                None
                        )
                else
                    try
                        let! count = operation ()
                        return Ok count
                    with error ->
                        return Error(PlatformFailure.fromException PlatformFailureBoundary.Storage None error)
            }

        let deleteOwnerAsync owner =
            protect owner (fun () -> delete (fun entry -> entry.Owner = owner))

        let deleteExpiredAsync owner before =
            protect owner (fun () -> delete (fun entry -> entry.Owner = owner && entry.Timestamp < before))

        { StoreAsync = storeAsync
          RetrieveAsync = rankAsync
          RetrieveFromTierAsync = retrieveFromTierAsync
          RecordAccessAsync = recordAccessAsync
          PromoteAsync = promoteAsync
          EvictAsync = evictAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }
