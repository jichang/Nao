namespace Nao.Agents.Tests

open System
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Persistence
open Nao.Agents

[<TestClass>]
type EtclovgHarnessTests() =

    let makeAgent (response: string) =
        Agent.create
            "test-agent"
            "test-agent"
            "test"
            0
            []
            AgentContract.Text
            (fun _context _input -> Task.FromResult response)
            (fun _context _message -> Task.FromResult None)

    [<TestMethod>]
    member _.HarnessErrorsExposePlatformCategoryAndRetryability() =
        let cases =
            [ (HarnessError.PermissionDenied, PlatformErrorCategory.PermissionDenied, false)
              (HarnessError.PolicyBlocked [], PlatformErrorCategory.PermissionDenied, false)
              (HarnessError.NotReady [], PlatformErrorCategory.NotReady, true)
              (HarnessError.InitializationFailed "failed", PlatformErrorCategory.NotReady, true)
              (HarnessError.ResourceLimitExceeded LimitExceeded.Duration, PlatformErrorCategory.ResourceExhausted, false)
              (HarnessError.ConstitutionViolation [], PlatformErrorCategory.InvalidOutput, false)
              (HarnessError.ExecutionFailed "failed", PlatformErrorCategory.InternalFailure, false) ]

        for error, expectedCategory, expectedRetryable in cases do
            Assert.AreEqual(expectedCategory, error.Category)
            Assert.AreEqual(expectedRetryable, error.Retryable)

            let failure = error.ToPlatformFailure(Some "execution-1")
            Assert.AreEqual(expectedCategory, failure.Category)
            Assert.AreEqual(expectedRetryable, failure.Retryable)
            Assert.AreEqual(error.Message, failure.Message)
            Assert.AreEqual(Some "execution-1", failure.CorrelationId)

    [<TestMethod>]
    member _.SuccessfulExecutionReturnsResponse() =
        let agent = makeAgent "hello world"
        let config = EtclovgConfig.Default

        let result =
            (EtclovgHarness.runAsync config AgentContext.allowAll agent "test").Result

        Assert.IsTrue(result.Success)
        Assert.AreEqual(Some "hello world", result.Response)
        Assert.IsTrue(result.HarnessError.IsNone)
        Assert.IsTrue(result.Trace.IsSome)

    [<TestMethod>]
    member _.PolicyViolationBlocksExecution() =
        let agent = makeAgent "response"
        let policy = PolicyEngine.costBudgetPolicy 0.0m // zero budget
        let engine = PolicyEngine.create [ policy ]

        let usage =
            { ResourceUsage.Zero with
                EstimatedCostUsd = 1.0m }
        // Need to set initial usage on the execution context
        let config =
            { EtclovgConfig.Default with
                PolicyEngine = Some engine }
        // With zero budget and usage > 0, it should block
        // Actually with zero cost model the initial usage is 0, so it won't block
        // Let's use a different approach - use a policy that always blocks
        let alwaysBlock =
            { Id = "always-block"
              Description = "Blocks everything"
              Enforcement = PolicyEnforcement.Block
              Evaluate = fun _ -> Some "no execution allowed" }

        let blockEngine = PolicyEngine.create [ alwaysBlock ]

        let blockConfig =
            { EtclovgConfig.Default with
                PolicyEngine = Some blockEngine }

        let result =
            (EtclovgHarness.runAsync blockConfig AgentContext.allowAll agent "test").Result

        Assert.IsFalse(result.Success)
        Assert.IsTrue(result.HarnessError.Value.Message.Contains("Blocked by policy"))
        Assert.AreEqual(1, result.PolicyViolations.Length)

    [<TestMethod>]
    member _.ReadinessCheckFailureBlocksExecution() =
        let agent = makeAgent "response"

        let failCheck =
            ReadinessCheck.create "prereq" (fun _ _ ->
                Task.FromResult(ReadinessResult.NotReady [ "missing dependency" ]))

        let config =
            { EtclovgConfig.Default with
                ReadinessChecks = [ failCheck ] }

        let result =
            (EtclovgHarness.runAsync config AgentContext.allowAll agent "test").Result

        Assert.IsFalse(result.Success)
        Assert.IsTrue(result.HarnessError.Value.Message.Contains("Not ready"))

    [<TestMethod>]
    member _.LifecycleHookCanBlockInit() =
        let agent = makeAgent "response"

        let blockHook =
            { LifecycleHook.passthrough with
                OnBeforeInit = fun _ -> Task.FromResult(Error "init blocked") }

        let config =
            { EtclovgConfig.Default with
                Lifecycle = [ blockHook ] }

        let result =
            (EtclovgHarness.runAsync config AgentContext.allowAll agent "test").Result

        Assert.IsFalse(result.Success)
        Assert.AreEqual(Some(HarnessError.InitializationFailed "init blocked"), result.HarnessError)

    [<TestMethod>]
    member _.ConstitutionViolationBlocksOutput() =
        let agent = makeAgent "contact user@evil.com for info"

        let constitution =
            Constitution.empty "safety"
            |> Constitution.addRule Constitution.noPrivateDataRule

        let config =
            { EtclovgConfig.Default with
                Constitution = Some constitution }

        let result =
            (EtclovgHarness.runAsync config AgentContext.allowAll agent "test").Result

        Assert.IsFalse(result.Success)
        Assert.IsTrue(result.HarnessError.Value.Message.Contains("Output violates constitution"))
        Assert.IsTrue(result.ConstitutionViolations.Length > 0)

    [<TestMethod>]
    member _.DeterministicAgentDoesNotRecordLlmCall() =
        let agent = makeAgent "done"
        let metrics = InMemory.metrics ()

        let config =
            { EtclovgConfig.Default with
                Metrics = Some metrics }

        let context =
            { AgentContext.allowAll with
                SessionKey = "metrics/session" }

        let result = (EtclovgHarness.runAsync config context agent "test").Result

        Assert.IsTrue(result.Success)
        Assert.IsTrue(result.Metrics.IsSome)
        Assert.AreEqual(0, result.Metrics.Value.TotalLlmCalls)

    [<TestMethod>]
    member _.TraceStoredAfterExecution() =
        let agent = makeAgent "answer"
        let store = InMemoryTraceStore.create ()

        let config =
            { EtclovgConfig.Default with
                TraceStore = Some store }

        let result =
            (EtclovgHarness.runAsync config AgentContext.allowAll agent "question").Result

        Assert.IsTrue(result.Success)
        let agentId = "test-agent"
        let traces = (store.GetTracesAsync agentId 10).Result
        Assert.AreEqual(1, traces.Length)
        Assert.IsTrue(traces.[0].Success)

    [<TestMethod>]
    member _.AuditLogRecordsEntry() =
        let agent = makeAgent "ok"
        let audit = InMemory.auditLog ()

        let config =
            { EtclovgConfig.Default with
                AuditLog = Some audit }

        let result =
            (EtclovgHarness.runAsync config AgentContext.allowAll agent "test").Result

        Assert.IsTrue(result.Success)
        Assert.AreEqual(1, result.AuditEntries)
        let agentId = "test-agent"

        let entries =
            (audit.QueryAsync agentId (DateTimeOffset.UtcNow.AddMinutes(-1.0))).Result

        Assert.IsTrue(entries.Length > 0)

    [<TestMethod>]
    member _.AllLayersWorkTogether() =
        let agent = makeAgent "safe response"
        let metrics = InMemory.metrics ()
        let tracer = InMemory.tracer ()
        let store = InMemoryTraceStore.create ()
        let audit = InMemory.auditLog ()

        let constitution =
            Constitution.empty "basic" |> Constitution.addRule Constitution.noHarmRule

        let passCheck =
            ReadinessCheck.create "ready" (fun _ _ -> Task.FromResult ReadinessResult.Ready)

        let config =
            { EtclovgConfig.Default with
                Metrics = Some metrics
                Tracer = Some tracer
                TraceStore = Some store
                AuditLog = Some audit
                Constitution = Some constitution
                ReadinessChecks = [ passCheck ]
                Lifecycle = [ LifecycleHook.passthrough ] }

        let context =
            { AgentContext.allowAll with
                SessionKey = "metrics/session" }

        let result = (EtclovgHarness.runAsync config context agent "hello").Result

        Assert.IsTrue(result.Success)
        Assert.AreEqual(Some "safe response", result.Response)
        Assert.IsTrue(result.Metrics.IsSome)
        Assert.IsTrue(result.Trace.IsSome)
        Assert.AreEqual(1, result.AuditEntries)
