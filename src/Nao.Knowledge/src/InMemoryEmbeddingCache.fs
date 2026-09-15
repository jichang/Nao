namespace Nao.Knowledge

open System
open System.Collections.Generic
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
module KnowledgeEmbeddingCaches =
    let inMemory () =
        let gate = obj ()

        let partitions =
            Dictionary<string, Dictionary<EmbeddingCacheKey, EmbeddingCacheEntry>>()

        let partition authorization =
            authorization |> AuthorizationScope.identity |> AuthorizationIdentity.storageKey

        { GetManyAsync =
            fun authorization keys cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()

                if isNull (box keys) || (keys |> Set.ofList).Count <> keys.Length then
                    invalidArg (nameof keys) "Embedding cache lookup keys must be non-null and unique."

                let partitionKey = partition authorization

                lock gate (fun () ->
                    match partitions.TryGetValue partitionKey with
                    | true, records ->
                        keys
                        |> List.choose (fun key ->
                            match records.TryGetValue key with
                            | true, value -> Some(key, EmbeddingCacheEntry.clone value)
                            | false, _ -> None)
                        |> Map.ofList
                    | false, _ -> Map.empty)
                |> Task.FromResult
          SaveBatchAsync =
            fun authorization entries cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()

                if isNull (box entries) then
                    nullArg (nameof entries)

                let identity = AuthorizationScope.identity authorization
                let partitionKey = partition authorization

                entries
                |> List.iter (fun entry ->
                    if EmbeddingCacheEntry.owner entry <> identity then
                        raise (UnauthorizedAccessException "Embedding cache entry belongs to another identity."))

                if
                    (entries |> List.map EmbeddingCacheEntry.key |> Set.ofList).Count
                    <> entries.Length
                then
                    invalidArg (nameof entries) "Embedding cache batch contains duplicate keys."

                lock gate (fun () ->
                    let records =
                        match partitions.TryGetValue partitionKey with
                        | true, existing -> existing
                        | false, _ ->
                            let created = Dictionary()
                            partitions.Add(partitionKey, created)
                            created

                    entries
                    |> List.iter (fun entry ->
                        let key = EmbeddingCacheEntry.key entry

                        match records.TryGetValue key with
                        | true, existing when not (EmbeddingCacheEntry.equivalent existing entry) ->
                            invalidOp "Embedding cache key is already bound to different vector data."
                        | _ -> ())

                    entries
                    |> List.iter (fun entry ->
                        let key = EmbeddingCacheEntry.key entry

                        if not (records.ContainsKey key) then
                            records.Add(key, EmbeddingCacheEntry.clone entry)))

                Task.CompletedTask }
