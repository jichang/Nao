namespace Nao.Agents.Tests

open System
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass>]
type VerificationTests() =

    let agentId = "test-agent"
    let correlation = CorrelationContext.root ()

    [<TestMethod>]
    member _.StartTraceCreatesNewTrace() =
        let trace = Verification.startTrace correlation agentId "user input"
        Assert.AreEqual(correlation, trace.Correlation)
        Assert.AreEqual(agentId, trace.AgentId)
        Assert.AreEqual("user input", trace.Input)
        Assert.IsTrue(trace.Output.IsNone)
        Assert.AreEqual(0, trace.Steps.Length)
        Assert.IsFalse(trace.Success)

    [<TestMethod>]
    member _.AddStepAppendsToTrace() =
        let trace = Verification.startTrace correlation agentId "input"

        let updated =
            trace
            |> Verification.addStep (TraceAction.LlmCall "gpt-4") "input" "output" 100L

        Assert.AreEqual(1, updated.Steps.Length)
        Assert.AreEqual(1, updated.Steps.[0].StepNumber)
        Assert.AreEqual("input", updated.Steps.[0].Input)
        Assert.AreEqual("output", updated.Steps.[0].Output)
        Assert.AreEqual(100L, updated.Steps.[0].DurationMs)

    [<TestMethod>]
    member _.CompleteMarksSuccess() =
        let trace = Verification.startTrace correlation agentId "input"
        let completed = trace |> Verification.complete "final answer"
        Assert.IsTrue(completed.Success)
        Assert.AreEqual(Some "final answer", completed.Output)
        Assert.IsTrue(completed.CompletedAt.IsSome)

    [<TestMethod>]
    member _.FailMarksFailure() =
        let trace = Verification.startTrace correlation agentId "input"
        let failed = trace |> Verification.fail "something broke"
        Assert.IsFalse(failed.Success)
        Assert.AreEqual(Some "something broke", failed.Output)
        Assert.IsTrue(failed.CompletedAt.IsSome)

    [<TestMethod>]
    member _.LlmJudgeUsesTraceCorrelation() =
        let observed = ref None

        let provider =
            LlmProvider.create (fun () -> "capturing") (fun actual _conversation _options ->
                observed.Value <- Some actual
                Task.FromResult(CompletionResult.create "PASS: verified" "stop" None None))

        let trace =
            Verification.startTrace correlation agentId "input"
            |> Verification.complete "output"

        let judge = LlmJudge.create provider CompletionOptions.Default [ "correctness" ]
        let result = Judge.judgeAsync trace judge |> _.Result

        Assert.AreEqual(JudgementVerdict.Pass, result.Verdict)
        Assert.AreEqual(Some correlation, observed.Value)

    [<TestMethod>]
    member _.GroundTaskUsesSuppliedCorrelation() =
        let observed = ref None

        let provider =
            LlmProvider.create (fun () -> "capturing") (fun actual _conversation _options ->
                observed.Value <- Some actual
                Task.FromResult(CompletionResult.create "understood" "stop" None None))

        let result =
            Verification.groundTaskAsync correlation provider CompletionOptions.Default "do the work"
            |> _.Result

        Assert.AreEqual(Some "understood", result.Understanding)
        Assert.AreEqual(Some correlation, observed.Value)

    [<TestMethod>]
    member _.CheckReadinessAllPass() =
        let check1 =
            ReadinessCheck.create "check1" (fun _ _ -> Task.FromResult ReadinessResult.Ready)

        let check2 =
            ReadinessCheck.create "check2" (fun _ _ -> Task.FromResult ReadinessResult.Ready)

        let result = (Verification.checkReadiness [ check1; check2 ] agentId "input").Result
        Assert.AreEqual(ReadinessResult.Ready, result)

    [<TestMethod>]
    member _.CheckReadinessCollectsFailures() =
        let passCheck =
            ReadinessCheck.create "pass" (fun _ _ -> Task.FromResult ReadinessResult.Ready)

        let failCheck =
            ReadinessCheck.create "fail" (fun _ _ -> Task.FromResult(ReadinessResult.NotReady [ "missing tool" ]))

        let result =
            (Verification.checkReadiness [ passCheck; failCheck ] agentId "input").Result

        match result with
        | ReadinessResult.NotReady reasons ->
            Assert.AreEqual(1, reasons.Length)
            Assert.AreEqual("missing tool", reasons.[0])
        | ReadinessResult.Ready -> Assert.Fail("Expected NotReady")

    [<TestMethod>]
    member _.CheckReadinessRunsInParallelAndPreservesReasonOrder() =
        let firstStarted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let secondStarted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let release =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let check name (started: TaskCompletionSource<unit>) reason =
            ReadinessCheck.create name (fun _ _ ->
                task {
                    started.SetResult()
                    do! release.Task
                    return ReadinessResult.NotReady [ reason ]
                })

        let pending =
            Verification.checkReadiness
                [ check "first" firstStarted "first reason"
                  check "second" secondStarted "second reason" ]
                agentId
                "input"

        Assert.IsTrue(firstStarted.Task.Wait(TimeSpan.FromSeconds 1.0))
        Assert.IsTrue(secondStarted.Task.Wait(TimeSpan.FromSeconds 1.0))
        release.SetResult()

        Assert.AreEqual(ReadinessResult.NotReady [ "first reason"; "second reason" ], pending.Result)

    [<TestMethod>]
    member _.CheckReadinessPropagatesCheckErrors() : Task =
        task {
            let expected = InvalidOperationException("readiness failed")

            let check =
                ReadinessCheck.create "throws" (fun _ _ -> Task.FromException<ReadinessResult>(expected))

            try
                let! _ = Verification.checkReadiness [ check ] agentId "input"
                Assert.Fail("Expected readiness failure")
            with :? InvalidOperationException as actual ->
                Assert.AreSame(expected, actual)
        }
        :> Task

