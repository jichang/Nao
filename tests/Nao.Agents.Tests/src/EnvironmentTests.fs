namespace Nao.Agents.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type ResourceLimitsTests() =

    [<TestMethod>]
    member _.UnlimitedHasMaxValues() =
        let limits = ResourceLimits.Unlimited
        Assert.AreEqual(Int32.MaxValue, limits.MaxLlmCalls)
        Assert.AreEqual(Int32.MaxValue, limits.MaxTotalTokens)
        Assert.AreEqual(Int32.MaxValue, limits.MaxToolCalls)

    [<TestMethod>]
    member _.ConstrainedSetsSpecificValues() =
        let limits = ResourceLimits.Constrained 60 10 5000
        Assert.AreEqual(TimeSpan.FromSeconds(60.0), limits.MaxDuration)
        Assert.AreEqual(10, limits.MaxLlmCalls)
        Assert.AreEqual(5000, limits.MaxTotalTokens)

    [<TestMethod>]
    member _.UsageZeroDoesNotExceedLimits() =
        let limits = ResourceLimits.Constrained 60 10 5000
        let usage = ResourceUsage.Zero
        Assert.IsFalse(usage.Exceeds(limits))

    [<TestMethod>]
    member _.UsageExceedsWhenOverLimit() =
        let limits = ResourceLimits.Constrained 60 2 5000
        let usage = { ResourceUsage.Zero with LlmCalls = 3 }
        Assert.IsTrue(usage.Exceeds(limits))

    [<TestMethod>]
    member _.CheckReturnsSpecificLimitExceeded() =
        let limits = ResourceLimits.Constrained 60 10 100

        let usage =
            { ResourceUsage.Zero with
                TotalTokens = 101 }

        let result = ResourceUsage.check limits usage
        Assert.AreEqual(Some LimitExceeded.TotalTokens, result)

    [<TestMethod>]
    member _.CheckReturnsNoneWhenWithinLimits() =
        let limits = ResourceLimits.Constrained 60 10 5000

        let usage =
            { ResourceUsage.Zero with
                LlmCalls = 5
                TotalTokens = 2000 }

        let result = ResourceUsage.check limits usage
        Assert.AreEqual(None, result)

[<TestClass>]
type SandboxTests() =

    [<TestMethod>]
    member _.DefaultSandboxAllowsNetwork() =
        let config = SandboxConfig.Default
        Assert.IsTrue(config.AllowNetwork)
        Assert.IsFalse(config.AllowFileSystem)

    [<TestMethod>]
    member _.RestrictedSandboxDeniesAll() =
        let limits = ResourceLimits.Constrained 10 5 1000
        let config = SandboxConfig.Restricted limits
        Assert.IsFalse(config.AllowNetwork)
        Assert.IsFalse(config.AllowFileSystem)
        Assert.AreEqual(limits, config.Limits)

    [<TestMethod>]
    member _.ExecutionContextRecordsLlmCall() =
        let ctx = ExecutionContext.Create SandboxConfig.Default
        Assert.AreEqual(0, ctx.Usage.LlmCalls)
        ctx.RecordLlmCall(100, 0.001m)
        Assert.AreEqual(1, ctx.Usage.LlmCalls)
        Assert.AreEqual(100, ctx.Usage.TotalTokens)
        Assert.AreEqual(0.001m, ctx.Usage.EstimatedCostUsd)

    [<TestMethod>]
    member _.ExecutionContextRecordsToolCall() =
        let ctx = ExecutionContext.Create SandboxConfig.Default
        ctx.RecordToolCall()
        ctx.RecordToolCall()
        Assert.AreEqual(2, ctx.Usage.ToolCalls)

    [<TestMethod>]
    member _.CreateChildLinksToParent() =
        let parent = ExecutionContext.Create SandboxConfig.Default
        let child = parent.CreateChild()
        Assert.AreEqual(Some parent, child.ParentContext)
        Assert.AreNotEqual(parent.ExecutionId, child.ExecutionId)

    [<TestMethod>]
    member _.CheckLimitsDetectsExceeded() =
        let limits = ResourceLimits.Constrained 60 1 5000

        let config =
            { SandboxConfig.Default with
                Limits = limits }

        let ctx = ExecutionContext.Create config
        ctx.RecordLlmCall(100, 0.01m)
        ctx.RecordLlmCall(100, 0.01m) // second call exceeds limit of 1
        let result = ctx.CheckLimits()
        Assert.AreEqual(Some LimitExceeded.LlmCalls, result)

[<TestClass>]
type ExecutionEnvironmentTests() =

    let makeAgent (response: string) =
        Agent.create
            "test"
            "test"
            "test agent"
            0
            []
            AgentContract.Text
            (fun _context _input -> Task.FromResult response)
            (fun _context _message -> Task.FromResult None)

    [<TestMethod>]
    member _.LocalEnvironmentExecutesAgent() =
        let agent = makeAgent "hello"
        let ctx = ExecutionContext.Create SandboxConfig.Default
        let env = ExecutionEnvironment.local ()
        let result = (env.ExecuteAsync ctx AgentContext.allowAll agent "input").Result

        match result with
        | Ok response -> Assert.AreEqual("hello", response)
        | Error _ -> Assert.Fail("Expected Ok")

    [<TestMethod>]
    member _.ExecuteWithTimeoutReturnsOnTime() =
        let agent = makeAgent "fast"

        let config =
            { SandboxConfig.Default with
                Limits =
                    { ResourceLimits.Unlimited with
                        MaxDuration = TimeSpan.FromSeconds 5.0 } }

        let ctx = ExecutionContext.Create config
        let env = ExecutionEnvironment.local ()

        let result =
            (ExecutionEnvironment.executeWithTimeout env ctx AgentContext.allowAll agent "go").Result

        match result with
        | Ok r -> Assert.AreEqual("fast", r)
        | Error _ -> Assert.Fail("Expected Ok")
