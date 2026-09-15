namespace Nao.Knowledge

open System
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
module EmbeddingMigration =
    let private canonicalInputs (inputs: EmbeddingInput list) =
        if isNull (box inputs) || List.isEmpty inputs then
            invalidArg (nameof inputs) "An embedding migration requires at least one input."

        inputs
        |> List.iter (fun input ->
            if isNull input.Text || input.ContentHash <> ContentHash.computeText input.Text then
                invalidArg (nameof inputs) "Embedding migration input content hash is invalid.")

        inputs
        |> List.groupBy _.ContentHash
        |> List.map (fun (contentHash, values) -> contentHash, List.head values)
        |> List.sortBy (fst >> ContentHash.value)

    let create
        (cache: KnowledgeEmbeddingCache)
        (store: KnowledgeEmbeddingMigrationStore)
        (clock: unit -> DateTimeOffset)
        =
        if isNull (box cache) || isNull (box store) || isNull (box clock) then
            invalidArg "cache" "Embedding cache, migration store, and clock are required."

        { RunAsync =
            fun request cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()
                    let source = EmbeddingCapabilityDescriptor.validate request.Source
                    let target = EmbeddingCapabilityDescriptor.validate request.Target.Descriptor

                    if source.ProfileId = target.ProfileId then
                        invalidArg (nameof request) "Embedding migration source and target profiles must differ."

                    if request.Target.MaximumInputsPerExecution <= 0 then
                        invalidArg (nameof request) "Target embedding executor requires a positive input limit."

                    let representatives = canonicalInputs request.Inputs
                    let contentHashes = representatives |> List.map fst

                    let initial =
                        EmbeddingMigrationCheckpoint.create
                            (AuthorizationScope.identity request.Authorization)
                            source
                            target
                            contentHashes
                            request.RequestedAt

                    let! stored =
                        store.LoadAsync
                            request.Authorization
                            (EmbeddingMigrationCheckpoint.id initial)
                            cancellationToken

                    let mutable checkpoint =
                        match stored with
                        | Some existing when
                            EmbeddingMigrationCheckpoint.owner existing = EmbeddingMigrationCheckpoint.owner initial
                            && EmbeddingMigrationCheckpoint.source existing = source
                            && EmbeddingMigrationCheckpoint.target existing = target
                            && EmbeddingMigrationCheckpoint.planHash existing = EmbeddingMigrationCheckpoint.planHash
                                initial
                            && EmbeddingMigrationCheckpoint.contentHashes existing = contentHashes
                            ->
                            existing
                        | Some _ -> invalidOp "Stored embedding-migration checkpoint does not match the requested plan."
                        | None -> initial

                    let sourceKeys =
                        contentHashes
                        |> List.map (fun hash -> EmbeddingCacheKey.derive hash source.ProfileId)

                    let! sourceEntries = cache.GetManyAsync request.Authorization sourceKeys cancellationToken

                    if sourceEntries.Count <> sourceKeys.Length then
                        invalidOp "Embedding migration requires every source-profile cache entry to exist."

                    let byHash = representatives |> Map.ofList
                    let metrics = ResizeArray<CachedEmbeddingExecutionMetrics>()
                    let mutable migrated = 0

                    while EmbeddingMigrationCheckpoint.completedAt checkpoint |> Option.isNone do
                        cancellationToken.ThrowIfCancellationRequested()
                        let start = EmbeddingMigrationCheckpoint.nextContentIndex checkpoint

                        let batchHashes =
                            contentHashes
                            |> List.skip start
                            |> List.truncate request.Target.MaximumInputsPerExecution

                        let batchInputs = batchHashes |> List.map (fun hash -> byHash[hash])

                        let! result =
                            request.Target.EmbedAsync
                                { Authorization = request.Authorization
                                  Inputs = batchInputs }
                                cancellationToken

                        let next =
                            EmbeddingMigrationCheckpoint.advance batchInputs.Length (clock ()) checkpoint

                        do!
                            store.SaveAsync
                                request.Authorization
                                (if EmbeddingMigrationCheckpoint.revision checkpoint = 0 then
                                     None
                                 else
                                     Some checkpoint)
                                next
                                cancellationToken

                        checkpoint <- next
                        migrated <- migrated + batchInputs.Length
                        metrics.Add result.Metrics

                    return
                        { Checkpoint = checkpoint
                          MigratedContentCount = migrated
                          ExecutionMetrics = List.ofSeq metrics }
                } }

[<RequireQualifiedAccess>]
module EmbeddingUpgradeReads =
    let create
        (cache: KnowledgeEmbeddingCache)
        (source: EmbeddingCapabilityDescriptor)
        (target: EmbeddingCapabilityDescriptor)
        =
        if isNull (box cache) then
            nullArg (nameof cache)

        let source = EmbeddingCapabilityDescriptor.validate source
        let target = EmbeddingCapabilityDescriptor.validate target

        if source.ProfileId = target.ProfileId then
            invalidArg (nameof target) "Embedding upgrade source and target profiles must differ."

        let readProfile
            (authorization: AuthorizationScope)
            (inputs: EmbeddingContentReference list)
            (profile: EmbeddingCapabilityDescriptor)
            cancellationToken
            =
            task {
                let keys =
                    inputs
                    |> List.map (fun input -> EmbeddingCacheKey.derive input.ContentHash profile.ProfileId)
                    |> List.distinct

                let! entries = cache.GetManyAsync authorization keys cancellationToken

                if entries.Count <> keys.Length then
                    return None
                else
                    let embeddings: GeneratedEmbedding list =
                        inputs
                        |> List.map (fun input ->
                            let entry = entries[EmbeddingCacheKey.derive input.ContentHash profile.ProfileId]

                            { ChunkId = input.ChunkId
                              ContentHash = input.ContentHash
                              ProfileId = profile.ProfileId
                              Values = EmbeddingCacheEntry.values entry
                              GeneratedAt = EmbeddingCacheEntry.generatedAt entry })

                    let result: EmbeddingUpgradeReadResult =
                        { Profile = profile
                          Embeddings = embeddings }

                    return Some result
            }

        { ResolveAsync =
            fun request cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    if isNull (box request.Inputs) || List.isEmpty request.Inputs then
                        invalidArg (nameof request.Inputs) "An embedding read requires at least one input."

                    if
                        (request.Inputs |> List.map _.ChunkId |> Set.ofList).Count
                        <> request.Inputs.Length
                    then
                        invalidArg (nameof request.Inputs) "Embedding read inputs cannot contain duplicate chunks."

                    let! result =
                        match request.Mode with
                        | EmbeddingReadMode.SourceOnly ->
                            readProfile request.Authorization request.Inputs source cancellationToken
                        | EmbeddingReadMode.TargetOnly ->
                            readProfile request.Authorization request.Inputs target cancellationToken
                        | EmbeddingReadMode.TargetFirst ->
                            task {
                                let! targetResult =
                                    readProfile request.Authorization request.Inputs target cancellationToken

                                match targetResult with
                                | Some value -> return Some value
                                | None ->
                                    return! readProfile request.Authorization request.Inputs source cancellationToken
                            }

                    return
                        result
                        |> Option.defaultWith (fun () ->
                            invalidOp "The selected embedding read mode has no complete compatible profile result.")
                } }
