namespace Nao.Knowledge.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type EmbeddingExecutionTests() =
    let now = DateTimeOffset(2026, 9, 15, 3, 0, 0, TimeSpan.Zero)

    let authorization =
        SecurityPrincipal.create (TenantId.parse "embedding-tenant") (UserId.parse "embedding-user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "embedding-workspace") None
            |> Option.get

    let documentVersionId =
        KnowledgeDocumentVersionId.derive
            (KnowledgeDocumentId.parse "embedding-document")
            1
            (ContentHash.computeText "embedding-document")

    let limits = EmbeddingCapabilityLimits.create 2 1000 4 5

    let descriptor =
        EmbeddingCapabilityDescriptor.create
            (ComponentVersion.create "fixture-embedding-model" "2026-09")
            2
            EmbeddingNormalization.UnitLength
            VectorDistanceMetric.Cosine
            (ComponentVersion.create "fixture-preprocessing" "1")
            limits

    let input sequence text =
        let contentHash = ContentHash.computeText text

        { ChunkId = KnowledgeChunkId.derive documentVersionId "embedding-fixture" sequence contentHash
          ContentHash = contentHash
          Text = text }

    let tokenCount (text: string) =
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length

    let response (batch: EmbeddingProviderBatch) =
        let tokens = batch.Inputs |> List.sumBy (fun item -> tokenCount item.Text)

        { Embeddings =
            batch.Inputs
            |> List.rev
            |> List.map (fun item ->
                { ChunkId = item.ChunkId
                  ContentHash = item.ContentHash
                  ProfileId = descriptor.ProfileId
                  Values = [| float tokens; float item.Text.Length |]
                  GeneratedAt = now })
          Usage =
            { InputTokens = tokens
              EstimatedCostUsd = decimal tokens / 1000m } }

    let config retry throttle accountant provenance delay =
        { Retry = retry
          MaximumInputsPerExecution = 10
          Throttle = throttle
          CostAccountant = accountant
          ProvenanceRecorder = provenance
          DelayAsync = delay
          Clock = fun () -> now }

    let run executor inputs cancellationToken =
        executor.EmbedAsync
            { Authorization = authorization
              Inputs = inputs }
            cancellationToken
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Executor batches by item and token limits while accounting every response``() =
        let providerBatches = ResizeArray<EmbeddingProviderBatch>()
        let throttles = ResizeArray<EmbeddingThrottleRequest>()
        let costs = ResizeArray<EmbeddingCostRecord>()
        let provenance = ResizeArray<EmbeddingProvenanceRecord>()

        let provider: KnowledgeEmbeddingProvider =
            { Descriptor = descriptor
              CountTokens = tokenCount
              EmbedBatchAsync =
                fun scope batch cancellationToken ->
                    cancellationToken.ThrowIfCancellationRequested()
                    Assert.AreEqual(authorization, scope)
                    providerBatches.Add batch
                    response batch |> Task.FromResult
              Dispose = ignore }

        let throttle =
            { AcquireAsync =
                fun scope request cancellationToken ->
                    cancellationToken.ThrowIfCancellationRequested()
                    Assert.AreEqual(authorization, scope)
                    throttles.Add request
                    Task.CompletedTask }

        let accountant =
            { RecordAsync =
                fun scope record cancellationToken ->
                    cancellationToken.ThrowIfCancellationRequested()
                    Assert.AreEqual(authorization, scope)
                    costs.Add record
                    Task.CompletedTask }

        let provenanceRecorder: EmbeddingProvenanceRecorder =
            { RecordAsync =
                fun scope record cancellationToken ->
                    cancellationToken.ThrowIfCancellationRequested()
                    Assert.AreEqual(authorization, scope)
                    provenance.Add record
                    Task.CompletedTask }

        let executor =
            EmbeddingExecution.create
                provider
                (config EmbeddingRetryPolicy.None throttle accountant provenanceRecorder (fun _ _ -> Task.CompletedTask))

        let inputs = [ input 0 "one two"; input 1 "three"; input 2 "four five" ]
        let result = run executor inputs CancellationToken.None

        Assert.AreEqual(2, providerBatches.Count)
        Assert.AreEqual(2, providerBatches[0].Inputs.Length)
        Assert.AreEqual(1, providerBatches[1].Inputs.Length)
        Assert.AreEqual(2, throttles.Count)
        Assert.AreEqual(2, costs.Count)
        Assert.AreEqual(2, provenance.Count)
        Assert.AreEqual(3, result.Metrics.InputCount)
        Assert.AreEqual(2, result.Metrics.BatchCount)
        Assert.AreEqual(2, result.Metrics.ProviderAttempts)
        Assert.AreEqual(5, result.Metrics.InputTokens)
        Assert.AreEqual(0.005m, result.Metrics.EstimatedCostUsd)

        CollectionAssert.AreEqual(
            inputs |> List.map _.ChunkId |> List.toArray,
            result.Embeddings |> List.map _.ChunkId |> List.toArray
        )

        for index in 0 .. providerBatches.Count - 1 do
            Assert.AreEqual(providerBatches[index].Id, throttles[index].BatchId)
            Assert.AreEqual(providerBatches[index].Id, costs[index].BatchId)
            Assert.AreEqual(providerBatches[index].Id, EmbeddingProvenanceRecord.batchId provenance[index])
            Assert.AreEqual(EmbeddingCostRecord.currentVersion, costs[index].Version)

            Assert.AreEqual(
                EmbeddingProvenanceRecord.currentVersion,
                EmbeddingProvenanceRecord.version provenance[index]
            )

            Assert.AreEqual(descriptor.ProfileId, EmbeddingProvenanceRecord.profileId provenance[index])

            let expectedInputs = providerBatches[index].Inputs
            let recordedInputs = EmbeddingProvenanceRecord.inputs provenance[index]
            Assert.AreEqual(expectedInputs.Length, recordedInputs.Length)

            for inputIndex in 0 .. expectedInputs.Length - 1 do
                Assert.AreEqual(
                    expectedInputs[inputIndex].ChunkId,
                    EmbeddingProvenanceInput.chunkId recordedInputs[inputIndex]
                )

                Assert.AreEqual(
                    expectedInputs[inputIndex].ContentHash,
                    EmbeddingProvenanceInput.contentHash recordedInputs[inputIndex]
                )

                Assert.AreEqual(
                    expectedInputs[inputIndex].Text.Length,
                    EmbeddingProvenanceInput.characterCount recordedInputs[inputIndex]
                )

                Assert.AreEqual(
                    tokenCount expectedInputs[inputIndex].Text,
                    EmbeddingProvenanceInput.tokenCount recordedInputs[inputIndex]
                )

            Assert.AreEqual(
                providerBatches[index].Id,
                providerBatches[index].Id |> EmbeddingBatchId.value |> EmbeddingBatchId.parse
            )

        let first = provenance[0]

        let restored =
            EmbeddingProvenanceRecord.restore
                (EmbeddingProvenanceRecord.version first)
                (EmbeddingProvenanceRecord.batchId first)
                (EmbeddingProvenanceRecord.profileId first)
                (EmbeddingProvenanceRecord.inputs first)
                (EmbeddingProvenanceRecord.providerInputTokens first)
                (EmbeddingProvenanceRecord.estimatedCostUsd first)
                (EmbeddingProvenanceRecord.providerAttempts first)
                (EmbeddingProvenanceRecord.startedAt first)
                (EmbeddingProvenanceRecord.completedAt first)

        Assert.AreEqual(first, restored)
        Assert.IsFalse(first.ToString().Contains("one two", StringComparison.Ordinal))
        Assert.IsFalse(first.ToString().Contains("three", StringComparison.Ordinal))

    [<TestMethod>]
    member _.``Retryable failure reacquires throttle and records cost once with stable batch identity``() =
        let throttles = ResizeArray<EmbeddingThrottleRequest>()
        let costs = ResizeArray<EmbeddingCostRecord>()
        let delays = ResizeArray<TimeSpan>()
        let provenance = ResizeArray<EmbeddingProvenanceRecord>()
        let mutable attempts = 0
        let mutable providerBatchId = None

        let provider: KnowledgeEmbeddingProvider =
            { Descriptor = descriptor
              CountTokens = tokenCount
              EmbedBatchAsync =
                fun _ batch _ ->
                    attempts <- attempts + 1

                    match providerBatchId with
                    | None -> providerBatchId <- Some batch.Id
                    | Some expected -> Assert.AreEqual(expected, batch.Id)

                    if attempts = 1 then
                        PlatformFailure.create
                            PlatformErrorCategory.TransientDependency
                            "temporary provider failure"
                            true
                            None
                        |> PlatformFailureException
                        |> Task.FromException<EmbeddingProviderResponse>
                    else
                        response batch |> Task.FromResult
              Dispose = ignore }

        let throttle =
            { AcquireAsync =
                fun _ request _ ->
                    throttles.Add request
                    Task.CompletedTask }

        let accountant =
            { RecordAsync =
                fun _ record _ ->
                    costs.Add record
                    Task.CompletedTask }

        let executor =
            EmbeddingExecution.create
                provider
                (config
                    (EmbeddingRetryPolicy.Fixed(1, TimeSpan.FromSeconds 2.0))
                    throttle
                    accountant
                    { RecordAsync =
                        fun _ record _ ->
                            provenance.Add record
                            Task.CompletedTask }
                    (fun delay cancellationToken ->
                        cancellationToken.ThrowIfCancellationRequested()
                        delays.Add delay
                        Task.CompletedTask))

        let result = run executor [ input 0 "retry me" ] CancellationToken.None

        Assert.AreEqual(2, attempts)
        Assert.AreEqual(2, result.Metrics.ProviderAttempts)
        Assert.AreEqual(2, throttles.Count)
        Assert.AreEqual(1, delays.Count)
        Assert.AreEqual(TimeSpan.FromSeconds 2.0, delays[0])
        Assert.AreEqual(1, costs.Count)
        Assert.AreEqual(1, provenance.Count)
        Assert.AreEqual(2, costs[0].ProviderAttempts)
        Assert.AreEqual(2, EmbeddingProvenanceRecord.providerAttempts provenance[0])
        Assert.AreEqual(throttles[0].BatchId, throttles[1].BatchId)
        Assert.AreEqual(throttles[0].BatchId, costs[0].BatchId)

    [<TestMethod>]
    member _.``Malformed provider output is not retried or costed``() =
        let mutable attempts = 0
        let mutable costRecords = 0
        let mutable provenanceRecords = 0

        let provider: KnowledgeEmbeddingProvider =
            { Descriptor = descriptor
              CountTokens = tokenCount
              EmbedBatchAsync =
                fun _ batch _ ->
                    attempts <- attempts + 1
                    let valid = response batch

                    Task.FromResult(
                        { valid with
                            Embeddings =
                                valid.Embeddings
                                |> List.map (fun embedding -> { embedding with Values = [| 1.0 |] }) }
                    )
              Dispose = ignore }

        let executor =
            EmbeddingExecution.create
                provider
                (config
                    (EmbeddingRetryPolicy.Fixed(3, TimeSpan.Zero))
                    { AcquireAsync = fun _ _ _ -> Task.CompletedTask }
                    { RecordAsync =
                        fun _ _ _ ->
                            costRecords <- costRecords + 1
                            Task.CompletedTask }
                    { RecordAsync =
                        fun _ _ _ ->
                            provenanceRecords <- provenanceRecords + 1
                            Task.CompletedTask }
                    (fun _ _ -> Task.CompletedTask))

        let error =
            Assert.ThrowsExactly<PlatformFailureException>(fun () ->
                run executor [ input 0 "invalid vector" ] CancellationToken.None |> ignore)

        let failure =
            match error :> exn with
            | PlatformFailureException failure -> failure
            | unexpected ->
                Assert.Fail($"Expected platform failure, got {unexpected.GetType().Name}.")
                Unchecked.defaultof<PlatformFailure>

        Assert.AreEqual(PlatformErrorCategory.InvalidOutput, failure.Category)
        Assert.IsFalse(failure.Retryable)

        Assert.AreEqual(1, attempts)
        Assert.AreEqual(0, costRecords)
        Assert.AreEqual(0, provenanceRecords)

    [<TestMethod>]
    member _.``Provenance recording failure prevents successful completion and accounting``() =
        let mutable costRecords = 0

        let provider: KnowledgeEmbeddingProvider =
            { Descriptor = descriptor
              CountTokens = tokenCount
              EmbedBatchAsync = fun _ batch _ -> response batch |> Task.FromResult
              Dispose = ignore }

        let executor =
            EmbeddingExecution.create
                provider
                (config
                    EmbeddingRetryPolicy.None
                    { AcquireAsync = fun _ _ _ -> Task.CompletedTask }
                    { RecordAsync =
                        fun _ _ _ ->
                            costRecords <- costRecords + 1
                            Task.CompletedTask }
                    { RecordAsync = fun _ _ _ -> Task.FromException(InvalidOperationException "provenance unavailable") }
                    (fun _ _ -> Task.CompletedTask))

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            run executor [ input 0 "must be attributable" ] CancellationToken.None |> ignore)
        |> ignore

        Assert.AreEqual(0, costRecords)

    [<TestMethod>]
    member _.``Cancellation prevents throttle provider and accounting work``() =
        let mutable calls = 0

        let provider: KnowledgeEmbeddingProvider =
            { Descriptor = descriptor
              CountTokens = tokenCount
              EmbedBatchAsync =
                fun _ batch _ ->
                    calls <- calls + 1
                    response batch |> Task.FromResult
              Dispose = ignore }

        let executor =
            EmbeddingExecution.create
                provider
                (config
                    EmbeddingRetryPolicy.None
                    { AcquireAsync =
                        fun _ _ _ ->
                            calls <- calls + 1
                            Task.CompletedTask }
                    { RecordAsync =
                        fun _ _ _ ->
                            calls <- calls + 1
                            Task.CompletedTask }
                    { RecordAsync =
                        fun _ _ _ ->
                            calls <- calls + 1
                            Task.CompletedTask }
                    (fun _ _ -> Task.CompletedTask))

        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        Assert.ThrowsExactly<OperationCanceledException>(fun () ->
            run executor [ input 0 "cancelled" ] cancellation.Token |> ignore)
        |> ignore

        Assert.AreEqual(0, calls)
