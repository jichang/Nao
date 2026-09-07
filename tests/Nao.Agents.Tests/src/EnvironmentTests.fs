namespace Nao.Agents.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Agents.Worker

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
    member _.CreateChildSharesParentResourceUsage() =
        let parent = ExecutionContext.Create SandboxConfig.Default
        let child = parent.CreateChild()

        child.RecordLlmCall(100, 0.01m)
        parent.RecordToolCall()

        Assert.AreEqual(parent.Usage, child.Usage)
        Assert.AreEqual(1, parent.Usage.LlmCalls)
        Assert.AreEqual(1, child.Usage.ToolCalls)

    [<TestMethod>]
    member _.OperationKeysAreRetryStableAndKindSeparated() =
        let turnKey = IdempotencyKey.forExecution (ExecutionId.generate ())

        let create correlation =
            ExecutionContext.CreateForRequest SandboxConfig.Default correlation turnKey Threading.CancellationToken.None

        let first = create (CorrelationContext.root ())
        let retry = create (CorrelationContext.retry first.Correlation)

        Assert.AreEqual(first.NextProviderCallKey(), retry.NextProviderCallKey())
        Assert.AreNotEqual(first.NextToolCallKey(), first.NextArtifactKey())

    [<TestMethod>]
    member _.NestedWorkSharesOperationSequence() =
        let parent = ExecutionContext.Create SandboxConfig.Default
        let child = parent.CreateChild()

        let first = parent.NextToolCallKey()
        let second = child.NextToolCallKey()

        Assert.AreNotEqual(first, second)

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

    [<TestMethod>]
    member _.NestedCallsReserveSharedLimitsAtomically() =
        let limits =
            { ResourceLimits.Unlimited with
                MaxLlmCalls = 1
                MaxToolCalls = 1 }

        let parent =
            ExecutionContext.Create
                { SandboxConfig.Default with
                    Limits = limits }

        let child = parent.CreateChild()

        Assert.AreEqual(None, parent.BeginLlmCall())
        Assert.AreEqual(Some LimitExceeded.LlmCalls, child.BeginLlmCall())
        Assert.AreEqual(None, child.BeginToolCall())
        Assert.AreEqual(Some LimitExceeded.ToolCalls, parent.BeginToolCall())