[<TestClass>]
type RegressionTests() =

    let agentId = "test"

    let makeTrace success steps duration : ExecutionTrace =
        let startedAt = DateTimeOffset.UtcNow.AddMilliseconds(-duration)

        { Id = Guid.NewGuid()
          Correlation = CorrelationContext.root ()
          AgentId = agentId
          Input = "test"
          Output = Some "result"
          Steps =
            [ for i in 1..steps ->
                  { StepNumber = i
                    Action = TraceAction.LlmCall "test"
                    Input = ""
                    Output = ""
                    DurationMs = 10L
                    Timestamp = DateTimeOffset.UtcNow } ]
          StartedAt = startedAt
          CompletedAt = Some DateTimeOffset.UtcNow
          Success = success
          Metadata = Map.empty }

    [<TestMethod>]
    member _.NoRegressionWhenSimilar() =
        let baseline = makeTrace true 3 1000.0
        let current = makeTrace true 3 1200.0
        let result = Regression.detect baseline current
        Assert.IsFalse(result.IsRegression)
        Assert.AreEqual(0, result.Regressions.Length)

    [<TestMethod>]
    member _.DetectsStepCountRegression() =
        let baseline = makeTrace true 2 1000.0
        let current = makeTrace true 10 1000.0 // 10 > 2*2
        let result = Regression.detect baseline current
        Assert.IsTrue(result.IsRegression)

        Assert.IsTrue(
            result.Regressions
            |> List.exists (fun r -> r.Category = RegressionCategory.Latency)
        )

    [<TestMethod>]
    member _.DetectsSuccessRegression() =
        let baseline = makeTrace true 3 1000.0
        let current = makeTrace false 3 1000.0
        let result = Regression.detect baseline current
        Assert.IsTrue(result.IsRegression)

        Assert.IsTrue(
            result.Regressions
            |> List.exists (fun r -> r.Category = RegressionCategory.SuccessRate)
        )

        Assert.AreEqual(
            1.0,
            (result.Regressions
             |> List.find (fun r -> r.Category = RegressionCategory.SuccessRate))
                .Severity
        )

    [<TestMethod>]
    member _.InMemoryTraceStoreSavesAndRetrieves() =
        let store = InMemoryTraceStore.create ()
        let trace = makeTrace true 2 500.0
        store.SaveAsync(trace).Wait()
        let retrieved = (store.GetTracesAsync agentId 10).Result
        Assert.AreEqual(1, retrieved.Length)
        Assert.AreEqual(trace.Id, retrieved.[0].Id)

    [<TestMethod>]
    member _.GetBaselineReturnsLatestSuccessful() =
        let store = InMemoryTraceStore.create ()

        let old =
            { makeTrace true 2 2000.0 with
                StartedAt = DateTimeOffset.UtcNow.AddHours(-2.0) }

        let recent =
            { makeTrace true 3 1000.0 with
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5.0) }

        let failed =
            { makeTrace false 1 500.0 with
                StartedAt = DateTimeOffset.UtcNow }

        store.SaveAsync(old).Wait()
        store.SaveAsync(recent).Wait()
        store.SaveAsync(failed).Wait()
        let baseline = (store.GetBaselineAsync agentId "test").Result
        Assert.IsTrue(baseline.IsSome)
        Assert.AreEqual(recent.Id, baseline.Value.Id)
