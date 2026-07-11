namespace Nao.Persistence

open System
open System.Threading.Tasks
open System.Collections.Concurrent
open Nao.Agents

/// In-memory implementation of tool discovery with usage-based ranking
type InMemoryToolDiscovery(config: ToolDiscoveryConfig, ?embeddingProvider: IEmbeddingProvider) =
    let tools = ConcurrentDictionary<string, ToolSchema * DiscoverySource>()
    let stats = ConcurrentDictionary<string, ToolUsageStats>()
    let availability = ConcurrentDictionary<string, ToolAvailability>()

    let computeRelevance (query: string) (schema: ToolSchema) =
        task {
            match embeddingProvider with
            | Some provider ->
                let toolText = sprintf "%s %s %s" schema.Name schema.Description (schema.Category |> Option.defaultValue "")
                let! queryEmb = provider.EmbedAsync query
                let! toolEmb = provider.EmbedAsync toolText
                return SemanticSimilarity.cosineSimilarity queryEmb toolEmb
            | None ->
                // Keyword overlap scoring
                let queryWords = query.ToLowerInvariant().Split(' ') |> Set.ofArray
                let toolWords =
                    (sprintf "%s %s" schema.Name schema.Description).ToLowerInvariant().Split(' ')
                    |> Set.ofArray
                let overlap = Set.intersect queryWords toolWords |> Set.count
                return float overlap / float (max 1 queryWords.Count)
        }

    let estimateSchemaTokens (schema: ToolSchema) =
        let rendered = ToolSchema.render schema
        (rendered.Length + 3) / 4

    /// Register a tool from a given source
    member _.Register (schema: ToolSchema) (source: DiscoverySource) =
        tools.TryAdd(schema.Name, (schema, source)) |> ignore

    /// Register multiple tools
    member this.RegisterMany (schemas: ToolSchema list) (source: DiscoverySource) =
        for schema in schemas do
            this.Register schema source

    interface IToolDiscovery with
        member _.DiscoverAsync() =
            tools.Values
            |> Seq.map fst
            |> Seq.toList
            |> Task.FromResult

        member _.RankForTaskAsync (taskDescription: string) (maxTools: int) =
            task {
                let allTools = tools.Values |> Seq.map fst |> Seq.toList
                let! scored =
                    allTools
                    |> List.map (fun schema ->
                        task {
                            let! relevance = computeRelevance taskDescription schema
                            // Boost by usage success rate
                            let usageBoost =
                                match stats.TryGetValue(schema.Name) with
                                | true, s when s.InvocationCount > 0 ->
                                    float s.SuccessCount / float s.InvocationCount * 0.2
                                | _ -> 0.0
                            return (schema, relevance + usageBoost)
                        })
                    |> fun tasks -> Task.WhenAll(tasks |> List.toArray)
                return
                    scored
                    |> Array.filter (fun (_, score) -> score >= config.RelevanceThreshold)
                    |> Array.sortByDescending snd
                    |> Array.truncate maxTools
                    |> Array.toList
            }

        member _.CheckAvailabilityAsync(toolName: string) =
            match availability.TryGetValue(toolName) with
            | true, avail -> Task.FromResult(avail)
            | _ ->
                if tools.ContainsKey(toolName) then
                    Task.FromResult(ToolAvailability.Available)
                else
                    Task.FromResult(ToolAvailability.Unavailable "Tool not registered")

        member _.GetStatsAsync(toolName: string) =
            match stats.TryGetValue(toolName) with
            | true, s -> Task.FromResult(Some s)
            | _ -> Task.FromResult(None)

        member _.RecordInvocationAsync (toolName: string) (success: bool) (latencyMs: int64) (cost: float) =
            stats.AddOrUpdate(
                toolName,
                { ToolName = toolName
                  InvocationCount = 1
                  SuccessCount = if success then 1 else 0
                  FailureCount = if success then 0 else 1
                  AverageLatencyMs = float latencyMs
                  LastUsed = Some DateTimeOffset.UtcNow
                  TotalCost = cost },
                fun _ existing ->
                    let newCount = existing.InvocationCount + 1
                    { existing with
                        InvocationCount = newCount
                        SuccessCount = existing.SuccessCount + (if success then 1 else 0)
                        FailureCount = existing.FailureCount + (if success then 0 else 1)
                        AverageLatencyMs =
                            (existing.AverageLatencyMs * float existing.InvocationCount + float latencyMs) / float newCount
                        LastUsed = Some DateTimeOffset.UtcNow
                        TotalCost = existing.TotalCost + cost })
            |> ignore
            task { return () }

        member _.PruneForContextAsync (taskDescription: string) (availableTokenBudget: int) =
            task {
                let allTools = tools.Values |> Seq.map fst |> Seq.toList
                let! scored =
                    allTools
                    |> List.map (fun schema ->
                        task {
                            let! relevance = computeRelevance taskDescription schema
                            return (schema, relevance)
                        })
                    |> fun tasks -> Task.WhenAll(tasks |> List.toArray)

                let ranked =
                    scored
                    |> Array.filter (fun (_, score) -> score >= config.RelevanceThreshold)
                    |> Array.sortByDescending snd

                // Greedily fill token budget
                let mutable remaining = availableTokenBudget
                let result = ResizeArray<ToolSchema>()
                for (schema, _) in ranked do
                    let tokens = estimateSchemaTokens schema
                    if remaining >= tokens && result.Count < config.MaxToolsInContext then
                        result.Add(schema)
                        remaining <- remaining - tokens
                return result |> Seq.toList
            }
