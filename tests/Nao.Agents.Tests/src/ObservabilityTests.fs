namespace Nao.Agents.Tests

open System
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass>]
type TraceTests() =
    let correlation = CorrelationContext.root ()

    let scope =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "trace-tests") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    [<TestMethod>]
    member _.StartTraceCreatesRootSpan() =
        let tracer = InMemory.tracer ()
        let span = tracer.StartTrace scope correlation "test-op"
        Assert.AreEqual("test-op", span.OperationName)
        Assert.IsTrue(span.ParentSpanId.IsNone)
        Assert.IsTrue(span.EndTime.IsNone)

    [<TestMethod>]
    member _.StartSpanCreatesChild() =
        let tracer = InMemory.tracer ()
        let root = tracer.StartTrace scope correlation "root"
        let child = tracer.StartSpan scope root "child-op"
        Assert.AreEqual("child-op", child.OperationName)
        Assert.AreEqual(Some root.Id, child.ParentSpanId)
        Assert.AreEqual(root.TraceId, child.TraceId)
        Assert.AreEqual(correlation, child.Correlation)

    [<TestMethod>]
    member _.EndSpanSetsEndTimeAndStatus() =
        let tracer = InMemory.tracer ()
        let span = tracer.StartTrace scope correlation "op"
        tracer.EndSpan scope span SpanStatus.Ok
        let traces = tracer.GetTrace scope span.TraceId
        Assert.AreEqual(1, traces.Length)
        Assert.IsTrue(traces.[0].EndTime.IsSome)
        Assert.AreEqual(SpanStatus.Ok, traces.[0].Status)

    [<TestMethod>]
    member _.EndSpanWithError() =
        let tracer = InMemory.tracer ()
        let span = tracer.StartTrace scope correlation "op"
        tracer.EndSpan scope span (SpanStatus.Error TelemetryErrorCategory.Internal)
        let traces = tracer.GetTrace scope span.TraceId

        match traces.[0].Status with
        | SpanStatus.Error category -> Assert.AreEqual(TelemetryErrorCategory.Internal, category)
        | _ -> Assert.Fail("Expected error status")

    [<TestMethod>]
    member _.AddEventAppendsToSpan() =
        let tracer = InMemory.tracer ()
        let span = tracer.StartTrace scope correlation "op"
        tracer.AddEvent scope span "something-happened" (Map.ofList [ "key", "value" ])
        let traces = tracer.GetTrace scope span.TraceId
        Assert.AreEqual(1, traces.[0].Events.Length)
        Assert.AreEqual("something-happened", traces.[0].Events.[0].Name)

    [<TestMethod>]
    member _.SetAttributesMergesWithExisting() =
        let tracer = InMemory.tracer ()
        let span = tracer.StartTrace scope correlation "op"
        tracer.SetAttributes scope span (Map.ofList [ "env", "test" ])
        // After first SetAttributes, get the updated span from the store
        let updated = tracer.GetTrace scope span.TraceId |> List.head
        tracer.SetAttributes scope updated (Map.ofList [ "version", "1.0" ])
        let traces = tracer.GetTrace scope span.TraceId
        Assert.IsTrue(traces.[0].Attributes.ContainsKey("env"))
        Assert.IsTrue(traces.[0].Attributes.ContainsKey("version"))

    [<TestMethod>]
    member _.GetTraceReturnsAllSpansForTrace() =
        let tracer = InMemory.tracer ()
        let root = tracer.StartTrace scope correlation "root"
        let _child1 = tracer.StartSpan scope root "child1"
        let _child2 = tracer.StartSpan scope root "child2"
        let traces = tracer.GetTrace scope root.TraceId
        Assert.AreEqual(3, traces.Length)

