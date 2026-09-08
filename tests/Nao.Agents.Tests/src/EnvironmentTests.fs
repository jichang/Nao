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
              ProtocolVersion = protocolVersion
              IdentityIsolation = ProcessIdentityIsolation.Disabled
              FileSystemIsolation = ProcessFileSystemIsolation.Disabled
              SecretResolver = None
              OutputCapture = None }

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
    member _.LinuxUserNamespaceMapsWorkerIdentity() =
        if OperatingSystem.IsLinux() then
            let unshareExecutable =
                [ "/usr/bin/unshare"; "/bin/unshare" ] |> List.tryFind IO.File.Exists

            match unshareExecutable with
            | None -> ()
            | Some unshareExecutable ->
                let userId = uint32 (Random.Shared.Next(60000, 65000))
                let groupId = uint32 (Random.Shared.Next(60000, 65000))

                let agent =
                    Agent.createWorker
                        "worker-identity"
                        "worker-identity"
                        "process identity test agent"
                        0
                        []
                        AgentContract.Text
                        (WorkerAgentReference.create "process-identity")

                let environment =
                    ExecutionEnvironment.processWorker
                        { FileName = workerExecutable
                          Arguments = []
                          ProtocolVersion = WorkerProtocol.CurrentVersion
                          IdentityIsolation =
                            ProcessIdentityIsolation.LinuxUserNamespace(unshareExecutable, userId, groupId)
                          FileSystemIsolation = ProcessFileSystemIsolation.Disabled
                          SecretResolver = None
                          OutputCapture = None }

                let result =
                    environment.ExecuteAsync
                        (ExecutionContext.Create SandboxConfig.Default)
                        (AgentContext.unrestrictedForTests ())
                        agent
                        ""
                    |> _.Result

                Assert.AreEqual(Ok(sprintf "%d|%d" userId groupId), result)

    [<TestMethod>]
    member _.LinuxUserNamespaceRejectsMissingLauncherBeforeSpawn() =
        if OperatingSystem.IsLinux() then
            let missingLauncher =
                IO.Path.Combine(IO.Path.GetTempPath(), "nao-missing-unshare-" + Guid.NewGuid().ToString("N"))

            let environment =
                ExecutionEnvironment.processWorker
                    { FileName = workerExecutable
                      Arguments = []
                      ProtocolVersion = WorkerProtocol.CurrentVersion
                      IdentityIsolation = ProcessIdentityIsolation.LinuxUserNamespace(missingLauncher, 65534u, 65534u)
                      FileSystemIsolation = ProcessFileSystemIsolation.Disabled
                      SecretResolver = None
                      OutputCapture = None }

            let error =
                Assert.ThrowsExactlyAsync<GovernedExecutionException>(fun () ->
                    environment.ExecuteAsync
                        (ExecutionContext.Create SandboxConfig.Default)
                        (AgentContext.unrestrictedForTests ())
                        workerAgent
                        "payload"
                    :> Task)
                |> _.Result

            StringAssert.Contains(error.Message, "existing fully qualified unshare")

    [<TestMethod>]
    member _.ProcessEnvironmentResolvesDeclaredSecretInsideWorker() =
        let handle = "secret-" + Guid.NewGuid().ToString("N")
        let secret = Guid.NewGuid().ToString("N")
        let resolvedHandles = ResizeArray<string>()

        let agent =
            Agent.createWorker
                "worker-secret"
                "worker-secret"
                "process secret test agent"
                0
                []
                AgentContract.Text
                (WorkerAgentReference.create "process-secret-hash")

        let environment =
            ExecutionEnvironment.processWorker
                { FileName = workerExecutable
                  Arguments = []
                  ProtocolVersion = WorkerProtocol.CurrentVersion
                  IdentityIsolation = ProcessIdentityIsolation.Disabled
                  FileSystemIsolation = ProcessFileSystemIsolation.Disabled
                  SecretResolver =
                    Some
                        { Handles = [ handle ]
                          ResolveAsync =
                            fun _ _ requestedHandle ->
                                resolvedHandles.Add requestedHandle
                                Task.FromResult(if requestedHandle = handle then Some secret else None) }
                  OutputCapture = None }

        let result =
            environment.ExecuteAsync
                (ExecutionContext.Create SandboxConfig.Default)
                (AgentContext.unrestrictedForTests ())
                agent
                handle
            |> _.Result

        let expectedHash =
            secret
            |> Text.Encoding.UTF8.GetBytes
            |> Security.Cryptography.SHA256.HashData
            |> Convert.ToHexString

        Assert.AreEqual(Ok expectedHash, result)
        CollectionAssert.AreEqual([| handle |], resolvedHandles.ToArray())

    [<TestMethod>]
    member _.ProcessEnvironmentRejectsUndeclaredSecretWithoutResolvingIt() =
        let mutable resolverCalled = false

        let agent =
            Agent.createWorker
                "worker-secret"
                "worker-secret"
                "process secret test agent"
                0
                []
                AgentContract.Text
                (WorkerAgentReference.create "process-secret-hash")

        let environment =
            ExecutionEnvironment.processWorker
                { FileName = workerExecutable
                  Arguments = []
                  ProtocolVersion = WorkerProtocol.CurrentVersion
                  IdentityIsolation = ProcessIdentityIsolation.Disabled
                  FileSystemIsolation = ProcessFileSystemIsolation.Disabled
                  SecretResolver =
                    Some
                        { Handles = []
                          ResolveAsync =
                            fun _ _ _ ->
                                resolverCalled <- true
                                Task.FromResult None }
                  OutputCapture = None }

        Assert.ThrowsExactlyAsync<GovernedExecutionException>(fun () ->
            environment.ExecuteAsync
                (ExecutionContext.Create SandboxConfig.Default)
                (AgentContext.unrestrictedForTests ())
                agent
                ("undeclared-" + Guid.NewGuid().ToString("N"))
            :> Task)
        |> _.Wait()

        Assert.IsFalse resolverCalled

    [<TestMethod>]
    member _.ProcessEnvironmentRejectsUnresolvedDeclaredSecret() =
        let handle = "secret-" + Guid.NewGuid().ToString("N")

        let environment =
            ExecutionEnvironment.processWorker
                { FileName = workerExecutable
                  Arguments = []
                  ProtocolVersion = WorkerProtocol.CurrentVersion
                  IdentityIsolation = ProcessIdentityIsolation.Disabled
                  FileSystemIsolation = ProcessFileSystemIsolation.Disabled
                  SecretResolver =
                    Some
                        { Handles = [ handle ]
                          ResolveAsync = fun _ _ _ -> Task.FromResult None }
                  OutputCapture = None }

        let error =
            Assert.ThrowsExactlyAsync<GovernedExecutionException>(fun () ->
                environment.ExecuteAsync
                    (ExecutionContext.Create SandboxConfig.Default)
                    (AgentContext.unrestrictedForTests ())
                    workerAgent
                    "payload"
                :> Task)
            |> _.Result

        StringAssert.Contains(error.Message, "could not be resolved")

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
    [<DoNotParallelize>]
    member _.LinuxLandlockAllowsDeclaredFileAndDeniesUndeclaredFile() =
        if OperatingSystem.IsLinux() then
            let directory =
                IO.Path.Combine(IO.Path.GetTempPath(), "nao-landlock-" + Guid.NewGuid().ToString("N"))

            IO.Directory.CreateDirectory directory |> ignore
            let declaredPath = IO.Path.Combine(directory, "declared.txt")
            let undeclaredPath = IO.Path.Combine(directory, "undeclared.txt")
            let declaredContent = Guid.NewGuid().ToString("N")
            IO.File.WriteAllText(declaredPath, declaredContent)
            IO.File.WriteAllText(undeclaredPath, Guid.NewGuid().ToString("N"))

            try
                let agent =
                    Agent.createWorker
                        "worker-read-file"
                        "worker-read-file"
                        "process filesystem allowlist test agent"
                        0
                        []
                        AgentContract.Text
                        (WorkerAgentReference.create "process-filesystem-probe")

                let sandbox =
                    { SandboxConfig.Default with
                        AllowFileSystem = true
                        AllowedPaths = [ declaredPath ] }

                let environment =
                    ExecutionEnvironment.processWorker
                        { FileName = workerExecutable
                          Arguments = []
                          ProtocolVersion = WorkerProtocol.CurrentVersion
                          IdentityIsolation = ProcessIdentityIsolation.Disabled
                          FileSystemIsolation = ProcessFileSystemIsolation.LinuxLandlock
                          SecretResolver = None
                          OutputCapture = None }

                let result =
                    environment.ExecuteAsync
                        (ExecutionContext.Create sandbox)
                        (AgentContext.unrestrictedForTests ())
                        agent
                        (declaredPath + "\n" + undeclaredPath)
                    |> _.Result

                Assert.AreEqual(Ok(declaredContent + "|<denied>"), result)
            finally
                IO.Directory.Delete(directory, true)

    [<TestMethod>]
    member _.LinuxLandlockRejectsRelativeAllowedPathBeforeSpawn() =
        if OperatingSystem.IsLinux() then
            let environment =
                ExecutionEnvironment.processWorker
                    { FileName = workerExecutable
                      Arguments = []
                      ProtocolVersion = WorkerProtocol.CurrentVersion
                      IdentityIsolation = ProcessIdentityIsolation.Disabled
                      FileSystemIsolation = ProcessFileSystemIsolation.LinuxLandlock
                      SecretResolver = None
                      OutputCapture = None }

            let sandbox =
                { SandboxConfig.Default with
                    AllowFileSystem = true
                    AllowedPaths = [ "relative-" + Guid.NewGuid().ToString("N") ] }

            let error =
                Assert.ThrowsExactlyAsync<GovernedExecutionException>(fun () ->
                    environment.ExecuteAsync
                        (ExecutionContext.Create sandbox)
                        (AgentContext.unrestrictedForTests ())
                        workerAgent
                        "payload"
                    :> Task)
                |> _.Result

            StringAssert.Contains(error.Message, "allowlist paths must be fully qualified")

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
    member _.ProcessEnvironmentReturnsStructuredMemoryLimit() =
        let agent =
            Agent.createWorker
                "worker-memory"
                "worker-memory"
                "process memory limit test agent"
                0
                []
                AgentContract.Text
                (WorkerAgentReference.create "process-delay")

        let sandbox =
            { SandboxConfig.Default with
                Limits =
                    { ResourceLimits.Unlimited with
                        MaxMemoryBytes = Some 1L } }

        let execution = ExecutionContext.Create sandbox
        let environment = processEnvironment WorkerProtocol.CurrentVersion []

        let result =
            environment.ExecuteAsync execution (AgentContext.unrestrictedForTests ()) agent "30000"
            |> _.Result

        Assert.AreEqual(Error LimitExceeded.Memory, result)

    [<TestMethod>]
    member _.ProcessEnvironmentReturnsEveryStructuredWorkerBudgetLimit() =
        let amount = Random.Shared.Next(2, 20)

        let cases =
            [ LimitExceeded.LlmCalls,
              { ResourceLimits.Unlimited with
                  MaxLlmCalls = 0 },
              "llm-call"
              LimitExceeded.ToolCalls,
              { ResourceLimits.Unlimited with
                  MaxToolCalls = 0 },
              "tool-call"
              LimitExceeded.TotalTokens,
              { ResourceLimits.Unlimited with
                  MaxTotalTokens = amount - 1 },
              sprintf "llm-usage|%d|0" amount
              LimitExceeded.Cost,
              { ResourceLimits.Unlimited with
                  MaxCostUsd = decimal (amount - 1) },
              sprintf "llm-usage|0|%d" amount ]

        let agent =
            Agent.createWorker
                "worker-budget"
                "worker-budget"
                "process worker budget test agent"
                0
                []
                AgentContract.Text
                (WorkerAgentReference.create "process-budget")

        for expected, limits, input in cases do
            let execution =
                ExecutionContext.Create
                    { SandboxConfig.Default with
                        Limits = limits }

            let result =
                (processEnvironment WorkerProtocol.CurrentVersion []).ExecuteAsync
                    execution
                    (AgentContext.unrestrictedForTests ())
                    agent
                    input
                |> _.Result

            Assert.AreEqual(Error expected, result, sprintf "Expected worker limit %A." expected)

    [<TestMethod>]
    member _.ProcessEnvironmentMergesWorkerUsageIntoHostBudget() =
        let tokens = Random.Shared.Next(2, 20)
        let cost = decimal (Random.Shared.Next(2, 20))

        let agent =
            Agent.createWorker
                "worker-budget"
                "worker-budget"
                "process worker usage test agent"
                0
                []
                AgentContract.Text
                (WorkerAgentReference.create "process-budget")

        let execution = ExecutionContext.Create SandboxConfig.Default

        let result =
            (processEnvironment WorkerProtocol.CurrentVersion []).ExecuteAsync
                execution
                (AgentContext.unrestrictedForTests ())
                agent
                (sprintf "llm|%d|%M" tokens cost)
            |> _.Result

        Assert.AreEqual(Ok "completed", result)
        Assert.AreEqual(1, execution.Usage.LlmCalls)
        Assert.AreEqual(tokens, execution.Usage.TotalTokens)
        Assert.AreEqual(cost, execution.Usage.EstimatedCostUsd)

    [<TestMethod>]
    member _.ProcessEnvironmentEnforcesRemainingHostBudget() =
        let agent =
            Agent.createWorker
                "worker-budget"
                "worker-budget"
                "process worker remaining budget test agent"
                0
                []
                AgentContract.Text
                (WorkerAgentReference.create "process-budget")

        let execution =
            ExecutionContext.Create
                { SandboxConfig.Default with
                    Limits =
                        { ResourceLimits.Unlimited with
                            MaxToolCalls = 1 } }

        execution.RecordToolCall()

        let result =
            (processEnvironment WorkerProtocol.CurrentVersion []).ExecuteAsync
                execution
                (AgentContext.unrestrictedForTests ())
                agent
                "tool-call"
            |> _.Result

        Assert.AreEqual(Error LimitExceeded.ToolCalls, result)
        Assert.AreEqual(1, execution.Usage.ToolCalls)

    [<TestMethod>]
    [<DoNotParallelize>]
    member _.ProcessEnvironmentReturnsStructuredCpuLimit() =
        let agent =
            Agent.createWorker
                "worker-cpu"
                "worker-cpu"
                "process CPU limit test agent"
                0
                []
                AgentContract.Text
                (WorkerAgentReference.create "process-cpu")

        let sandbox =
            { SandboxConfig.Default with
                Limits =
                    { ResourceLimits.Unlimited with
                        MaxCpuTime = Some(TimeSpan.FromMilliseconds 50.0) } }

        let execution = ExecutionContext.Create sandbox
        let environment = processEnvironment WorkerProtocol.CurrentVersion []

        let result =
            environment.ExecuteAsync execution (AgentContext.unrestrictedForTests ()) agent "30000"
            |> _.Result

        Assert.AreEqual(Error LimitExceeded.Cpu, result)

    [<TestMethod>]
    member _.ProcessEnvironmentDoesNotCountIdleTimeAsCpuTime() =
        let agent =
            Agent.createWorker
                "worker-delay"
                "worker-delay"
                "process CPU allowance test agent"
                0
                []
                AgentContract.Text
                (WorkerAgentReference.create "process-delay")

        let sandbox =
            { SandboxConfig.Default with
                Limits =
                    { ResourceLimits.Unlimited with
                        MaxCpuTime = Some(TimeSpan.FromSeconds 2.0) } }

        let execution = ExecutionContext.Create sandbox
        let environment = processEnvironment WorkerProtocol.CurrentVersion []

        let result =
            environment.ExecuteAsync execution (AgentContext.unrestrictedForTests ()) agent "500"
            |> _.Result

        Assert.AreEqual(Ok "completed", result)

    [<TestMethod>]
    member _.ProcessEnvironmentReturnsOutputWithinByteLimit() =
        let outputLength = Random.Shared.Next(64, 257)

        let agent =
            Agent.createWorker
                "worker-output"
                "worker-output"
                "process output test agent"
                0
                []
                AgentContract.Text
                (WorkerAgentReference.create "process-output")

        let sandbox =
            { SandboxConfig.Default with
                Limits =
                    { ResourceLimits.Unlimited with
                        MaxOutputBytes = Some 4096L } }

        let execution = ExecutionContext.Create sandbox
        let environment = processEnvironment WorkerProtocol.CurrentVersion []

        match
            environment.ExecuteAsync execution (AgentContext.unrestrictedForTests ()) agent (string outputLength)
            |> _.Result
        with
        | Error limit -> Assert.Fail(sprintf "Expected worker result, got limit %A." limit)
        | Ok output ->
            Assert.AreEqual(outputLength, output.Length)
            Assert.IsTrue(output |> Seq.forall ((=) 'x'))

    [<TestMethod>]
    [<DoNotParallelize>]
    member _.ProcessEnvironmentReturnsStructuredStdoutLimit() =
        let agent =
            Agent.createWorker
                "worker-output"
                "worker-output"
                "process output limit test agent"
                0
                []
                AgentContract.Text
                (WorkerAgentReference.create "process-output")

        let sandbox =
            { SandboxConfig.Default with
                Limits =
                    { ResourceLimits.Unlimited with
                        MaxOutputBytes = Some 1024L } }

        let execution = ExecutionContext.Create sandbox
        let environment = processEnvironment WorkerProtocol.CurrentVersion []

        let result =
            environment.ExecuteAsync execution (AgentContext.unrestrictedForTests ()) agent "8192"
            |> _.Result

        Assert.AreEqual(Error LimitExceeded.Output, result)

    [<TestMethod>]
    [<DoNotParallelize>]
    member _.ProcessEnvironmentReturnsStructuredStderrLimit() =
        let agent =
            Agent.createWorker
                "worker-stderr"
                "worker-stderr"
                "process standard error limit test agent"
                0
                []
                AgentContract.Text
                (WorkerAgentReference.create "process-stderr")

        let sandbox =
            { SandboxConfig.Default with
                Limits =
                    { ResourceLimits.Unlimited with
                        MaxOutputBytes = Some 1024L } }

        let execution = ExecutionContext.Create sandbox
        let environment = processEnvironment WorkerProtocol.CurrentVersion []

        let result =
            environment.ExecuteAsync execution (AgentContext.unrestrictedForTests ()) agent "8192"
            |> _.Result

        Assert.AreEqual(Error LimitExceeded.Output, result)

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
