namespace Nao.Persistence

open System
open System.Threading.Tasks
open System.Collections.Concurrent
open Nao.Agents

module InMemoryEpisodicMemory =
    let create (embeddingProvider: EmbeddingProvider option) : EpisodicMemory =
        let episodes = ConcurrentDictionary<string * string, Episode>()
        let embeddings = ConcurrentDictionary<string * string, float array>()
        let key owner episodeId = owner, episodeId

        let requireOwner owner =
            if String.IsNullOrWhiteSpace owner then
                invalidArg (nameof owner) "Episodic-memory owner cannot be blank."

        let owned owner =
            episodes.Values |> Seq.filter (fun episode -> episode.Owner = owner)

        let computeEmbedding (text: string) =
            task {
                match embeddingProvider with
                | Some provider -> return! provider.EmbedAsync text
                | None ->
                    let vector = Array.zeroCreate 64

                    for word in text.ToLowerInvariant().Split(' ') |> Array.distinct do
                        let index = abs (word.GetHashCode()) % 64
                        vector.[index] <- vector.[index] + 1.0

                    return vector
            }

        let recordAsync (episode: Episode) =
            task {
                requireOwner episode.Owner
                episodes.[key episode.Owner episode.Id] <- episode

                let! embedding =
                    computeEmbedding (sprintf "%s %s %s" episode.Action episode.Observation episode.Context)

                embeddings.[key episode.Owner episode.Id] <- embedding
            }

        let collectRelated owner episodeId maxHops =
            let rec collect (ids: Set<string>) (visited: Set<string>) depth =
                if depth >= maxHops then
                    visited
                else
                    let neighbors =
                        ids
                        |> Set.toList
                        |> List.collect (fun id ->
                            match episodes.TryGetValue(key owner id) with
                            | true, episode -> episode.LinkedEpisodes
                            | _ -> [])
                        |> List.filter (fun id -> not (visited.Contains id))
                        |> Set.ofList

                    collect neighbors (Set.union visited neighbors) (depth + 1)

            collect (Set.singleton episodeId) (Set.singleton episodeId) 0

        let queryAsync owner (query: EpisodeQuery) =
            requireOwner owner

            task {
                match query with
                | EpisodeQuery.BySimilarity(description, topK) ->
                    let! queryEmbedding = computeEmbedding description

                    return
                        owned owner
                        |> Seq.map (fun episode ->
                            let embedding =
                                match embeddings.TryGetValue(key owner episode.Id) with
                                | true, value -> value
                                | _ -> Array.empty

                            episode, SemanticSimilarity.cosineSimilarity queryEmbedding embedding)
                        |> Seq.sortByDescending snd
                        |> Seq.truncate topK
                        |> Seq.map fst
                        |> Seq.toList
                | EpisodeQuery.ByTimeRange(from', to') ->
                    return
                        owned owner
                        |> Seq.filter (fun episode -> episode.Timestamp >= from' && episode.Timestamp <= to')
                        |> Seq.sortByDescending (fun episode -> episode.Timestamp)
                        |> Seq.toList
                | EpisodeQuery.ByTags tags ->
                    let tagSet = Set.ofList tags

                    return
                        owned owner
                        |> Seq.filter (fun episode -> episode.Tags |> List.exists tagSet.Contains)
                        |> Seq.sortByDescending (fun episode -> episode.Importance)
                        |> Seq.toList
                | EpisodeQuery.Recent count ->
                    return
                        owned owner
                        |> Seq.sortByDescending (fun episode -> episode.Timestamp)
                        |> Seq.truncate count
                        |> Seq.toList
                | EpisodeQuery.Related(episodeId, maxHops) ->
                    return
                        collectRelated owner episodeId maxHops
                        |> Set.toList
                        |> List.choose (fun id ->
                            match episodes.TryGetValue(key owner id) with
                            | true, episode -> Some episode
                            | _ -> None)
                | EpisodeQuery.ByOutcome(success, topK) ->
                    return
                        owned owner
                        |> Seq.filter (fun episode -> episode.Success = success)
                        |> Seq.sortByDescending (fun episode -> episode.Importance)
                        |> Seq.truncate topK
                        |> Seq.toList
            }

        let linkAsync owner fromId toId =
            requireOwner owner

            match episodes.TryGetValue(key owner fromId) with
            | true, episode when
                episodes.ContainsKey(key owner toId)
                && not (episode.LinkedEpisodes |> List.contains toId)
                ->
                episodes.[key owner fromId] <-
                    { episode with
                        LinkedEpisodes = toId :: episode.LinkedEpisodes }
            | _ -> ()

            Task.FromResult()

        let getChainAsync owner episodeId =
            requireOwner owner

            let rec walk id visited collected =
                if visited |> Set.contains id then
                    collected
                else
                    match episodes.TryGetValue(key owner id) with
                    | true, episode ->
                        episode.LinkedEpisodes
                        |> List.fold
                            (fun result linkedId -> walk linkedId (visited.Add id) result)
                            (episode :: collected)
                    | _ -> collected

            walk episodeId Set.empty []
            |> List.sortBy (fun episode -> episode.Timestamp)
            |> Task.FromResult

        let synthesizeAsync owner context =
            requireOwner owner

            task {
                let! queryEmbedding = computeEmbedding context

                let similar =
                    owned owner
                    |> Seq.map (fun episode ->
                        let embedding =
                            match embeddings.TryGetValue(key owner episode.Id) with
                            | true, value -> value
                            | _ -> Array.empty

                        episode, SemanticSimilarity.cosineSimilarity queryEmbedding embedding)
                    |> Seq.filter (fun (_, similarity) -> similarity > 0.3)
                    |> Seq.sortByDescending snd
                    |> Seq.truncate 10
                    |> Seq.map fst
                    |> Seq.toList

                let successes =
                    similar
                    |> List.filter (fun episode -> episode.Success)
                    |> List.map (fun episode -> sprintf "When %s -> %s (success)" episode.Action episode.Observation)

                let failures =
                    similar
                    |> List.filter (fun episode -> not episode.Success)
                    |> List.map (fun episode -> sprintf "Avoid: %s -> %s (failed)" episode.Action episode.Observation)

                return successes @ failures
            }

        let delete predicate =
            let matches = episodes.Values |> Seq.filter predicate |> Seq.toArray

            for episode in matches do
                episodes.TryRemove(key episode.Owner episode.Id) |> ignore
                embeddings.TryRemove(key episode.Owner episode.Id) |> ignore

            Task.FromResult matches.Length

        let forgetBelowAsync owner threshold =
            requireOwner owner
            delete (fun episode -> episode.Owner = owner && episode.Importance < threshold)

        let protect owner operation =
            task {
                if String.IsNullOrWhiteSpace owner then
                    return
                        Error(
                            PlatformFailure.create
                                PlatformErrorCategory.InvalidInput
                                "Episodic-memory owner cannot be blank."
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
            protect owner (fun () -> delete (fun episode -> episode.Owner = owner))

        let deleteExpiredAsync owner before =
            protect owner (fun () -> delete (fun episode -> episode.Owner = owner && episode.Timestamp < before))

        { RecordAsync = recordAsync
          QueryAsync = queryAsync
          LinkAsync = linkAsync
          GetChainAsync = getChainAsync
          SynthesizeAsync = synthesizeAsync
          ForgetBelowAsync = forgetBelowAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }
