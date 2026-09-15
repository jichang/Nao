namespace Nao.E2E.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval
open Nao.Knowledge
open Nao.Persistence

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type EmbeddingObservabilityTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "embedding-observability") (UserId.parse "eval-user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "eval-workspace") None
            |> Option.get

    let descriptor =
        EmbeddingCapabilityDescriptor.create
            (ComponentVersion.create "observed-model" "2026-09")
            2
            EmbeddingNormalization.UnitLength
            VectorDistanceMetric.Cosine
            (ComponentVersion.create "observed-tokenizer" "1")
            (EmbeddingCapabilityLimits.create 8 1000 256 1024)

    let documentVersionId =
        KnowledgeDocumentVersionId.derive
            (KnowledgeDocumentId.parse "observed-document")
            1
            (ContentHash.computeText "observed-document")

    let input =
        let text = "private embedding input"
        let contentHash = ContentHash.computeText text

        { ChunkId = KnowledgeChunkId.derive documentVersionId "observed" 0 contentHash
          ContentHash = contentHash
          Text = text }

    let embeddingExecutor onCall =
        let provider: KnowledgeEmbeddingProvider =
            { Descriptor = descriptor
              CountTokens = fun _ -> 3
              EmbedBatchAsync =
                fun _ batch _ ->
                    onCall ()

                    Task.FromResult(
                        { Embeddings =
                            batch.Inputs
                            |> List.map (fun value ->
                                { ChunkId = value.ChunkId
                                  ContentHash = value.ContentHash
                                  ProfileId = descriptor.ProfileId
                                  Values = [| 1.0; 2.0 |]
                                  GeneratedAt = DateTimeOffset.UtcNow })
                          Usage =
                            { InputTokens = 3
                              EstimatedCostUsd = 0.002m } }
                    )
              Dispose = ignore }

        EmbeddingExecution.create
            provider
            { Retry = EmbeddingRetryPolicy.None
              MaximumInputsPerExecution = 10
              Throttle = { AcquireAsync = fun _ _ _ -> Task.CompletedTask }
              CostAccountant = { RecordAsync = fun _ _ _ -> Task.CompletedTask }
              ProvenanceRecorder = { RecordAsync = fun _ _ _ -> Task.CompletedTask }
              DelayAsync = fun _ _ -> Task.CompletedTask
              Clock = fun () -> DateTimeOffset.UtcNow }

    let context correlation =
        { (AgentContext.unrestrictedForTests ()) with
            Correlation = correlation
            SessionKey = "embedding-observability"
            Authorization = Some authorization }

    let request correlation (agent: Agent) sandbox =
        ExecutionRequest.create
            authorization
            (TurnId.generate ())
            "embedding-observability"
            agent.Metadata.Id
            "embed the document"
            sandbox
            Map.empty
            Map.empty
            correlation

    let agent (executor: EmbeddingExecutor) : Agent =
        Agent.create
            "embedding-agent"
            "embedding-agent"
            "Runs an embedding batch."
            0
            []
            AgentContract.Text
            (fun context _ ->
                task {
                    let scope = AgentContext.requireAuthorization context

                    let! result =
                        executor.EmbedAsync
                            { Authorization = scope
                              Inputs = [ input ] }
                            context.CancellationToken

                    return string result.Embeddings.Length
                })

    [<TestMethod>]
    member _.``Embedding batches appear in budgets metrics spans and evaluation traces``() =
        let mutable providerCalls = 0
        let executor = embeddingExecutor (fun () -> providerCalls <- providerCalls + 1)
        let agent = agent executor
        let tracer = InMemoryTracer.create ()
        let metrics = InMemoryMetricsCollector.create ()

        let harness =
            { EtclovgConfig.Default with
                Tracer = Some tracer
                Metrics = Some metrics }

        let correlation = CorrelationContext.root ()
        let agentContext = context correlation
        let sandbox = SandboxConfig.Default

        let result =
            EtclovgHarness.runAsync
                harness
                agentContext
                agent
                (request correlation agent sandbox)
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        Assert.AreEqual(1, providerCalls)
        Assert.AreEqual(0, result.Usage.LlmCalls)
        Assert.AreEqual(1, result.Usage.EmbeddingCalls)
        Assert.AreEqual(3, result.Usage.TotalTokens)
        Assert.AreEqual(0.002m, result.Usage.EstimatedCostUsd)
        Assert.AreEqual(1, result.Evidence.Metrics.Value.TotalEmbeddingCalls)
        Assert.AreEqual(3, result.Evidence.Metrics.Value.TotalEmbeddingInputTokens)
        Assert.AreEqual(0.002m, result.Evidence.Metrics.Value.TotalEmbeddingCostUsd)

        let metric =
            metrics.GetByExecution authorization result.Correlation.ExecutionId
            |> List.exactlyOne

        match metric.Payload with
        | MetricPayload.EmbeddingCall(profileId, batchId, 1, 3, _, 0.002m, 1) ->
            Assert.AreEqual(EmbeddingProfileId.value descriptor.ProfileId, profileId)
            StringAssert.StartsWith(batchId, "ebatch_")
        | payload -> Assert.Fail($"Unexpected embedding metric: {payload}")

        let spans = tracer.GetByExecution authorization result.Correlation.ExecutionId

        let embeddingSpan =
            spans |> List.find (fun span -> span.OperationName = "embedding.generate")

        Assert.AreEqual(SpanStatus.Ok, embeddingSpan.Status)
        Assert.AreEqual("1", embeddingSpan.Attributes["embedding.input_count"])

        Assert.IsFalse(
            embeddingSpan.Attributes
            |> Seq.exists (fun pair -> pair.Value.Contains(input.Text))
        )

        let trace = result.Evidence.Trace.Value

        Assert.IsTrue(
            trace.Steps
            |> List.exists (fun step ->
                match step.Action with
                | TraceAction.EmbeddingCall profileId ->
                    profileId = EmbeddingProfileId.value descriptor.ProfileId
                    && not (step.Input.Contains(input.Text, StringComparison.Ordinal))
                | _ -> false)
        )

        let evalExecution =
            EvalExecutionConfig.create authorization (fun () -> context (CorrelationContext.root ())) harness sandbox

        let evalRun =
            EvalRun.create (AuthorizationScope.identity authorization) (Guid.NewGuid())

        let evaluator =
            Evaluator.create "pass" (fun _ _ _ -> Task.FromResult(EvalVerdict.Pass, "observed"))

        let evalResult =
            EvalRunner.runCaseAsync
                evalExecution
                evalRun
                evaluator
                agent
                (EvalCase.create "embedding-eval" "embed the document" "1")
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.IsTrue(
            evalResult.ExecutionTrace.Value.Steps
            |> List.exists (fun step ->
                match step.Action with
                | TraceAction.EmbeddingCall _ -> true
                | _ -> false)
        )

    [<TestMethod>]
    member _.``Embedding call budget rejects transport before provider invocation``() =
        let mutable providerCalls = 0

        let agent =
            embeddingExecutor (fun () -> providerCalls <- providerCalls + 1) |> agent

        let correlation = CorrelationContext.root ()

        let sandbox =
            { SandboxConfig.Default with
                Limits =
                    { ResourceLimits.Unlimited with
                        MaxEmbeddingCalls = 0 } }

        let result =
            EtclovgHarness.runAsync
                EtclovgConfig.Default
                (context correlation)
                agent
                (request correlation agent sandbox)
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(ExecutionTerminalStatus.LimitExceeded LimitExceeded.EmbeddingCalls, result.Status)
        Assert.AreEqual(0, providerCalls)
        Assert.AreEqual(0, result.Usage.EmbeddingCalls)
