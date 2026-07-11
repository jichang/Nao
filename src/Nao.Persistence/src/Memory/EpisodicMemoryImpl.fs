namespace Nao.Persistence

open System
open System.Threading.Tasks
open System.Collections.Concurrent
open Nao.Agents

/// In-memory episodic memory implementation
type InMemoryEpisodicMemory(?embeddingProvider: IEmbeddingProvider) =
    let episodes = ConcurrentDictionary<string, Episode>()
    let embeddings = ConcurrentDictionary<string, float array>()

    let computeEmbedding (text: string) =
        task {
            match embeddingProvider with
            | Some provider ->
                return! provider.EmbedAsync text
            | None ->
                // Simple hash-based pseudo-embedding for testing
                let words = text.ToLowerInvariant().Split(' ') |> Array.distinct
                let vec = Array.zeroCreate 64
                for word in words do
                    let hash = abs (word.GetHashCode())
                    let idx = hash % 64
                    vec.[idx] <- vec.[idx] + 1.0
                return vec
        }

    interface IEpisodicMemory with
        member _.RecordAsync(episode: Episode) =
            task {
                episodes.TryAdd(episode.Id, episode) |> ignore
                let! emb = computeEmbedding (sprintf "%s %s %s" episode.Action episode.Observation episode.Context)
                embeddings.TryAdd(episode.Id, emb) |> ignore
            }

        member _.QueryAsync(query: EpisodeQuery) =
            let collectRelated episodeId maxHops =
                let rec collect (ids: Set<string>) (visited: Set<string>) (depth: int) =
                    if depth >= maxHops then visited
                    else
                        let neighbors =
                            ids
                            |> Set.toList
                            |> List.collect (fun id ->
                                match episodes.TryGetValue(id) with
                                | true, ep -> ep.LinkedEpisodes
                                | _ -> [])
                            |> List.filter (fun id -> not (visited.Contains id))
                            |> Set.ofList
                        collect neighbors (Set.union visited neighbors) (depth + 1)
                collect (Set.singleton episodeId) (Set.singleton episodeId) 0

            task {
                match query with
                | EpisodeQuery.BySimilarity (description, topK) ->
                    let! queryEmb = computeEmbedding description
                    return
                        episodes.Values
                        |> Seq.map (fun ep ->
                            let emb =
                                match embeddings.TryGetValue(ep.Id) with
                                | true, e -> e
                                | _ -> Array.empty
                            (ep, SemanticSimilarity.cosineSimilarity queryEmb emb))
                        |> Seq.sortByDescending snd
                        |> Seq.truncate topK
                        |> Seq.map fst
                        |> Seq.toList

                | EpisodeQuery.ByTimeRange (from', to') ->
                    return
                        episodes.Values
                        |> Seq.filter (fun ep -> ep.Timestamp >= from' && ep.Timestamp <= to')
                        |> Seq.sortByDescending (fun ep -> ep.Timestamp)
                        |> Seq.toList

                | EpisodeQuery.ByTags tags ->
                    let tagSet = Set.ofList tags
                    return
                        episodes.Values
                        |> Seq.filter (fun ep ->
                            ep.Tags |> List.exists (fun t -> tagSet.Contains t))
                        |> Seq.sortByDescending (fun ep -> ep.Importance)
                        |> Seq.toList

                | EpisodeQuery.Recent count ->
                    return
                        episodes.Values
                        |> Seq.sortByDescending (fun ep -> ep.Timestamp)
                        |> Seq.truncate count
                        |> Seq.toList

                | EpisodeQuery.Related (episodeId, maxHops) ->
                    let related = collectRelated episodeId maxHops
                    return
                        related
                        |> Set.toList
                        |> List.choose (fun id ->
                            match episodes.TryGetValue(id) with
                            | true, ep -> Some ep
                            | _ -> None)

                | EpisodeQuery.ByOutcome (success, topK) ->
                    return
                        episodes.Values
                        |> Seq.filter (fun ep -> ep.Success = success)
                        |> Seq.sortByDescending (fun ep -> ep.Importance)
                        |> Seq.truncate topK
                        |> Seq.toList
            }

        member _.LinkAsync (fromId: string) (toId: string) =
            match episodes.TryGetValue(fromId) with
            | true, ep ->
                if not (ep.LinkedEpisodes |> List.contains toId) then
                    let updated = { ep with LinkedEpisodes = toId :: ep.LinkedEpisodes }
                    episodes.TryUpdate(fromId, updated, ep) |> ignore
            | _ -> ()
            task { return () }

        member _.GetChainAsync(episodeId: string) =
            let rec walk (id: string) (visited: Set<string>) (acc: Episode list) =
                if visited.Contains id then acc
                else
                    match episodes.TryGetValue(id) with
                    | true, ep ->
                        let newVisited = visited.Add id
                        let mutable result = ep :: acc
                        for linkedId in ep.LinkedEpisodes do
                            result <- walk linkedId newVisited result
                        result
                    | _ -> acc

            task {
                return walk episodeId Set.empty [] |> List.sortBy (fun ep -> ep.Timestamp)
            }

        member _.SynthesizeAsync(context: string) =
            task {
                let! queryEmb = computeEmbedding context
                let similar =
                    episodes.Values
                    |> Seq.map (fun ep ->
                        let emb =
                            match embeddings.TryGetValue(ep.Id) with
                            | true, e -> e
                            | _ -> Array.empty
                        (ep, SemanticSimilarity.cosineSimilarity queryEmb emb))
                    |> Seq.filter (fun (_, sim) -> sim > 0.3)
                    |> Seq.sortByDescending snd
                    |> Seq.truncate 10
                    |> Seq.map fst
                    |> Seq.toList

                // Synthesize lessons from patterns
                let successfulPatterns =
                    similar
                    |> List.filter (fun ep -> ep.Success)
                    |> List.map (fun ep -> sprintf "When %s -> %s (success)" ep.Action ep.Observation)

                let failurePatterns =
                    similar
                    |> List.filter (fun ep -> not ep.Success)
                    |> List.map (fun ep -> sprintf "Avoid: %s -> %s (failed)" ep.Action ep.Observation)

                return successfulPatterns @ failurePatterns
            }

        member _.ForgetBelowAsync(importanceThreshold: float) =
            task {
                let toForget =
                    episodes.Values
                    |> Seq.filter (fun ep -> ep.Importance < importanceThreshold)
                    |> Seq.toList
                let mutable count = 0
                for ep in toForget do
                    if episodes.TryRemove(ep.Id) |> fst then
                        embeddings.TryRemove(ep.Id) |> ignore
                        count <- count + 1
                return count
            }