[<TestClass>]
type ExecutionEnvironmentTests() =

    let makeAgent (response: string) =
        Agent.create "test" "test" "test agent" 0 [] AgentContract.Text (fun _context _input ->
            Task.FromResult response)

    let workerExecutable =
        let assemblyDirectory =
            typeof<WorkerAssemblyMarker>.Assembly.Location
            |> IO.Path.GetDirectoryName
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Worker assembly directory is unavailable.")

        IO.Path.Combine(assemblyDirectory, "Nao.Agents.Worker")

    let workerAgent =
        Agent.createWorker
            "worker-test"
            "worker-test"
            "process worker test agent"
            0
            []
            AgentContract.Text
            (WorkerAgentReference.create "process-diagnostic")

    let processEnvironment (protocolVersion: int) (arguments: string list) =
        ExecutionEnvironment.processWorker
            { FileName = workerExecutable
              Arguments = arguments
              ProtocolVersion = protocolVersion }

    [<TestMethod>]
    member _.LocalEnvironmentExecutesAgent() =
        let agent = makeAgent "hello"
        let ctx = ExecutionContext.Create SandboxConfig.Default
        let env = ExecutionEnvironment.local ()

        let result =
            (env.ExecuteAsync ctx (AgentContext.unrestrictedForTests ()) agent "input").Result

        match result with
        | Ok response -> Assert.AreEqual("hello", response)
        | Error _ -> Assert.Fail("Expected Ok")

    [<TestMethod>]
    member _.ProcessEnvironmentExecutesWorkerAgentInChildProcess() =
        let correlation = CorrelationContext.root ()
        let ctx = ExecutionContext.CreateWithCorrelation SandboxConfig.Default correlation
        let environment = processEnvironment WorkerProtocol.CurrentVersion []

        let result =
            environment.ExecuteAsync ctx (AgentContext.unrestrictedForTests ()) workerAgent "payload"
            |> _.Result

        match result with
        | Error limit -> Assert.Fail(sprintf "Expected worker result, got limit %A." limit)
        | Ok output ->
            let fields = output.Split('|')
            Assert.AreEqual(5, fields.Length)
            Assert.AreNotEqual(Environment.ProcessId, Int32.Parse fields.[0])
            Assert.AreEqual("payload", fields.[1])
            Assert.AreEqual(ExecutionId.serialize correlation.ExecutionId, fields.[2])
            Assert.AreEqual(string (CorrelationId.value correlation.CorrelationId), fields.[3])
            Assert.AreEqual(string correlation.Attempt, fields.[4])

    [<TestMethod>]
    member _.ProcessEnvironmentRejectsProtocolMismatchBeforeExecution() =
        let environment =
            processEnvironment
                WorkerProtocol.CurrentVersion
                [ "--protocol-version"; string (WorkerProtocol.CurrentVersion + 1) ]

        let ctx = ExecutionContext.Create SandboxConfig.Default

        let error =
            Assert.ThrowsExactlyAsync<GovernedExecutionException>(fun () ->
                environment.ExecuteAsync ctx (AgentContext.unrestrictedForTests ()) workerAgent "payload" :> Task)
            |> _.Result

        StringAssert.Contains(error.Message, "protocol mismatch")

    [<TestMethod>]
    member _.ProcessEnvironmentRejectsLocalOnlyAgentBeforeSpawn() =
        let environment = processEnvironment WorkerProtocol.CurrentVersion []
        let ctx = ExecutionContext.Create SandboxConfig.Default

        let error =
            Assert.ThrowsExactlyAsync<GovernedExecutionException>(fun () ->
                environment.ExecuteAsync ctx (AgentContext.unrestrictedForTests ()) (makeAgent "local") "payload"
                :> Task)
            |> _.Result

        StringAssert.Contains(error.Message, "no process-worker reference")

    [<TestMethod>]
    [<DoNotParallelize>]
    member _.ProcessEnvironmentExposesOnlyDeclaredEnvironmentVariables() =
        let variableName = "NAO_PROCESS_TEST_" + Guid.NewGuid().ToString("N")
        let inheritedValue = "host-value"
        let declaredValue = "declared-value"
        let previousValue = Environment.GetEnvironmentVariable variableName

        try
            Environment.SetEnvironmentVariable(variableName, inheritedValue)

            let agent =
                Agent.createWorker
                    "worker-environment"
                    "worker-environment"
                    "process environment test agent"
                    0
                    []
                    AgentContract.Text
                    (WorkerAgentReference.create "process-environment")

            let environment = processEnvironment WorkerProtocol.CurrentVersion []

            let run sandbox =
                let execution = ExecutionContext.Create sandbox

                environment.ExecuteAsync execution (AgentContext.unrestrictedForTests ()) agent variableName
                |> _.Result

            match run SandboxConfig.Default with
            | Error limit -> Assert.Fail(sprintf "Expected worker result, got limit %A." limit)
            | Ok value -> Assert.AreEqual("<missing>", value)

            let declaredSandbox =
                { SandboxConfig.Default with
                    EnvironmentVariables = Map.ofList [ variableName, declaredValue ] }

            match run declaredSandbox with
            | Error limit -> Assert.Fail(sprintf "Expected worker result, got limit %A." limit)
            | Ok value -> Assert.AreEqual(declaredValue, value)
        finally
            Environment.SetEnvironmentVariable(variableName, previousValue)

    [<TestMethod>]
    member _.ProcessEnvironmentUsesDeclaredWorkingDirectory() =
        let workingDirectory =
            IO.Path.Combine(IO.Path.GetTempPath(), "nao-worker-" + Guid.NewGuid().ToString("N"))

        IO.Directory.CreateDirectory workingDirectory |> ignore

        try
            let agent =
                Agent.createWorker
                    "worker-directory"
                    "worker-directory"
                    "process working directory test agent"
                    0
                    []
                    AgentContract.Text
                    (WorkerAgentReference.create "process-working-directory")

            let sandbox =
                { SandboxConfig.Default with
                    WorkingDirectory = Some workingDirectory }

            let execution = ExecutionContext.Create sandbox
            let environment = processEnvironment WorkerProtocol.CurrentVersion []

            match
                environment.ExecuteAsync execution (AgentContext.unrestrictedForTests ()) agent ""
                |> _.Result
            with
            | Error limit -> Assert.Fail(sprintf "Expected worker result, got limit %A." limit)
            | Ok directory -> Assert.AreEqual(IO.Path.GetFullPath workingDirectory, directory)
        finally
            IO.Directory.Delete(workingDirectory, true)

    [<TestMethod>]
    member _.ProcessEnvironmentRejectsMissingWorkingDirectoryBeforeSpawn() =
        let missingDirectory =
            IO.Path.Combine(IO.Path.GetTempPath(), "nao-missing-" + Guid.NewGuid().ToString("N"))

        let sandbox =
            { SandboxConfig.Default with
                WorkingDirectory = Some missingDirectory }

        let execution = ExecutionContext.Create sandbox
        let environment = processEnvironment WorkerProtocol.CurrentVersion []

        let error =
            Assert.ThrowsExactlyAsync<GovernedExecutionException>(fun () ->
                environment.ExecuteAsync execution (AgentContext.unrestrictedForTests ()) workerAgent "payload" :> Task)
            |> _.Result

        StringAssert.Contains(error.Message, "working directory does not exist")

    [<TestMethod>]
    member _.ProcessEnvironmentRejectsRelativeWorkingDirectoryBeforeSpawn() =
        let sandbox =
            { SandboxConfig.Default with
                WorkingDirectory = Some("relative-" + Guid.NewGuid().ToString("N")) }

        let execution = ExecutionContext.Create sandbox
        let environment = processEnvironment WorkerProtocol.CurrentVersion []

        let error =
            Assert.ThrowsExactlyAsync<GovernedExecutionException>(fun () ->
                environment.ExecuteAsync execution (AgentContext.unrestrictedForTests ()) workerAgent "payload" :> Task)
            |> _.Result

        StringAssert.Contains(error.Message, "working directory must be fully qualified")

    [<TestMethod>]
    [<DoNotParallelize>]
    member _.ProcessEnvironmentKillsChildProcessTreeOnCancellation() =
        let childProcessIdPath = IO.Path.GetTempFileName()
        IO.File.Delete childProcessIdPath

        try
            let agent =
                Agent.createWorker
                    "worker-tree"
                    "worker-tree"
                    "process tree test agent"
                    0
                    []
                    AgentContract.Text
                    (WorkerAgentReference.create "process-tree")

            use cancellation = new CancellationTokenSource()

            let execution =
                ExecutionContext.CreateWithCancellation SandboxConfig.Default cancellation.Token

            let environment = processEnvironment WorkerProtocol.CurrentVersion []

            let running =
                environment.ExecuteAsync execution (AgentContext.unrestrictedForTests ()) agent childProcessIdPath

            let rendezvousDeadline = DateTime.UtcNow + TimeSpan.FromSeconds 5.0

            while not (IO.File.Exists childProcessIdPath) && DateTime.UtcNow < rendezvousDeadline do
                Task.Delay(TimeSpan.FromMilliseconds 10.0).Wait()

            Assert.IsTrue(IO.File.Exists childProcessIdPath, "Worker did not publish its child process ID.")

            let childProcessId = IO.File.ReadAllText childProcessIdPath |> Int32.Parse
            use child = Diagnostics.Process.GetProcessById childProcessId
            cancellation.Cancel()

            Assert.ThrowsExactlyAsync<OperationCanceledException>(fun () -> running :> Task)
            |> _.Wait()

            Assert.IsTrue(child.WaitForExit(5000), "Worker child process survived cancellation.")
        finally
            IO.File.Delete childProcessIdPath
            IO.File.Delete(childProcessIdPath + ".pending")

    [<TestMethod>]
    member _.LocalEnvironmentEnforcesCancellationToken() =
        let agent =
            Agent.create "slow" "slow" "slow" 0 [] AgentContract.Text (fun _ _ ->
                task {
                    do! Task.Delay(TimeSpan.FromSeconds 5.0)
                    return "late"
                })

        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        let execution =
            ExecutionContext.CreateWithCancellation SandboxConfig.Default cancellation.Token

        let environment = ExecutionEnvironment.local ()

        Assert.ThrowsExactlyAsync<OperationCanceledException>(fun () ->
            environment.ExecuteAsync execution (AgentContext.unrestrictedForTests ()) agent "input" :> Task)
        |> _.Wait()

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
            (ExecutionEnvironment.executeWithTimeout env ctx (AgentContext.unrestrictedForTests ()) agent "go").Result

        match result with
        | Ok r -> Assert.AreEqual("fast", r)
        | Error _ -> Assert.Fail("Expected Ok")

    [<TestMethod>]
    member _.ExecuteWithTimeoutPreservesCallerCancellation() =
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        let ctx =
            ExecutionContext.CreateWithCancellation SandboxConfig.Default cancellation.Token

        let environment = ExecutionEnvironment.local ()

        Assert.ThrowsExactlyAsync<OperationCanceledException>(fun () ->
            ExecutionEnvironment.executeWithTimeout
                environment
                ctx
                (AgentContext.unrestrictedForTests ())
                (makeAgent "unused")
                "input"
            :> Task)
        |> _.Wait()
