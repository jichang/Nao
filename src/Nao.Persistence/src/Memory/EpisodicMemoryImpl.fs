namespace Nao.Persistence

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Nao.Agents

module internal EpisodicEmbedding =
    let compute (provider: EmbeddingProvider option) (text: string) =
        task {
            match provider with
            | Some provider -> return! provider.EmbedAsync text
            | None ->
                let vector = Array.zeroCreate 64

                for word in text.ToLowerInvariant().Split(' ') |> Array.distinct do
                    let hash = SHA256.HashData(Encoding.UTF8.GetBytes word)
                    let index = int hash.[0] % vector.Length
                    vector.[index] <- vector.[index] + 1.0

                return vector
        }

    let episodeText (episode: Episode) =
        sprintf "%s %s %s" episode.Action episode.Observation episode.Context

/// Shared projection accepts already-computed embeddings so replay never calls a provider.
type internal EpisodicProjection() =
    let sync = obj ()
    let entries = Dictionary<string * string, Episode * float array>()
    let ownerKey = AuthorizationScope.identity >> AuthorizationIdentity.storageKey

    let owned owner =
        entries
        |> Seq.choose (fun pair -> if fst pair.Key = owner then Some pair.Value else None)
        |> Seq.toList

    let related owner episodeId maxHops =
        let rec collect frontier visited depth =
            if depth >= maxHops || Set.isEmpty frontier then
                visited
            else
                let neighbors =
                    frontier
                    |> Seq.collect (fun id ->
                        match entries.TryGetValue((owner, id)) with
                        | true, (episode, _) -> episode.LinkedEpisodes
                        | _ -> [])
                    |> Set.ofSeq
                    |> fun found -> Set.difference found visited

                collect neighbors (Set.union visited neighbors) (depth + 1)

        collect (Set.singleton episodeId) (Set.singleton episodeId) 0

    member _.Record scope (episode: Episode) embedding =
        lock sync (fun () -> entries.[(ownerKey scope, episode.Id)] <- episode, Array.copy embedding)

    member _.Query scope query queryEmbedding =
        lock sync (fun () ->
            let owner = ownerKey scope
            let episodes = owned owner

            match query with
            | EpisodeQuery.BySimilarity(_, topK) ->
                episodes
                |> List.sortByDescending (fun (_, embedding) ->
                    SemanticSimilarity.cosineSimilarity queryEmbedding embedding)
                |> List.truncate topK
                |> List.map fst
            | EpisodeQuery.ByTimeRange(from', to') ->
                episodes
                |> List.map fst
                |> List.filter (fun episode -> episode.Timestamp >= from' && episode.Timestamp <= to')
                |> List.sortByDescending _.Timestamp
            | EpisodeQuery.ByTags tags ->
                let tagSet = Set.ofList tags

                episodes
                |> List.map fst
                |> List.filter (fun episode -> episode.Tags |> List.exists tagSet.Contains)
                |> List.sortByDescending _.Importance
            | EpisodeQuery.Recent count ->
                episodes
                |> List.map fst
                |> List.sortByDescending _.Timestamp
                |> List.truncate count
            | EpisodeQuery.Related(episodeId, maxHops) ->
                related owner episodeId maxHops
                |> Set.toList
                |> List.choose (fun id ->
                    match entries.TryGetValue((owner, id)) with
                    | true, (episode, _) -> Some episode
                    | _ -> None)
            | EpisodeQuery.ByOutcome(success, topK) ->
                episodes
                |> List.map fst
                |> List.filter (fun episode -> episode.Success = success)
                |> List.sortByDescending _.Importance
                |> List.truncate topK)

    member _.Link scope fromId toId =
        lock sync (fun () ->
            let owner = ownerKey scope

            match entries.TryGetValue((owner, fromId)) with
            | true, (episode, embedding) when
                entries.ContainsKey((owner, toId))
                && not (List.contains toId episode.LinkedEpisodes)
                ->
                entries.[(owner, fromId)] <-
                    { episode with
                        LinkedEpisodes = toId :: episode.LinkedEpisodes },
                    embedding
            | _ -> ())

    member _.GetChain scope episodeId =
        lock sync (fun () ->
            let owner = ownerKey scope

            related owner episodeId Int32.MaxValue
            |> Set.toList
            |> List.choose (fun id ->
                match entries.TryGetValue((owner, id)) with
                | true, (episode, _) -> Some episode
                | _ -> None)
            |> List.sortBy _.Timestamp)

    member _.Synthesize scope queryEmbedding =
        lock sync (fun () ->
            let similar =
                owned (ownerKey scope)
                |> List.map (fun (episode, embedding) ->
                    episode, SemanticSimilarity.cosineSimilarity queryEmbedding embedding)
                |> List.filter (fun (_, similarity) -> similarity > 0.3)
                |> List.sortByDescending snd
                |> List.truncate 10
                |> List.map fst

            let successes =
                similar
                |> List.filter _.Success
                |> List.map (fun episode -> sprintf "When %s -> %s (success)" episode.Action episode.Observation)

            let failures =
                similar
                |> List.filter (fun episode -> not episode.Success)
                |> List.map (fun episode -> sprintf "Avoid: %s -> %s (failed)" episode.Action episode.Observation)

            successes @ failures)

    member _.Delete scope predicate =
        lock sync (fun () ->
            let owner = ownerKey scope
            let matches = owned owner |> List.map fst |> List.filter predicate

            for episode in matches do
                entries.Remove((owner, episode.Id)) |> ignore

            matches.Length)

module InMemoryEpisodicMemory =
    let create (embeddingProvider: EmbeddingProvider option) : EpisodicMemory =
        let projection = EpisodicProjection()

        { RecordAsync =
            fun scope episode ->
                task {
                    let! embedding = EpisodicEmbedding.compute embeddingProvider (EpisodicEmbedding.episodeText episode)
                    projection.Record scope episode embedding
                }
          QueryAsync =
            fun scope query ->
                task {
                    let! embedding =
                        match query with
                        | EpisodeQuery.BySimilarity(text, _) -> EpisodicEmbedding.compute embeddingProvider text
                        | _ -> System.Threading.Tasks.Task.FromResult [||]

                    return projection.Query scope query embedding
                }
          LinkAsync = fun scope fromId toId -> task { projection.Link scope fromId toId }
          GetChainAsync = fun scope id -> task { return projection.GetChain scope id }
          SynthesizeAsync =
            fun scope text ->
                task {
                    let! embedding = EpisodicEmbedding.compute embeddingProvider text
                    return projection.Synthesize scope embedding
                }
          ForgetBelowAsync =
            fun scope threshold ->
                task { return projection.Delete scope (fun episode -> episode.Importance < threshold) }
          DeleteOwnerAsync = fun scope -> task { return Ok(projection.Delete scope (fun _ -> true)) }
          DeleteExpiredAsync =
            fun scope before -> task { return Ok(projection.Delete scope (fun episode -> episode.Timestamp < before)) } }