[<TestClass>]
type MetricsTests() =
    let scope =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "metrics-tests") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    let timestamp = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    let correlation = CorrelationContext.root ()

    [<TestMethod>]
    member _.RecordLlmCallTracksTokensAndCalls() =
        let collector = InMemory.metrics ()
        collector.Record scope (MetricRecord.llmCall correlation timestamp (ProviderTokenUsage.Split(100, 50)) 200L)

        collector.Record
            scope
            (MetricRecord.llmCall correlation (timestamp.AddSeconds 1) (ProviderTokenUsage.Split(200, 100)) 300L)

        let metrics = collector.GetMetrics scope
        Assert.AreEqual(2, metrics.TotalLlmCalls)
        Assert.AreEqual(Some 300, metrics.TotalInputTokens)
        Assert.AreEqual(Some 150, metrics.TotalOutputTokens)

    [<TestMethod>]
    member _.RecordToolCallTracksCount() =
        let collector = InMemory.metrics ()
        collector.Record scope (MetricRecord.toolCall correlation timestamp "search" 50L true)
        collector.Record scope (MetricRecord.toolCall correlation (timestamp.AddSeconds 1) "calc" 30L false)
        let metrics = collector.GetMetrics scope
        Assert.AreEqual(2, metrics.TotalToolCalls)

    [<TestMethod>]
    member _.RecordEmbeddingCallTracksDedicatedUsageAndCost() =
        let collector = InMemory.metrics ()

        collector.Record scope (MetricRecord.embeddingCall correlation timestamp "profile" "batch" 2 40 125L 0.003m 2)

        let metrics = collector.GetMetrics scope
        Assert.AreEqual(0, metrics.TotalLlmCalls)
        Assert.AreEqual(1, metrics.TotalEmbeddingCalls)
        Assert.AreEqual(40, metrics.TotalEmbeddingInputTokens)
        Assert.AreEqual(0.003m, metrics.TotalEmbeddingCostUsd)
        Assert.AreEqual(0.003m, metrics.TotalCostUsd)
        Assert.AreEqual(TimeSpan.FromMilliseconds 125.0, metrics.EmbeddingWaitTime)

    [<TestMethod>]
    member _.AvgLatencyCalculatedCorrectly() =
        let collector = InMemory.metrics ()
        collector.Record scope (MetricRecord.llmCall correlation timestamp (ProviderTokenUsage.Split(100, 50)) 100L)

        collector.Record
            scope
            (MetricRecord.llmCall correlation (timestamp.AddSeconds 1) (ProviderTokenUsage.Split(100, 50)) 300L)

        let metrics = collector.GetMetrics scope
        Assert.AreEqual(200.0, metrics.AvgLatencyMs)

    [<TestMethod>]
    member _.EstimateCostUsesModel() =
        let collector = InMemory.metrics ()
        collector.Record scope (MetricRecord.llmCall correlation timestamp (ProviderTokenUsage.Split(1000, 500)) 100L)

        let model = CostModel.create "test-pricing" "1" 0.0025m 0.01m

        let cost = collector.EstimateCost scope model

        Assert.AreEqual(
            Some
                { PricingId = "test-pricing"
                  PricingVersion = "1"
                  AmountUsd = 0.0075m },
            cost
        )

    [<TestMethod>]
    member _.ProviderTokenFidelityDoesNotInventUnavailableSplits() =
        let collector = InMemory.metrics ()

        collector.Record scope (MetricRecord.llmCall correlation timestamp (ProviderTokenUsage.Aggregate 150) 100L)

        let aggregateOnly = collector.GetMetrics scope
        Assert.AreEqual(None, aggregateOnly.TotalInputTokens)
        Assert.AreEqual(None, aggregateOnly.TotalOutputTokens)
        Assert.AreEqual(Some 150, aggregateOnly.TotalProviderTokens)

        let pricing = CostModel.create "test-pricing" "1" 0.001m 0.002m
        Assert.AreEqual(None, collector.EstimateCost scope pricing)

        collector.Record
            scope
            (MetricRecord.llmCall correlation (timestamp.AddSeconds 1.0) ProviderTokenUsage.Unavailable 100L)

        Assert.AreEqual(None, (collector.GetMetrics scope).TotalProviderTokens)

    [<TestMethod>]
    member _.ZeroMetricsOnFreshCollector() =
        let collector = InMemory.metrics ()
        let metrics = collector.GetMetrics scope
        Assert.AreEqual(0, metrics.TotalLlmCalls)
        Assert.AreEqual(Some 0, metrics.TotalInputTokens)
        Assert.AreEqual(0, metrics.TotalToolCalls)
        Assert.AreEqual(0.0, metrics.AvgLatencyMs)

