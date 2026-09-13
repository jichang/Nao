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
        tracer.EndSpan scope span (SpanStatus.Error "failed")
        let traces = tracer.GetTrace scope span.TraceId

        match traces.[0].Status with
        | SpanStatus.Error msg -> Assert.AreEqual("failed", msg)
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
        collector.Record scope (MetricRecord.llmCall correlation timestamp 100 50 200L)
        collector.Record scope (MetricRecord.llmCall correlation (timestamp.AddSeconds 1) 200 100 300L)
        let metrics = collector.GetMetrics scope
        Assert.AreEqual(2, metrics.TotalLlmCalls)
        Assert.AreEqual(300, metrics.TotalInputTokens)
        Assert.AreEqual(150, metrics.TotalOutputTokens)

    [<TestMethod>]
    member _.RecordToolCallTracksCount() =
        let collector = InMemory.metrics ()
        collector.Record scope (MetricRecord.toolCall correlation timestamp "search" 50L true)
        collector.Record scope (MetricRecord.toolCall correlation (timestamp.AddSeconds 1) "calc" 30L false)
        let metrics = collector.GetMetrics scope
        Assert.AreEqual(2, metrics.TotalToolCalls)

    [<TestMethod>]
    member _.AvgLatencyCalculatedCorrectly() =
        let collector = InMemory.metrics ()
        collector.Record scope (MetricRecord.llmCall correlation timestamp 100 50 100L)
        collector.Record scope (MetricRecord.llmCall correlation (timestamp.AddSeconds 1) 100 50 300L)
        let metrics = collector.GetMetrics scope
        Assert.AreEqual(200.0, metrics.AvgLatencyMs)

    [<TestMethod>]
    member _.EstimateCostUsesModel() =
        let collector = InMemory.metrics ()
        collector.Record scope (MetricRecord.llmCall correlation timestamp 1000 500 100L)

        let model: CostModel =
            { InputCostPer1K = 0.0025m
              OutputCostPer1K = 0.01m }

        let cost = collector.EstimateCost scope model
        Assert.AreEqual(0.0075m, cost)

    [<TestMethod>]
    member _.ZeroMetricsOnFreshCollector() =
        let collector = InMemory.metrics ()
        let metrics = collector.GetMetrics scope
        Assert.AreEqual(0, metrics.TotalLlmCalls)
        Assert.AreEqual(0, metrics.TotalInputTokens)
        Assert.AreEqual(0, metrics.TotalToolCalls)
        Assert.AreEqual(0.0, metrics.AvgLatencyMs)

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
