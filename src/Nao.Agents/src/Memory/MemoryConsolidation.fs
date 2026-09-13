namespace Nao.Agents

open System
open System.Threading.Tasks
open Nao.Agents

/// Strategy for consolidating memories
[<RequireQualifiedAccess>]
type ConsolidationStrategy =
    /// Merge similar memories into a single entry
    | MergeSimilar of similarityThreshold: float
    /// Summarize clusters of related memories
    | Summarize of provider: LlmProvider * options: CompletionOptions
    /// Remove redundant memories (subsumed by others)
    | Deduplicate
    /// Decay importance of old, unaccessed memories
    | ImportanceDecay of decayFactor: float * minAge: TimeSpan
    /// Composite: apply multiple strategies in order
    | Composite of ConsolidationStrategy list

/// Result of a consolidation pass
type ConsolidationResult =
    { Merged: int
      Removed: int
      Summarized: int
      TotalBefore: int
      TotalAfter: int }

/// Functional memory-consolidation capability (background process).
type MemoryConsolidation =
    {
        /// Run a consolidation pass
        ConsolidateAsync: AuthorizationScope -> CorrelationContext -> ConsolidationStrategy -> Task<ConsolidationResult>
        /// Get consolidation statistics
        GetStatsAsync: AuthorizationScope -> Task<ConsolidationResult>
    }

/// Memory consolidation operating on `MemoryStore`.
module MemoryConsolidation =

    let private textSimilarity (a: string) (b: string) =
        let wordsA = a.ToLowerInvariant().Split(' ') |> Set.ofArray
        let wordsB = b.ToLowerInvariant().Split(' ') |> Set.ofArray
        let intersection = Set.intersect wordsA wordsB |> Set.count |> float
        let union = Set.union wordsA wordsB |> Set.count |> float
        if union = 0.0 then 0.0 else intersection / union

    let mergeSimilarAsync (store: MemoryStore) (authorization: AuthorizationScope) (threshold: float) : Task<int> =
        task {
            let! entries = store.RecallAllAsync authorization
            let mutable merged = 0
            let processed = System.Collections.Generic.HashSet<string>()

            for i in 0 .. entries.Length - 1 do
                if not (processed.Contains entries.[i].Key) then
                    let cluster = ResizeArray<MemoryEntry>()
                    cluster.Add(entries.[i])

                    for j in i + 1 .. entries.Length - 1 do
                        if not (processed.Contains entries.[j].Key) then
                            let sim = textSimilarity entries.[i].Value entries.[j].Value

                            if sim >= threshold then
                                cluster.Add(entries.[j])
                                processed.Add(entries.[j].Key) |> ignore

                    if cluster.Count > 1 then
                        // Merge: keep the most recent, combine values
                        let newest = cluster |> Seq.maxBy (fun e -> e.Timestamp)

                        let combinedValue =
                            cluster |> Seq.map (fun e -> e.Value) |> Seq.distinct |> String.concat " | "

                        let combinedTags =
                            cluster |> Seq.collect (fun e -> e.Tags) |> Seq.distinct |> Seq.toList

                        let mergedEntry =
                            { newest with
                                Value = combinedValue
                                Tags = combinedTags }
                        // Remove old entries and save merged
                        for e in cluster do
                            do! store.ForgetAsync authorization e.Key

                        do! store.SaveAsync authorization mergedEntry
                        merged <- merged + (cluster.Count - 1)

            return merged
        }

    let deduplicateAsync (store: MemoryStore) (authorization: AuthorizationScope) : Task<int> =
        task {
            let! entries = store.RecallAllAsync authorization
            let seen = System.Collections.Generic.HashSet<string>()
            let mutable removed = 0

            for entry in entries do
                let normalized = entry.Value.Trim().ToLowerInvariant()

                if seen.Contains normalized then
                    do! store.ForgetAsync authorization entry.Key
                    removed <- removed + 1
                else
                    seen.Add(normalized) |> ignore

            return removed
        }

    let importanceDecayAsync
        (store: MemoryStore)
        (authorization: AuthorizationScope)
        (decayFactor: float)
        (minAge: TimeSpan)
        : Task<int> =
        task {
            let! entries = store.RecallAllAsync authorization
            let now = DateTimeOffset.UtcNow
            let mutable decayed = 0

            for entry in entries do
                let age = now - entry.Timestamp

                if age > minAge then
                    // Add a decay marker tag
                    let decayCount =
                        entry.Tags
                        |> List.tryFind (fun t -> t.StartsWith("decay:"))
                        |> Option.map (fun t -> t.Replace("decay:", "") |> float)
                        |> Option.defaultValue 1.0

                    let newDecay = decayCount * decayFactor

                    if newDecay < 0.1 then
                        do! store.ForgetAsync authorization entry.Key
                        decayed <- decayed + 1
                    else
                        let updatedTags =
                            entry.Tags
                            |> List.filter (fun t -> not (t.StartsWith("decay:")))
                            |> fun tags -> sprintf "decay:%.3f" newDecay :: tags

                        let updated = { entry with Tags = updatedTags }
                        do! store.ForgetAsync authorization entry.Key
                        do! store.SaveAsync authorization updated

            return decayed
        }

    let summarizeClusterAsync
        (correlation: CorrelationContext)
        (provider: LlmProvider)
        (options: CompletionOptions)
        (store: MemoryStore)
        (authorization: AuthorizationScope)
        : Task<int> =
        task {
            let! entries = store.RecallAllAsync authorization

            if entries.Length < 10 then
                return 0
            else
                // Group by tags
                let groups =
                    entries
                    |> List.groupBy (fun e ->
                        match e.Tags with
                        | tag :: _ -> tag
                        | [] -> "untagged")
                    |> List.filter (fun (_, group) -> group.Length >= 3)

                let mutable summarized = 0

                for (tag, group) in groups do
                    let content =
                        group
                        |> List.map (fun e -> sprintf "- %s: %s" e.Key e.Value)
                        |> String.concat "\n"

                    let prompt =
                        [ { Role = System
                            Content =
                              "Consolidate these memory entries into a single concise summary. Preserve all key facts." }
                          { Role = User; Content = content } ]

                    let! result = provider.CompleteAsync correlation prompt options
                    // Replace cluster with summary
                    for e in group do
                        do! store.ForgetAsync authorization e.Key

                    let summaryEntry: MemoryEntry =
                        { Key = sprintf "consolidated:%s:%s" tag (Guid.NewGuid().ToString("N").[..7])
                          Value = result.Content
                          Timestamp = DateTimeOffset.UtcNow
                          Tags = [ tag; "consolidated" ] }

                    do! store.SaveAsync authorization summaryEntry
                    summarized <- summarized + group.Length

                return summarized
        }

    let rec consolidateAsync
        (correlation: CorrelationContext)
        (store: MemoryStore)
        (authorization: AuthorizationScope)
        (strategy: ConsolidationStrategy)
        : Task<ConsolidationResult> =
        task {
            let! beforeEntries = store.RecallAllAsync authorization
            let totalBefore = beforeEntries.Length

            match strategy with
            | ConsolidationStrategy.MergeSimilar threshold ->
                let! merged = mergeSimilarAsync store authorization threshold
                let! afterEntries = store.RecallAllAsync authorization

                return
                    { Merged = merged
                      Removed = 0
                      Summarized = 0
                      TotalBefore = totalBefore
                      TotalAfter = afterEntries.Length }

            | ConsolidationStrategy.Deduplicate ->
                let! removed = deduplicateAsync store authorization
                let! afterEntries = store.RecallAllAsync authorization

                return
                    { Merged = 0
                      Removed = removed
                      Summarized = 0
                      TotalBefore = totalBefore
                      TotalAfter = afterEntries.Length }

            | ConsolidationStrategy.ImportanceDecay(factor, minAge) ->
                let! removed = importanceDecayAsync store authorization factor minAge
                let! afterEntries = store.RecallAllAsync authorization

                return
                    { Merged = 0
                      Removed = removed
                      Summarized = 0
                      TotalBefore = totalBefore
                      TotalAfter = afterEntries.Length }

            | ConsolidationStrategy.Summarize(provider, options) ->
                let! summarized = summarizeClusterAsync correlation provider options store authorization
                let! afterEntries = store.RecallAllAsync authorization

                return
                    { Merged = 0
                      Removed = 0
                      Summarized = summarized
                      TotalBefore = totalBefore
                      TotalAfter = afterEntries.Length }

            | ConsolidationStrategy.Composite strategies ->
                let mutable totalMerged = 0
                let mutable totalRemoved = 0
                let mutable totalSummarized = 0

                for strat in strategies do
                    let! result = consolidateAsync correlation store authorization strat
                    totalMerged <- totalMerged + result.Merged
                    totalRemoved <- totalRemoved + result.Removed
                    totalSummarized <- totalSummarized + result.Summarized

                let! afterEntries = store.RecallAllAsync authorization

                return
                    { Merged = totalMerged
                      Removed = totalRemoved
                      Summarized = totalSummarized
                      TotalBefore = totalBefore
                      TotalAfter = afterEntries.Length }
        }
