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

    let createContainerRuntime capabilityJson =
        let directory =
            IO.Path.Combine(IO.Path.GetTempPath(), "nao-container-capability-" + Guid.NewGuid().ToString("N"))

        IO.Directory.CreateDirectory directory |> ignore
        let runtime = IO.Path.Combine(directory, "runtime")

        IO.File.WriteAllText(
            runtime,
            "#!/bin/sh\nif [ \"$1\" = \"info\" ]; then printf '%s\\n' '"
            + capabilityJson
            + "'; exit 0; fi\nexit 99\n"
        )

        IO.File.SetUnixFileMode(
            runtime,
            IO.UnixFileMode.UserRead
            ||| IO.UnixFileMode.UserWrite
            ||| IO.UnixFileMode.UserExecute
        )

        directory, runtime

    let supportedContainerCapabilities =
        "{\"ServerVersion\":\"test\",\"OSType\":\"linux\",\"MemoryLimit\":true,\"PidsLimit\":true,\"CgroupVersion\":\"2\"}"

    let testImageVerifier =
        { VerifierId = "test-verifier"
          PolicyVersion = "test-policy-v1"
          VerifyAsync = fun _ _ _ -> Task.FromResult(Ok(Map.ofList [ "subject", "nao-worker"; "verified", "true" ])) }

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
    member _.ContainerEnvironmentRejectsMissingRuntime() =
        let missingRuntime =
            IO.Path.Combine(IO.Path.GetTempPath(), "nao-missing-container-runtime-" + Guid.NewGuid().ToString("N"))

        let result =
            ExecutionEnvironment.tryContainerWorker
                { RuntimeFileName = missingRuntime
                  RuntimeArguments = []
                  ImageReference = "example@sha256:" + String('0', 64)
                  SupplyChainMetadata = Map.empty
                  ImageVerifier = testImageVerifier
                  Mounts = []
                  Devices = []
                  TemporaryStorage = []
                  NetworkPolicy = ContainerNetworkPolicy.Denied
                  WorkerCommand = []
                  ProtocolVersion = WorkerProtocol.CurrentVersion
                  SecretResolver = None
                  OutputCapture = None }

        match result with
        | Ok _ -> Assert.Fail("Expected an unsupported container host result.")
        | Error reasons -> Assert.IsTrue(reasons |> List.contains "Container runtime executable is unavailable.")

    [<TestMethod>]
    member _.ContainerEnvironmentReportsUnsupportedHostCapabilities() =
        let directory, runtime =
            createContainerRuntime
                "{\"ServerVersion\":\"test\",\"OSType\":\"windows\",\"MemoryLimit\":false,\"PidsLimit\":false}"

        try
            let result =
                ExecutionEnvironment.tryContainerWorker
                    { RuntimeFileName = runtime
                      RuntimeArguments = []
                      ImageReference = "nao-worker@sha256:" + String('a', 64)
                      SupplyChainMetadata = Map.empty
                      ImageVerifier = testImageVerifier
                      Mounts = []
                      Devices = []
                      TemporaryStorage = []
                      NetworkPolicy = ContainerNetworkPolicy.Denied
                      WorkerCommand = []
                      ProtocolVersion = WorkerProtocol.CurrentVersion
                      SecretResolver = None
                      OutputCapture = None }

            match result with
            | Ok _ -> Assert.Fail("Expected an unsupported container host result.")
            | Error reasons ->
                Assert.AreEqual(3, reasons.Length)
                Assert.IsTrue(reasons |> List.contains "Container runtime must provide Linux containers.")

                Assert.IsTrue(
                    reasons
                    |> List.contains "Container runtime does not report memory-limit support."
                )

                Assert.IsTrue(
                    reasons
                    |> List.contains "Container runtime does not report process-count limit support."
                )
        finally
            IO.Directory.Delete(directory, true)

    [<TestMethod>]
    member _.ContainerEnvironmentReportsSupportedHostCapabilities() =
        let directory, runtime = createContainerRuntime supportedContainerCapabilities

        try
            let config =
                { RuntimeFileName = runtime
                  RuntimeArguments = []
                  ImageReference = "nao-worker@sha256:" + String('a', 64)
                  SupplyChainMetadata = Map.empty
                  ImageVerifier = testImageVerifier
                  Mounts = []
                  Devices = []
                  TemporaryStorage = []
                  NetworkPolicy = ContainerNetworkPolicy.Denied
                  WorkerCommand = []
                  ProtocolVersion = WorkerProtocol.CurrentVersion
                  SecretResolver = None
                  OutputCapture = None }

            match ExecutionEnvironment.detectContainerHost config with
            | ContainerHostSupport.Unsupported reasons ->
                Assert.Fail(sprintf "Expected supported container host, got %A." reasons)
            | ContainerHostSupport.Supported capabilities ->
                Assert.AreEqual("test", capabilities.RuntimeVersion)
                Assert.AreEqual("linux", capabilities.OperatingSystem)
                Assert.AreEqual(Some "2", capabilities.CgroupVersion)
        finally
            IO.Directory.Delete(directory, true)

    [<TestMethod>]
    member _.ContainerEnvironmentRejectsUnpinnedImage() =
        let error =
            Assert.ThrowsExactly<ArgumentException>(fun () ->
                ExecutionEnvironment.containerWorker
                    { RuntimeFileName = workerExecutable
                      RuntimeArguments = []
                      ImageReference = "nao-worker:latest"
                      SupplyChainMetadata = Map.empty
                      ImageVerifier = testImageVerifier
                      Mounts = []
                      Devices = []
                      TemporaryStorage = []
                      NetworkPolicy = ContainerNetworkPolicy.Denied
                      WorkerCommand = []
                      ProtocolVersion = WorkerProtocol.CurrentVersion
                      SecretResolver = None
                      OutputCapture = None }
                |> ignore)

        StringAssert.Contains(error.Message, "pinned by a SHA-256 digest")

    [<TestMethod>]
    member _.ContainerEnvironmentRejectsRelativeWorkingDirectoryBeforeSideEffects() =
        let directory, runtime = createContainerRuntime supportedContainerCapabilities

        try
            let mutable artifactPublished = false

            let environment =
                ExecutionEnvironment.containerWorker
                    { RuntimeFileName = runtime
                      RuntimeArguments = []
                      ImageReference = "nao-worker@sha256:" + String('a', 64)
                      SupplyChainMetadata = Map.empty
                      ImageVerifier = testImageVerifier
                      Mounts = []
                      Devices = []
                      TemporaryStorage = []
                      NetworkPolicy = ContainerNetworkPolicy.Denied
                      WorkerCommand = []
                      ProtocolVersion = WorkerProtocol.CurrentVersion
                      SecretResolver = None
                      OutputCapture = None }

            let sandbox =
                { SandboxConfig.Default with
                    WorkingDirectory = Some("relative-" + Guid.NewGuid().ToString("N")) }

            let agentContext =
                { AgentContext.unrestrictedForTests () with
                    PublishArtifact =
                        fun _ ->
                            artifactPublished <- true
                            Task.CompletedTask }

            let error =
                Assert.ThrowsExactlyAsync<GovernedExecutionException>(fun () ->
                    environment.ExecuteAsync (ExecutionContext.Create sandbox) agentContext workerAgent "payload"
                    :> Task)
                |> _.Result

            StringAssert.Contains(error.Message, "working directory must be absolute")
            Assert.IsFalse artifactPublished
        finally
            IO.Directory.Delete(directory, true)

    [<TestMethod>]
    member _.ContainerEnvironmentRejectsDestinationAllowlistWhenBrokerFails() =
        let directory, runtime = createContainerRuntime supportedContainerCapabilities

        try
            let mutable artifactPublished = false
            let destinations = [ Uri("https://api.example.invalid/v1") ]

            let broker =
                { BrokerId = "test-broker"
                  PolicyVersion = "egress-v1"
                  AcquireAsync =
                    fun _ _ requested ->
                        Assert.AreEqual<Uri list>(destinations, requested)
                        Task.FromResult(Error "policy network unavailable") }

            let environment =
                ExecutionEnvironment.containerWorker
                    { RuntimeFileName = runtime
                      RuntimeArguments = []
                      ImageReference = "nao-worker@sha256:" + String('a', 64)
                      SupplyChainMetadata = Map.empty
                      ImageVerifier = testImageVerifier
                      Mounts = []
                      Devices = []
                      TemporaryStorage = []
                      NetworkPolicy = ContainerNetworkPolicy.DestinationAllowlist(destinations, broker)
                      WorkerCommand = []
                      ProtocolVersion = WorkerProtocol.CurrentVersion
                      SecretResolver = None
                      OutputCapture = None }

            let agentContext =
                { AgentContext.unrestrictedForTests () with
                    PublishArtifact =
                        fun _ ->
                            artifactPublished <- true
                            Task.CompletedTask }

            let error =
                Assert.ThrowsExactlyAsync<GovernedExecutionException>(fun () ->
                    environment.ExecuteAsync
                        (ExecutionContext.Create SandboxConfig.Default)
                        agentContext
                        workerAgent
                        "payload"
                    :> Task)
                |> _.Result

            StringAssert.Contains(error.Message, "policy network unavailable")
            Assert.IsFalse artifactPublished
        finally
            IO.Directory.Delete(directory, true)

    [<TestMethod>]
    member _.ContainerEnvironmentRejectsUnverifiedImageBeforeSideEffects() =
        let directory, runtime = createContainerRuntime supportedContainerCapabilities
        let launchMarker = IO.Path.Combine(directory, "launched")
        let mutable artifactPublished = false
        let mutable verifiedReference = None

        IO.File.WriteAllText(
            runtime,
            "#!/bin/sh\nif [ \"$1\" = \"info\" ]; then printf '%s\\n' '"
            + supportedContainerCapabilities
            + "'; exit 0; fi\ntouch '"
            + launchMarker
            + "'\nexit 99\n"
        )

        try
            let verifier =
                { VerifierId = "rejecting-verifier"
                  PolicyVersion = "deny-v1"
                  VerifyAsync =
                    fun _ _ imageReference ->
                        verifiedReference <- Some imageReference
                        Task.FromResult(Error "attestation subject did not match") }

            let environment =
                ExecutionEnvironment.containerWorker
                    { RuntimeFileName = runtime
                      RuntimeArguments = []
                      ImageReference = "nao-worker@sha256:" + String('c', 64)
                      SupplyChainMetadata = Map.empty
                      ImageVerifier = verifier
                      Mounts = []
                      Devices = []
                      TemporaryStorage = []
                      NetworkPolicy = ContainerNetworkPolicy.Denied
                      WorkerCommand = []
                      ProtocolVersion = WorkerProtocol.CurrentVersion
                      SecretResolver = None
                      OutputCapture = None }

            let agentContext =
                { AgentContext.unrestrictedForTests () with
                    PublishArtifact =
                        fun _ ->
                            artifactPublished <- true
                            Task.CompletedTask }

            let error =
                Assert.ThrowsExactlyAsync<GovernedExecutionException>(fun () ->
                    environment.ExecuteAsync
                        (ExecutionContext.Create SandboxConfig.Default)
                        agentContext
                        workerAgent
                        "payload"
                    :> Task)
                |> _.Result

            StringAssert.Contains(error.Message, "attestation subject did not match")
            Assert.AreEqual(Some("nao-worker@sha256:" + String('c', 64)), verifiedReference)
            Assert.IsFalse artifactPublished
            Assert.IsFalse(IO.File.Exists launchMarker)
        finally
            IO.Directory.Delete(directory, true)

    [<TestMethod>]
    [<DoNotParallelize>]
    member _.ContainerEnvironmentRunsWorkerWithRestrictedLaunchArguments() =
        if OperatingSystem.IsLinux() then
            let directory =
                IO.Path.Combine(IO.Path.GetTempPath(), "nao-container-runtime-" + Guid.NewGuid().ToString("N"))

            IO.Directory.CreateDirectory directory |> ignore
            let runtime = IO.Path.Combine(directory, "runtime")
            let argumentsPath = IO.Path.Combine(directory, "arguments")
            let cleanupArgumentsPath = runtime + ".cleanup"
            let artifacts = ResizeArray<Artifact>()
            let maximumMemoryBytes = int64 (Random.Shared.Next(512, 769)) * 1024L * 1024L
            let maximumProcesses = Random.Shared.Next(8, 33)
            let mutable releasedEgressLeases = 0

            let availableDestinations =
                [ Uri("https://api.example.invalid/v1")
                  Uri("https://objects.example.invalid:8443/artifacts")
                  Uri("http://metadata.example.invalid/status") ]

            let destinations =
                availableDestinations
                |> List.take (Random.Shared.Next(1, availableDestinations.Length + 1))

            let acquireEgressLease _ _ requested =
                Assert.AreEqual<Uri list>(destinations, requested)

                let claims =
                    Map.ofList [ "enforcement", "proxy"; "destinationCount", string requested.Length ]

                let lease =
                    { NetworkName = "nao-egress-test"
                      Claims = claims
                      Release = fun () -> releasedEgressLeases <- releasedEgressLeases + 1 }

                Task.FromResult(Ok lease)

            let egressBroker =
                { BrokerId = "test-egress-broker"
                  PolicyVersion = "test-egress-v1"
                  AcquireAsync = acquireEgressLease }

            let maximumCpuTime =
                TimeSpan.FromMilliseconds(float (Random.Shared.Next(1100, 1900)))

            IO.File.WriteAllText(
                runtime,
                "#!/bin/sh\nif [ \"$1\" = \"info\" ]; then printf '%s\\n' '"
                + supportedContainerCapabilities
                + "'; exit 0; fi\nif [ \"$1\" = \"rm\" ]; then printf '%s\\n' \"$@\" > \"$0.cleanup\"; exit 0; fi\nprintf '%s\\n' \"$@\" > \"$NAO_TEST_ARGUMENTS\"\ncidfile=''\nwhile [ \"$#\" -gt 0 ]; do\n  if [ \"$1\" = \"--cidfile\" ]; then shift; cidfile=\"$1\"; fi\n  shift\ndone\nprintf 'test-container' > \"$cidfile\"\nif [ \"$NAO_TEST_RUNTIME_MODE\" = \"fail\" ]; then exit 17; fi\nif [ \"$NAO_TEST_RUNTIME_MODE\" = \"wait\" ]; then while :; do sleep 1; done; fi\nexec \"$NAO_TEST_WORKER\"\n"
            )

            IO.File.SetUnixFileMode(
                runtime,
                IO.UnixFileMode.UserRead
                ||| IO.UnixFileMode.UserWrite
                ||| IO.UnixFileMode.UserExecute
            )

            try
                let containerConfig =
                    { RuntimeFileName = runtime
                      RuntimeArguments = []
                      ImageReference = "nao-worker@sha256:" + String('a', 64)
                      SupplyChainMetadata = Map.ofList [ "provenance", "https://example.invalid/nao-worker" ]
                      ImageVerifier = testImageVerifier
                      Mounts =
                        [ { SourcePath = directory
                            TargetPath = "/input"
                            ReadOnly = true } ]
                      Devices =
                        [ { HostPath = "/dev/null"
                            ContainerPath = "/dev/test-null"
                            Permissions = "r" } ]
                      TemporaryStorage = [ { Path = "/tmp"; MaxBytes = 4096L } ]
                      NetworkPolicy = ContainerNetworkPolicy.Denied
                      WorkerCommand = [ "/app/Nao.Agents.Worker" ]
                      ProtocolVersion = WorkerProtocol.CurrentVersion
                      SecretResolver = None
                      OutputCapture = None }

                let sandbox =
                    { SandboxConfig.Default with
                        WorkingDirectory = Some "/container/work"
                        Limits =
                            { ResourceLimits.Unlimited with
                                MaxMemoryBytes = Some maximumMemoryBytes
                                MaxCpuTime = Some maximumCpuTime
                                MaxProcessCount = Some maximumProcesses
                                MaxOutputBytes = Some 16384L }
                        EnvironmentVariables =
                            Map.ofList [ "NAO_TEST_ARGUMENTS", argumentsPath; "NAO_TEST_WORKER", workerExecutable ] }

                let agentContext =
                    { AgentContext.unrestrictedForTests () with
                        NextArtifactKey = fun () -> IdempotencyKey.parse ("container:" + String('b', 64))
                        PublishArtifact =
                            fun artifact ->
                                artifacts.Add artifact
                                Task.CompletedTask }

                let execute networkPolicy allowNetwork =
                    let environment =
                        ExecutionEnvironment.containerWorker
                            { containerConfig with
                                NetworkPolicy = networkPolicy }

                    let result =
                        environment.ExecuteAsync
                            (ExecutionContext.Create
                                { sandbox with
                                    AllowNetwork = allowNetwork })
                            agentContext
                            workerAgent
                            "payload"
                        |> _.Result

                    match result with
                    | Error limit -> Assert.Fail(sprintf "Expected container worker result, got limit %A." limit)
                    | Ok output -> StringAssert.Contains(output, "|payload|")

                    IO.File.ReadAllLines argumentsPath

                let arguments = execute ContainerNetworkPolicy.Denied true
                CollectionAssert.Contains(arguments, "--rm")
                CollectionAssert.Contains(arguments, "--interactive")
                CollectionAssert.Contains(arguments, "--name")
                CollectionAssert.Contains(arguments, "--user")
                CollectionAssert.Contains(arguments, "65532:65532")
                CollectionAssert.Contains(arguments, "--read-only")
                CollectionAssert.Contains(arguments, "--cap-drop=ALL")
                CollectionAssert.Contains(arguments, "--security-opt=no-new-privileges")
                CollectionAssert.Contains(arguments, "--network=none")
                CollectionAssert.Contains(arguments, sprintf "--memory=%d" maximumMemoryBytes)
                CollectionAssert.Contains(arguments, sprintf "--memory-swap=%d" maximumMemoryBytes)
                CollectionAssert.Contains(arguments, "--ulimit=cpu=2:2")
                CollectionAssert.Contains(arguments, sprintf "--pids-limit=%d" maximumProcesses)
                CollectionAssert.DoesNotContain(arguments, "16384")
                CollectionAssert.Contains(arguments, "--workdir")
                CollectionAssert.Contains(arguments, "/container/work")
                CollectionAssert.Contains(arguments, "--mount")
                CollectionAssert.Contains(arguments, sprintf "type=bind,src=%s,dst=/input,readonly" directory)
                CollectionAssert.Contains(arguments, "--device")
                CollectionAssert.Contains(arguments, "/dev/null:/dev/test-null:r")
                CollectionAssert.Contains(arguments, "--tmpfs")

                CollectionAssert.Contains(
                    arguments,
                    "/tmp:rw,noexec,nosuid,nodev,size=4096,uid=65532,gid=65532,mode=0700"
                )

                CollectionAssert.Contains(arguments, "NAO_TEST_ARGUMENTS")
                CollectionAssert.Contains(arguments, "NAO_TEST_WORKER")
                CollectionAssert.DoesNotContain(arguments, argumentsPath)
                CollectionAssert.DoesNotContain(arguments, workerExecutable)
                Assert.AreEqual(1, artifacts.Count)
                Assert.AreEqual("container.image", artifacts.[0].Kind)
                StringAssert.Contains(artifacts.[0].Payload, "nao-worker@sha256:")
                StringAssert.Contains(artifacts.[0].Payload, "provenance")
                StringAssert.Contains(artifacts.[0].Payload, "test-verifier")
                StringAssert.Contains(artifacts.[0].Payload, "test-policy-v1")
                StringAssert.Contains(artifacts.[0].Payload, "verified")

                let nameIndex = arguments |> Array.findIndex ((=) "--name")
                let containerName = arguments.[nameIndex + 1]
                StringAssert.StartsWith(containerName, "nao-")

                CollectionAssert.AreEqual(
                    [| "rm"; "--force"; "--volumes"; containerName |],
                    IO.File.ReadAllLines cleanupArgumentsPath
                )

                let failingEnvironment =
                    ExecutionEnvironment.containerWorker
                        { containerConfig with
                            NetworkPolicy = ContainerNetworkPolicy.Denied }

                let failingSandbox =
                    { sandbox with
                        EnvironmentVariables = sandbox.EnvironmentVariables |> Map.add "NAO_TEST_RUNTIME_MODE" "fail" }

                Assert.ThrowsExactly<GovernedExecutionException>(fun () ->
                    failingEnvironment.ExecuteAsync
                        (ExecutionContext.Create failingSandbox)
                        agentContext
                        workerAgent
                        "payload"
                    |> _.GetAwaiter().GetResult()
                    |> ignore)
                |> ignore

                let failedArguments = IO.File.ReadAllLines argumentsPath
                let failedNameIndex = failedArguments |> Array.findIndex ((=) "--name")
                let failedContainerName = failedArguments.[failedNameIndex + 1]

                CollectionAssert.AreEqual(
                    [| "rm"; "--force"; "--volumes"; failedContainerName |],
                    IO.File.ReadAllLines cleanupArgumentsPath
                )

                IO.File.Delete argumentsPath
                IO.File.Delete cleanupArgumentsPath
                use cancellation = new CancellationTokenSource()

                let waitingSandbox =
                    { sandbox with
                        EnvironmentVariables = sandbox.EnvironmentVariables |> Map.add "NAO_TEST_RUNTIME_MODE" "wait" }

                let waiting =
                    failingEnvironment.ExecuteAsync
                        (ExecutionContext.CreateWithCancellation waitingSandbox cancellation.Token)
                        agentContext
                        workerAgent
                        "payload"

                let rendezvousDeadline = DateTime.UtcNow + TimeSpan.FromSeconds 5.0

                while not (IO.File.Exists argumentsPath) && DateTime.UtcNow < rendezvousDeadline do
                    Task.Delay(TimeSpan.FromMilliseconds 10.0).Wait()

                Assert.IsTrue(IO.File.Exists argumentsPath, "Container runtime did not record its launch arguments.")
                let waitingArguments = IO.File.ReadAllLines argumentsPath
                let waitingNameIndex = waitingArguments |> Array.findIndex ((=) "--name")
                let waitingContainerName = waitingArguments.[waitingNameIndex + 1]
                cancellation.Cancel()

                Assert.ThrowsExactlyAsync<OperationCanceledException>(fun () -> waiting :> Task)
                |> _.Wait()

                CollectionAssert.AreEqual(
                    [| "rm"; "--force"; "--volumes"; waitingContainerName |],
                    IO.File.ReadAllLines cleanupArgumentsPath
                )

                let sandboxDeniedArguments = execute ContainerNetworkPolicy.Unrestricted false
                CollectionAssert.Contains(sandboxDeniedArguments, "--network=none")

                let unrestrictedArguments = execute ContainerNetworkPolicy.Unrestricted true
                CollectionAssert.DoesNotContain(unrestrictedArguments, "--network=none")

                let artifactsBeforeAllowlist = artifacts.Count

                let allowlistArguments =
                    execute (ContainerNetworkPolicy.DestinationAllowlist(destinations, egressBroker)) true

                let networkIndex = allowlistArguments |> Array.findIndex ((=) "--network")
                Assert.AreEqual("nao-egress-test", allowlistArguments.[networkIndex + 1])
                CollectionAssert.DoesNotContain(allowlistArguments, "--network=none")
                Assert.AreEqual(1, releasedEgressLeases)
                Assert.AreEqual(artifactsBeforeAllowlist + 2, artifacts.Count)
                Assert.AreEqual("container.network", artifacts.[artifacts.Count - 1].Kind)
                StringAssert.Contains(artifacts.[artifacts.Count - 1].Payload, "test-egress-broker")
                StringAssert.Contains(artifacts.[artifacts.Count - 1].Payload, "test-egress-v1")
                StringAssert.Contains(artifacts.[artifacts.Count - 1].Payload, string destinations.Length)

                for destination in destinations do
                    StringAssert.Contains(artifacts.[artifacts.Count - 1].Payload, destination.AbsoluteUri)

                let sandboxDeniedAllowlistArguments =
                    execute (ContainerNetworkPolicy.DestinationAllowlist(destinations, egressBroker)) false

                CollectionAssert.Contains(sandboxDeniedAllowlistArguments, "--network=none")
                Assert.AreEqual(1, releasedEgressLeases)

                let failingAllowlistEnvironment =
                    ExecutionEnvironment.containerWorker
                        { containerConfig with
                            NetworkPolicy = ContainerNetworkPolicy.DestinationAllowlist(destinations, egressBroker) }

                Assert.ThrowsExactly<GovernedExecutionException>(fun () ->
                    failingAllowlistEnvironment.ExecuteAsync
                        (ExecutionContext.Create failingSandbox)
                        agentContext
                        workerAgent
                        "payload"
                    |> _.GetAwaiter().GetResult()
                    |> ignore)
                |> ignore

                Assert.AreEqual(2, releasedEgressLeases)

                let publicationFailureContext =
                    { agentContext with
                        PublishArtifact = fun _ -> Task.FromException(InvalidOperationException("artifact unavailable")) }

                Assert.ThrowsExactlyAsync<InvalidOperationException>(fun () ->
                    failingAllowlistEnvironment.ExecuteAsync
                        (ExecutionContext.Create sandbox)
                        publicationFailureContext
                        workerAgent
                        "payload"
                    :> Task)
                |> _.Wait()

                Assert.AreEqual(3, releasedEgressLeases)

                let principal =
                    SecurityPrincipal.createAuthenticated
                        (TenantId.parse "container-tenant")
                        (UserId.parse "container-user")
                        (Some(ServiceId.parse "container-api"))
                        []
                        [ "reader" ]
                        (Map.ofList [ "region", "private-container-region" ])
                        AuthenticationStrength.MultiFactor

                let principalAgent =
                    Agent.createWorker
                        "principal"
                        "principal"
                        "principal propagation probe"
                        0
                        []
                        AgentContract.Text
                        (WorkerAgentReference.create "process-principal-hash")

                let principalExecution = ExecutionContext.Create sandbox

                let principalAuthorization =
                    AuthorizationScope.tryCreate principal None (WorkspaceId.parse "container-workspace") None
                    |> Option.map (
                        AuthorizationScope.startAgent principalAgent.Metadata.Id principalExecution.ExecutionId
                    )

                let principalContext =
                    { agentContext with
                        ExecutionBoundary = ExecutionBoundary.HarnessRequired
                        Authorization = principalAuthorization }

                let expectedHash =
                    sprintf "%A" (Some principal)
                    |> Text.Encoding.UTF8.GetBytes
                    |> Security.Cryptography.SHA256.HashData
                    |> Convert.ToHexString

                let principalResult =
                    failingEnvironment.ExecuteAsync principalExecution principalContext principalAgent "model input"
                    |> _.GetAwaiter().GetResult()

                Assert.AreEqual(Ok expectedHash, principalResult)
                Assert.IsFalse(IO.File.ReadAllText(argumentsPath).Contains("private-container-region"))

                Assert.IsFalse(
                    artifacts
                    |> Seq.exists (fun artifact -> artifact.Payload.Contains("private-container-region"))
                )

                IO.File.Delete argumentsPath

                Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(fun () ->
                    failingEnvironment.ExecuteAsync
                        (ExecutionContext.Create sandbox)
                        { principalContext with
                            Authorization = None }
                        principalAgent
                        "model input"
                    :> Task)
                |> _.Wait()

                Assert.IsFalse(IO.File.Exists argumentsPath)
            finally
                IO.Directory.Delete(directory, true)

    [<TestMethod>]
    member _.ProtectedProcessExecutionRejectsMissingPrincipalBeforeWorkerLaunch() =
        let environment = processEnvironment WorkerProtocol.CurrentVersion []

        let context =
            { AgentContext.unrestrictedForTests () with
                ExecutionBoundary = ExecutionBoundary.HarnessRequired }

        Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(fun () ->
            environment.ExecuteAsync (ExecutionContext.Create SandboxConfig.Default) context workerAgent "payload"
            :> Task)
        |> _.Wait()

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

                let probeStartInfo = Diagnostics.ProcessStartInfo()
                probeStartInfo.FileName <- unshareExecutable
                probeStartInfo.UseShellExecute <- false
                probeStartInfo.CreateNoWindow <- true
                probeStartInfo.Environment.Clear()

                for argument in
                    [ "--user"
                      sprintf "--map-user=%d" userId
                      sprintf "--map-group=%d" groupId
                      "--"
                      "/bin/true" ] do
                    probeStartInfo.ArgumentList.Add argument

                use probe = new Diagnostics.Process(StartInfo = probeStartInfo)
                let supported = probe.Start() && probe.WaitForExit(5000) && probe.ExitCode = 0

                if not probe.HasExited then
                    probe.Kill(true)
                    probe.WaitForExit()

                if not supported then
                    Assert.Inconclusive("This host does not permit the configured Linux user-namespace mapping.")

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