[<TestClass>]
type TelemetrySchemaTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate
                principal
                None
                (WorkspaceId.parse "workspace")
                (Some(SessionId.parse "session"))
            |> Option.get

    [<TestMethod>]
    member _.SchemaV1NamesAndBoundedDimensionsRemainStable() =
        Assert.AreEqual(1, TelemetrySchema.CurrentVersion)

        CollectionAssert.AreEqual(
            [| "nao.execution.count"
               "nao.execution.duration"
               "nao.provider.call.count"
               "nao.provider.call.duration"
               "nao.provider.token.count"
               "nao.tool.call.count"
               "nao.tool.call.duration"
               "nao.retrieval.duration"
               "nao.persistence.operation.duration"
               "nao.execution.active"
               "nao.health.status"
               "nao.health.check.duration"
               "nao.saturation.utilization"
               "nao.saturation.rejected"
               "nao.provider.cost"
               "nao.provider.retry.count"
               "nao.circuit.state"
               "nao.policy.block.count"
               "nao.retrieval.quality"
               "nao.evaluation.drift" |],
            TelemetryInstrument.definitions
            |> List.map TelemetryInstrument.name
            |> List.toArray
        )

        let point =
            { Name = "nao.execution.active"
              Value = 1.0
              Labels = Map.ofList [ TelemetryAttribute.AgentId, "agent" ] }

        MetricRecord.custom (CorrelationContext.root ()) DateTimeOffset.UtcNow point
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            MetricRecord.custom
                (CorrelationContext.root ())
                DateTimeOffset.UtcNow
                { point with
                    Labels = Map.ofList [ "user.content", "sensitive prompt" ] }
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.ExecutionAttributesAndTraceIdentityPreserveLineageWithoutContent() =
        let root = CorrelationContext.root ()
        let delegated = CorrelationContext.delegateFrom root
        let retry = CorrelationContext.retry delegated

        let attributes =
            TelemetryAttributes.execution authorization (TurnId.parse "turn") "agent" retry

        Assert.AreEqual(
            ExecutionId.serialize delegated.ExecutionId,
            attributes.[TelemetryAttribute.CausationExecutionId]
        )

        Assert.AreEqual("2", attributes.[TelemetryAttribute.Attempt])
        Assert.IsFalse(attributes |> Map.containsKey "input")

        let tracer = InMemory.tracer ()
        let rootSpan = tracer.StartTrace authorization root TelemetrySpan.Harness
        let delegatedSpan = tracer.StartTrace authorization delegated TelemetrySpan.Harness
        Assert.AreEqual(rootSpan.TraceId, delegatedSpan.TraceId)
        Assert.AreEqual(TraceId.ofCorrelation root.CorrelationId, rootSpan.TraceId)

[<TestClass>]
type ResilienceTests() =

    [<TestMethod>]
    member _.ExecuteAsyncSucceedsOnFirstTry() =
        let config = ResilienceConfig.NoResilience
        let execute = fun (input: string) -> Task.FromResult(sprintf "ok:%s" input)
        let result = (Resilience.executeAsync config None execute "test").Result

        match result with
        | Ok v -> Assert.AreEqual("ok:test", v)
        | Error _ -> Assert.Fail("Expected Ok")

    [<TestMethod>]
    member _.ExecuteAsyncRetriesOnFailure() =
        let mutable attempts = 0

        let config =
            { ResilienceConfig.NoResilience with
                RetryPolicy = RetryPolicy.Fixed(3, 10) }

        let execute =
            fun (_: string) ->
                task {
                    attempts <- attempts + 1

                    if attempts < 3 then
                        failwith "transient"

                    return "success"
                }

        let result = (Resilience.executeAsync config None execute "x").Result

        match result with
        | Ok v -> Assert.AreEqual("success", v)
        | Error _ -> Assert.Fail("Expected Ok after retries")

        Assert.AreEqual(3, attempts)

    [<TestMethod>]
    member _.ExecuteAsyncReturnsErrorWhenRetriesExhausted() =
        let config =
            { ResilienceConfig.NoResilience with
                RetryPolicy = RetryPolicy.Fixed(2, 10) }

        let execute = fun (_: string) -> failwith "always fails": Task<string>
        let result = (Resilience.executeAsync config None execute "x").Result

        match result with
        | Error msg -> Assert.IsTrue(msg.Contains("always fails"))
        | Ok _ -> Assert.Fail("Expected Error")

    [<TestMethod>]
    member _.CircuitBreakerOpensAfterThreshold() =
        let config = CircuitBreakerConfig.Default
        let cb = CircuitBreaker.create config
        Assert.IsTrue(cb.CanExecute())
        // Record failures to open the circuit
        for _ in 1 .. config.FailureThreshold do
            cb.RecordFailure()

        Assert.IsFalse(cb.CanExecute())

    [<TestMethod>]
    member _.CircuitBreakerClosesAfterSuccesses() =
        let config =
            { CircuitBreakerConfig.Default with
                FailureThreshold = 1
                OpenDuration = TimeSpan.FromMilliseconds(50.0)
                SuccessThreshold = 1 }

        let cb = CircuitBreaker.create config
        cb.RecordFailure() // opens circuit
        Assert.IsFalse(cb.CanExecute()) // open, not enough time passed
        // Wait for open duration to expire
        System.Threading.Thread.Sleep(60)
        Assert.IsTrue(cb.CanExecute()) // half-open now
        cb.RecordSuccess()
        Assert.IsTrue(cb.CanExecute()) // closed

    [<TestMethod>]
    member _.FallbackDefaultValueOnFailure() =
        let config =
            { ResilienceConfig.NoResilience with
                RetryPolicy = RetryPolicy.None
                Fallback = FallbackStrategy.DefaultValue "fallback-val" }

        let execute = fun (_: string) -> failwith "fail": Task<string>
        let result = (Resilience.executeAsync config None execute "x").Result

        match result with
        | Ok v -> Assert.AreEqual("fallback-val", v)
        | Error _ -> Assert.Fail("Expected fallback value")
