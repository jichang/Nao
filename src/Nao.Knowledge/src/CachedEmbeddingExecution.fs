namespace Nao.Knowledge

open System
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
module CachedEmbeddingExecution =
    let create (cache: KnowledgeEmbeddingCache) (executor: EmbeddingExecutor) (clock: unit -> DateTimeOffset) =
        if isNull (box cache) || isNull (box executor) || isNull (box clock) then
            invalidArg "cache" "Embedding cache, executor, and clock are required."

        let profile = EmbeddingCapabilityDescriptor.validate executor.Descriptor

        if executor.MaximumInputsPerExecution <= 0 then
            invalidArg (nameof executor) "Cached embedding executor requires a positive execution input limit."

        let cachedExecutor: CachedEmbeddingExecutor =
            { Descriptor = profile
              MaximumInputsPerExecution = executor.MaximumInputsPerExecution
              EmbedAsync =
                fun request cancellationToken ->
                    task {
                        cancellationToken.ThrowIfCancellationRequested()

                        if isNull (box request.Inputs) then
                            nullArg (nameof request.Inputs)

                        if request.Inputs.Length > executor.MaximumInputsPerExecution then
                            invalidArg
                                (nameof request.Inputs)
                                $"Embedding execution exceeds the configured {executor.MaximumInputsPerExecution}-input limit."

                        request.Inputs
                        |> List.iter (fun input ->
                            if isNull input.Text || input.ContentHash <> ContentHash.computeText input.Text then
                                invalidArg (nameof request.Inputs) "Embedding input content hash is invalid.")

                        if
                            (request.Inputs |> List.map _.ChunkId |> Set.ofList).Count
                            <> request.Inputs.Length
                        then
                            invalidArg (nameof request.Inputs) "Embedding inputs cannot contain duplicate chunks."

                        let representatives =
                            request.Inputs
                            |> List.groupBy _.ContentHash
                            |> List.map (fun (contentHash, inputs) -> contentHash, List.head inputs)
                            |> List.sortBy (fun (contentHash, _) -> ContentHash.value contentHash)

                        let keys =
                            representatives
                            |> List.map (fun (contentHash, _) -> EmbeddingCacheKey.derive contentHash profile.ProfileId)

                        let! hits = cache.GetManyAsync request.Authorization keys cancellationToken

                        let misses =
                            representatives
                            |> List.filter (fun (contentHash, _) ->
                                let key = EmbeddingCacheKey.derive contentHash profile.ProfileId
                                not (hits.ContainsKey key))
                            |> List.map snd

                        let! (generated: EmbeddingExecutionResult) =
                            if List.isEmpty misses then
                                let empty: EmbeddingExecutionResult =
                                    { Embeddings = []
                                      Metrics =
                                        { InputCount = 0
                                          BatchCount = 0
                                          ProviderAttempts = 0
                                          InputTokens = 0
                                          EstimatedCostUsd = 0m } }

                                Task.FromResult empty
                            else
                                executor.EmbedAsync
                                    { Authorization = request.Authorization
                                      Inputs = misses }
                                    cancellationToken

                        let generatedByHash =
                            generated.Embeddings
                            |> List.map (fun embedding -> embedding.ContentHash, embedding)
                            |> Map.ofList

                        let newEntries =
                            misses
                            |> List.map (fun input ->
                                let embedding =
                                    generatedByHash
                                    |> Map.tryFind input.ContentHash
                                    |> Option.defaultWith (fun () ->
                                        invalidOp "Embedding executor omitted a requested cache miss.")

                                EmbeddingCacheEntry.create
                                    (AuthorizationScope.identity request.Authorization)
                                    profile
                                    embedding
                                    (clock ()))

                        if not (List.isEmpty newEntries) then
                            do! cache.SaveBatchAsync request.Authorization newEntries cancellationToken

                        let resolved =
                            newEntries
                            |> List.map (fun entry -> EmbeddingCacheEntry.key entry, entry)
                            |> Map.ofList
                            |> Map.fold (fun state key value -> state |> Map.add key value) hits

                        let embeddings =
                            request.Inputs
                            |> List.map (fun input ->
                                let key = EmbeddingCacheKey.derive input.ContentHash profile.ProfileId

                                let entry =
                                    resolved
                                    |> Map.tryFind key
                                    |> Option.defaultWith (fun () ->
                                        invalidOp "Embedding cache did not resolve a requested input.")

                                { ChunkId = input.ChunkId
                                  ContentHash = input.ContentHash
                                  ProfileId = profile.ProfileId
                                  Values = EmbeddingCacheEntry.values entry
                                  GeneratedAt = EmbeddingCacheEntry.generatedAt entry })

                        return
                            { Embeddings = embeddings
                              Metrics =
                                { InputCount = request.Inputs.Length
                                  UniqueContentCount = representatives.Length
                                  CacheHits = hits.Count
                                  CacheMisses = misses.Length
                                  Provider = generated.Metrics } }
                    } }

        cachedExecutor
