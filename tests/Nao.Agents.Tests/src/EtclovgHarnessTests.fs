namespace Nao.Agents.Tests

open System
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Persistence
open Nao.Agents

module private EtclovgHarness =
    let runAsync config context agent request =
        Nao.Agents.EtclovgHarness.runAsync config context agent request System.Threading.CancellationToken.None

[<TestClass>]
type EtclovgHarnessTests() =

    let authorization =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") (Some(SessionId.parse "session"))
        |> Option.get

    let makeAgent (response: string) =
        Agent.create "test-agent" "test-agent" "test" 0 [] AgentContract.Text (fun _context _input ->
            Task.FromResult response)

    let request (context: AgentContext) (agent: Agent) input =
        ExecutionRequest.create
            authorization
            (TurnId.parse "turn")
            "conversation"
            agent.Metadata.Id
            input
            SandboxConfig.Default
            Map.empty
            Map.empty
            context.Correlation

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
    member _.TerminalStatusesExposePlatformFailure() =
        let cases =
            [ (ExecutionTerminalStatus.Failed(HarnessError.ExecutionFailed "failed"),
               PlatformErrorCategory.InternalFailure,
               false)
              (ExecutionTerminalStatus.Denied HarnessError.PermissionDenied,
               PlatformErrorCategory.PermissionDenied,
               false)
              (ExecutionTerminalStatus.LimitExceeded LimitExceeded.Duration,
               PlatformErrorCategory.ResourceExhausted,
               false)
              (ExecutionTerminalStatus.Cancelled, PlatformErrorCategory.Cancelled, false)
              (ExecutionTerminalStatus.TimedOut, PlatformErrorCategory.TransientDependency, true)
              (ExecutionTerminalStatus.Indeterminate "unknown", PlatformErrorCategory.InternalFailure, false) ]

        for status, expectedCategory, expectedRetryable in cases do
            let failure = status.ToPlatformFailure(Some "execution-1")
            Assert.AreEqual(expectedCategory, failure.Category)
            Assert.AreEqual(expectedRetryable, failure.Retryable)
            Assert.AreEqual(Some "execution-1", failure.CorrelationId)

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            ExecutionTerminalStatus.Succeeded.ToPlatformFailure None |> ignore)
        |> ignore

    [<TestMethod>]
    member _.SuccessfulExecutionReturnsResponse() =
        let agent = makeAgent "hello world"
        let config = EtclovgConfig.Default

        let context = AgentContext.unrestrictedForTests ()

        let result =
            (EtclovgHarness.runAsync config context agent (request context agent "test")).Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        Assert.AreEqual(Some "hello world", result.Outputs.Response)
        Assert.IsTrue(result.Evidence.Trace.IsSome)

    [<TestMethod>]
    member _.HarnessPersistsTypedBoundaryCheckpoints() =
        let agent = makeAgent "checkpointed"
        let journal = InMemoryExecutionJournal.create ()

        let config =
            { EtclovgConfig.Default with
                ExecutionJournal = Some journal }

        let context = AgentContext.unrestrictedForTests ()
        let executionRequest = request context agent "input"
        let result = (EtclovgHarness.runAsync config context agent executionRequest).Result

        let checkpoints =
            journal.Checkpoints.GetByExecution result.Correlation.ExecutionId |> _.Result

        CollectionAssert.AreEqual(
            [| HarnessCheckpointPhase.Accepted
               HarnessCheckpointPhase.ExecutionStarted
               HarnessCheckpointPhase.Succeeded |],
            checkpoints |> List.map _.Phase |> List.toArray
        )

        Assert.IsTrue(
            checkpoints
            |> List.forall (fun checkpoint -> checkpoint.Correlation = result.Correlation)
        )

        Assert.IsTrue(
            checkpoints
            |> List.forall (fun checkpoint -> checkpoint.AgentId = agent.Metadata.Id)
        )

        Assert.IsTrue(checkpoints |> List.forall (fun checkpoint -> checkpoint.TurnId = "turn"))

    [<TestMethod>]
    member _.RetryDiscoversPriorAttemptCheckpointsByLogicalExecution() =
        let agent = makeAgent "checkpointed"
        let journal = InMemoryExecutionJournal.create ()

        let config =
            { EtclovgConfig.Default with
                ExecutionJournal = Some journal }

        let context = AgentContext.unrestrictedForTests ()
        let firstRequest = request context agent "input"

        let _ = (EtclovgHarness.runAsync config context agent firstRequest).Result

        let retryRequest =
            request
                { context with
                    Correlation = CorrelationContext.retry context.Correlation }
                agent
                "input"

        let checkpoints =
            journal.Checkpoints.GetByIdempotencyKey retryRequest.IdempotencyKey |> _.Result

        Assert.AreNotEqual(firstRequest.Correlation.ExecutionId, retryRequest.Correlation.ExecutionId)
        Assert.AreEqual(firstRequest.IdempotencyKey, retryRequest.IdempotencyKey)
        Assert.AreEqual(3, checkpoints.Length)

        Assert.IsTrue(
            checkpoints
            |> List.forall (fun checkpoint -> checkpoint.Correlation.ExecutionId = firstRequest.Correlation.ExecutionId)
        )

    [<TestMethod>]
    member _.RetryResumesIncompleteExecutionFromSafeCheckpoint() =
        let mutable executions = 0

        let agent =
            Agent.create "resumed" "resumed" "test" 0 [] AgentContract.Text (fun _ _ ->
                executions <- executions + 1
                Task.FromResult "resumed")

        let journal = InMemoryExecutionJournal.create ()

        let config =
            { EtclovgConfig.Default with
                ExecutionJournal = Some journal }

        let context = AgentContext.unrestrictedForTests ()
        let firstRequest = request context agent "input"

        journal.Checkpoints.Save
            { Id = Guid.NewGuid()
              IdempotencyKey = firstRequest.IdempotencyKey
              Correlation = firstRequest.Correlation
              Owner = "owner"
              TurnId = "turn"
              AgentId = agent.Metadata.Id
              Phase = HarnessCheckpointPhase.ExecutionStarted
              RecordedAt = DateTimeOffset.UtcNow }
        |> _.Wait()

        let retryContext =
            { context with
                Correlation = CorrelationContext.retry context.Correlation }

        let retryRequest = request retryContext agent "input"
        let result = (EtclovgHarness.runAsync config retryContext agent retryRequest).Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        Assert.AreEqual(Some "resumed", result.Outputs.Response)
        Assert.AreEqual(1, executions)
        Assert.AreEqual(retryRequest.Correlation, result.Correlation)

    [<TestMethod>]
    member _.RetryDoesNotRerunTerminalLogicalExecution() =
        let mutable executions = 0

        let agent =
            Agent.create "terminal" "terminal" "test" 0 [] AgentContract.Text (fun _ _ ->
                executions <- executions + 1
                Task.FromResult "done")

        let journal = InMemoryExecutionJournal.create ()

        let config =
            { EtclovgConfig.Default with
                ExecutionJournal = Some journal }

        let context = AgentContext.unrestrictedForTests ()
        let firstRequest = request context agent "input"
        let first = (EtclovgHarness.runAsync config context agent firstRequest).Result

        let retryContext =
            { context with
                Correlation = CorrelationContext.retry context.Correlation }

        let retry =
            EtclovgHarness.runAsync config retryContext agent (request retryContext agent "input")
            |> _.Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, first.Status)
        Assert.AreEqual(1, executions)

        match retry.Status with
        | ExecutionTerminalStatus.Indeterminate message -> StringAssert.Contains(message, "terminal checkpoint")
        | status -> Assert.Fail(sprintf "Expected indeterminate retry, got %A." status)

    [<DataTestMethod>]
    [<DataRow("intent")>]
    [<DataRow("interrupted")>]
    [<DataRow("compensation-failed")>]
    [<DataRow("succeeded")>]
    [<DataRow("quarantined")>]
    member _.RetryFailsClosedForAmbiguousExternalSideEffect(phaseName: string) =
        let mutable executed = false

        let agent =
            Agent.create "unsafe-resume" "unsafe-resume" "test" 0 [] AgentContract.Text (fun _ _ ->
                executed <- true
                Task.FromResult "unsafe")

        let journal = InMemoryExecutionJournal.create ()

        let config =
            { EtclovgConfig.Default with
                ExecutionJournal = Some journal }

        let context = AgentContext.unrestrictedForTests ()
        let firstRequest = request context agent "input"

        let phase =
            match phaseName with
            | "intent" -> ToolSideEffectPhase.IntentRecorded
            | "interrupted" -> ToolSideEffectPhase.Interrupted
            | "compensation-failed" -> ToolSideEffectPhase.CompensationFailed
            | "succeeded" -> ToolSideEffectPhase.Succeeded
            | "quarantined" -> ToolSideEffectPhase.Quarantined
            | value -> invalidArg (nameof value) "Unknown side-effect phase."

        journal.Checkpoints.Save
            { Id = Guid.NewGuid()
              IdempotencyKey = firstRequest.IdempotencyKey
              Correlation = firstRequest.Correlation
              Owner = "owner"
              TurnId = "turn"
              AgentId = agent.Metadata.Id
              Phase = HarnessCheckpointPhase.ExecutionStarted
              RecordedAt = DateTimeOffset.UtcNow }
        |> _.Wait()

        journal.SideEffects.Save
            { Id = Guid.NewGuid()
              IdempotencyKey = IdempotencyKey.operation "tool" 1 firstRequest.IdempotencyKey
              Correlation = firstRequest.Correlation
              Owner = "owner"
              TurnId = "turn"
              ToolName = "external"
              Input = "input"
              Phase = phase
              Detail = None
              RecordedAt = DateTimeOffset.UtcNow }
        |> _.Wait()

        let retryContext =
            { context with
                Correlation = CorrelationContext.retry context.Correlation }

        let result =
            EtclovgHarness.runAsync config retryContext agent (request retryContext agent "input")
            |> _.Result

        Assert.IsFalse(executed)

        match result.Status with
        | ExecutionTerminalStatus.Indeterminate message ->
            StringAssert.Contains(message, "without a confirmed durable outcome")
        | status -> Assert.Fail(sprintf "Expected indeterminate retry, got %A." status)

        let transitions =
            journal.SideEffects.GetByExecution firstRequest.Correlation.ExecutionId
            |> _.Result

        Assert.AreEqual(ToolSideEffectPhase.Quarantined, transitions |> List.last |> _.Phase)

        let expectedCount = if phase = ToolSideEffectPhase.Quarantined then 1 else 2

        Assert.AreEqual(expectedCount, transitions.Length)

    [<TestMethod>]
    member _.DirectAgentExecutionFailsClosedWhenHarnessIsRequired() =
        let mutable executed = false

        let agent =
            Agent.create "protected" "protected" "protected" 0 [] AgentContract.Text (fun _ _ ->
                executed <- true
                Task.FromResult "unguarded")

        let context =
            { AgentContext.unrestrictedForTests () with
                ExecutionBoundary = ExecutionBoundary.HarnessRequired }

        let result = (Agent.runAsync context "input" agent).Result

        match result with
        | Error failure -> Assert.AreEqual(PlatformErrorCategory.PermissionDenied, failure.Category)
        | Ok output -> Assert.Fail($"Expected governed execution denial, got: {output}")

        Assert.IsFalse(executed)

    [<TestMethod>]
    member _.HarnessDeadlineStopsUncooperativeAgent() =
        let agent =
            Agent.create "slow" "slow" "slow" 0 [] AgentContract.Text (fun _ _ ->
                task {
                    do! Task.Delay(TimeSpan.FromSeconds 5.0)
                    return "late"
                })

        let context = AgentContext.unrestrictedForTests ()

        let executionRequest =
            { request context agent "test" with
                Sandbox =
                    { SandboxConfig.Default with
                        Limits =
                            { ResourceLimits.Unlimited with
                                MaxDuration = TimeSpan.FromMilliseconds 25.0 } } }

        let result =
            (EtclovgHarness.runAsync EtclovgConfig.Default context agent executionRequest).Result

        Assert.AreEqual(ExecutionTerminalStatus.TimedOut, result.Status)

    [<TestMethod>]
    member _.HarnessPreservesCallerCancellation() =
        let agent =
            Agent.create "cancelled" "cancelled" "cancelled" 0 [] AgentContract.Text (fun _ _ ->
                task {
                    do! Task.Delay(TimeSpan.FromSeconds 5.0)
                    return "late"
                })

        let context = AgentContext.unrestrictedForTests ()
        use cancellation = new System.Threading.CancellationTokenSource()
        cancellation.Cancel()

        let result =
            (Nao.Agents.EtclovgHarness.runAsync
                EtclovgConfig.Default
                context
                agent
                (request context agent "test")
                cancellation.Token)
                .Result

        Assert.AreEqual(ExecutionTerminalStatus.Cancelled, result.Status)

    [<TestMethod>]
    member _.PublishedArtifactRetainsIdentityInExecutionOutput() =
        let mutable produced: Artifact option = None
        let mutable published: Artifact option = None

        let agent =
            Agent.create "artifact-agent" "artifact-agent" "test" 0 [] AgentContract.Text (fun context _ ->
                task {
                    let artifact =
                        Artifact.create (context.NextArtifactKey()) "report" "application/json" "{\"value\":42}"

                    produced <- Some artifact
                    do! context.PublishArtifact artifact

                    return "done"
                })

        let context =
            { (AgentContext.unrestrictedForTests ()) with
                PublishArtifact =
                    fun artifact ->
                        published <- Some artifact
                        Task.CompletedTask }

        let result =
            (EtclovgHarness.runAsync EtclovgConfig.Default context agent (request context agent "test")).Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        Assert.AreEqual(1, result.Outputs.Artifacts.Length)
        Assert.AreEqual(produced, published)
        Assert.AreEqual(produced, Some result.Outputs.Artifacts.Head)
        StringAssert.StartsWith(IdempotencyKey.value result.Outputs.Artifacts.Head.IdempotencyKey, "artifact:")

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

        let context = AgentContext.unrestrictedForTests ()

        let result =
            (EtclovgHarness.runAsync blockConfig context agent (request context agent "test")).Result

        Assert.AreEqual(
            ExecutionTerminalStatus.Denied(HarnessError.PolicyBlocked [ "no execution allowed" ]),
            result.Status
        )

        Assert.AreEqual(1, result.PolicyDecisions.PolicyViolations.Length)

    [<TestMethod>]
    member _.PolicyModifiedInputIsExecuted() =
        let mutable executedInput = ""

        let agent =
            Agent.create "modified-agent" "modified-agent" "test" 0 [] AgentContract.Text (fun _ input ->
                executedInput <- input
                Task.FromResult input)

        let modify =
            { Id = "redact"
              Description = "Redacts input"
              Enforcement = PolicyEnforcement.Modify(fun _ -> "redacted")
              Evaluate = fun _ -> Some "sensitive input" }

        let config =
            { EtclovgConfig.Default with
                PolicyEngine = Some(PolicyEngine.create [ modify ]) }

        let context = AgentContext.unrestrictedForTests ()

        let result =
            (EtclovgHarness.runAsync config context agent (request context agent "secret")).Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        Assert.AreEqual("redacted", executedInput)
        Assert.AreEqual(Some "redacted", result.Outputs.Response)

    [<TestMethod>]
    member _.ToolPolicyModifiedInputIsExecuted() =
        let mutable executedInput = ""

        let tool =
            Tool.create
                "modified-tool"
                "test"
                0
                []
                ToolCodec.text
                ToolCodec.text
                (ToolOperation.create ToolReplaySafety.RetrySafe (fun _ input ->
                    executedInput <- input
                    Task.FromResult(Ok input)))

        let protocol = ToolProtocol.fromTools [ tool ]

        let agent =
            Agent.create "tool-policy-agent" "tool-policy-agent" "test" 0 [] AgentContract.Text (fun context input ->
                task {
                    let! result = protocol.InvokeAsync context (context.NextToolCallKey()) tool.Name input
                    return result.Output
                })

        let modifyTool =
            { Id = "normalize-tool"
              Description = "Normalizes tool input"
              Enforcement = PolicyEnforcement.Modify(fun _ -> "normalized")
              Evaluate =
                fun policyContext ->
                    if policyContext.Action = "tool.execute:modified-tool" then
                        Some "normalize"
                    else
                        None }

        let config =
            { EtclovgConfig.Default with
                PolicyEngine = Some(PolicyEngine.create [ modifyTool ]) }

        let context = AgentContext.unrestrictedForTests ()

        let result =
            (EtclovgHarness.runAsync config context agent (request context agent "raw")).Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        Assert.AreEqual("normalized", executedInput)
        Assert.AreEqual(Some "normalized", result.Outputs.Response)

    [<TestMethod>]
    member _.ExecutionGraphNodesReenterHarnessPolicy() =
        let mutable nodeExecuted = false

        let nodeAgent =
            Agent.create "blocked-node" "Blocked node" "Must not execute" 0 [] AgentContract.Text (fun _ _ ->
                nodeExecuted <- true
                Task.FromResult "unexpected")

        let node =
            { Id = GraphNodeId.create "blocked"
              Agent = nodeAgent }

        let graph =
            { Entry = node.Id
              Nodes = [ node ]
              Edges = []
              MaxSteps = 1 }

        let graphAgent =
            ExecutionGraph.asAgent "graph" "Graph" "Governed graph" 0 [] AgentContract.Text graph

        let denyNode =
            { Id = "deny-node"
              Description = "Blocks only the graph node"
              Enforcement = PolicyEnforcement.Block
              Evaluate =
                fun policyContext ->
                    if policyContext.AgentId = nodeAgent.Metadata.Id then
                        Some "graph node denied"
                    else
                        None }

        let config =
            { EtclovgConfig.Default with
                PolicyEngine = Some(PolicyEngine.create [ denyNode ]) }

        let context = AgentContext.unrestrictedForTests ()

        let result =
            (EtclovgHarness.runAsync config context graphAgent (request context graphAgent "input")).Result

        Assert.AreEqual(ExecutionTerminalStatus.Denied HarnessError.PermissionDenied, result.Status)
        Assert.IsFalse(nodeExecuted)

    [<TestMethod>]
    member _.AgentGroupMembersReenterHarnessPolicy() =
        let mutable memberExecuted = false

        let memberAgent =
            Agent.create "blocked-member" "Blocked member" "Must not execute" 0 [] AgentContract.Text (fun _ _ ->
                memberExecuted <- true
                Task.FromResult "unexpected")

        let group = AgentGroup.create [ memberAgent ] (MaxRounds 1)

        let groupAgent =
            Agent.create "group" "Group" "Governed group" 0 [] AgentContract.Text (fun context input ->
                task {
                    let! history = AgentGroup.runAsync context input group
                    return history |> List.last |> _.Content
                })

        let denyMember =
            { Id = "deny-group-member"
              Description = "Blocks only the group member"
              Enforcement = PolicyEnforcement.Block
              Evaluate =
                fun policyContext ->
                    if policyContext.AgentId = memberAgent.Metadata.Id then
                        Some "group member denied"
                    else
                        None }

        let config =
            { EtclovgConfig.Default with
                PolicyEngine = Some(PolicyEngine.create [ denyMember ]) }

        let context = AgentContext.unrestrictedForTests ()

        let result =
            (EtclovgHarness.runAsync config context groupAgent (request context groupAgent "input")).Result

        Assert.AreEqual(ExecutionTerminalStatus.Denied HarnessError.PermissionDenied, result.Status)
        Assert.IsFalse(memberExecuted)

    [<TestMethod>]
    member _.ToolInvocationsReenterHarnessPolicy() =
        let mutable toolExecuted = false

        let tool =
            Tool.create
                "blocked-tool"
                "Must not execute"
                0
                []
                ToolCodec.text
                ToolCodec.text
                (ToolOperation.create ToolReplaySafety.ExternallySideEffecting (fun _ input ->
                    toolExecuted <- true
                    Task.FromResult(Ok input)))

        let protocol = ToolProtocol.fromTools [ tool ]

        let agent =
            Agent.create
                "tool-agent"
                "Tool agent"
                "Invokes a governed tool"
                0
                []
                AgentContract.Text
                (fun context input ->
                    task {
                        let! result = protocol.InvokeAsync context (context.NextToolCallKey()) tool.Name input

                        match result.Failure with
                        | Some failure -> return PlatformFailure.raiseException (failure.ToPlatformFailure None)
                        | None -> return result.Output
                    })

        let denyTool =
            { Id = "deny-tool"
              Description = "Blocks one tool invocation"
              Enforcement = PolicyEnforcement.Block
              Evaluate =
                fun policyContext ->
                    if policyContext.Action = "tool.execute:blocked-tool" then
                        Some "tool execution denied"
                    else
                        None }

        let config =
            { EtclovgConfig.Default with
                PolicyEngine = Some(PolicyEngine.create [ denyTool ]) }

        let context = AgentContext.unrestrictedForTests ()

        let result =
            (EtclovgHarness.runAsync config context agent (request context agent "input")).Result

        Assert.AreEqual(ExecutionTerminalStatus.Denied HarnessError.PermissionDenied, result.Status)
        Assert.IsFalse(toolExecuted)

    [<TestMethod>]
    member _.HarnessDeadlineFlowsIntoToolContext() =
        use observedCancellation = new System.Threading.ManualResetEventSlim(false)

        let tool =
            Tool.create
                "cancellable-tool"
                "Observes execution cancellation"
                0
                []
                ToolCodec.text
                ToolCodec.text
                (ToolOperation.create ToolReplaySafety.RetrySafe (fun context _ ->
                    task {
                        use _registration =
                            context.CancellationToken.Register(fun () -> observedCancellation.Set())

                        do! Task.Delay(TimeSpan.FromSeconds 5.0, context.CancellationToken)
                        return Ok "late"
                    }))

        let protocol = ToolProtocol.fromTools [ tool ]

        let agent =
            Agent.create "tool-timeout" "tool-timeout" "tool-timeout" 0 [] AgentContract.Text (fun context input ->
                task {
                    let! result = protocol.InvokeAsync context (context.NextToolCallKey()) tool.Name input
                    return result.Output
                })

        let context = AgentContext.unrestrictedForTests ()

        let executionRequest =
            { request context agent "input" with
                Sandbox =
                    { SandboxConfig.Default with
                        Limits =
                            { ResourceLimits.Unlimited with
                                MaxDuration = TimeSpan.FromMilliseconds 500.0 } } }

        let result =
            (EtclovgHarness.runAsync EtclovgConfig.Default context agent executionRequest).Result

        Assert.AreEqual(ExecutionTerminalStatus.TimedOut, result.Status)
        Assert.IsTrue(observedCancellation.Wait(TimeSpan.FromSeconds 1.0))

    [<TestMethod>]
    member _.NestedAgentsShareParentToolBudget() =
        let mutable toolExecutions = 0

        let tool =
            Tool.create
                "budgeted-tool"
                "Counts executions"
                0
                []
                ToolCodec.text
                ToolCodec.text
                (ToolOperation.create ToolReplaySafety.RetrySafe (fun _ input ->
                    toolExecutions <- toolExecutions + 1
                    Task.FromResult(Ok input)))

        let protocol = ToolProtocol.fromTools [ tool ]

        let invokeTool context input =
            task {
                let! result = protocol.InvokeAsync context (context.NextToolCallKey()) tool.Name input

                match result.Failure with
                | Some failure -> return PlatformFailure.raiseException (failure.ToPlatformFailure None)
                | None -> return result.Output
            }

        let child =
            Agent.create "budget-child" "Budget child" "Uses one tool call" 0 [] AgentContract.Text invokeTool

        let parent =
            Agent.create
                "budget-parent"
                "Budget parent"
                "Delegates before using a tool"
                0
                []
                AgentContract.Text
                (fun context input ->
                    task {
                        match! ExecutionRuntime.runAgent context child input with
                        | Error failure -> return PlatformFailure.raiseException failure
                        | Ok _ -> return! invokeTool context input
                    })

        let limits =
            { ResourceLimits.Unlimited with
                MaxToolCalls = 1 }

        let context = AgentContext.unrestrictedForTests ()

        let executionRequest =
            { request context parent "input" with
                Sandbox =
                    { SandboxConfig.Default with
                        Limits = limits } }

        let result =
            (EtclovgHarness.runAsync EtclovgConfig.Default context parent executionRequest).Result

        Assert.AreEqual(ExecutionTerminalStatus.LimitExceeded LimitExceeded.ToolCalls, result.Status)
        Assert.AreEqual(1, toolExecutions)
        Assert.AreEqual(1, result.Usage.ToolCalls)

    [<TestMethod>]
    member _.HarnessBlocksLlmCallsBeforeExceedingBudget() =
        let mutable providerCalls = 0

        let provider =
            LlmProvider.create (fun () -> "budget-provider") (fun _ _ _ ->
                providerCalls <- providerCalls + 1
                Task.FromResult(CompletionResult.create "done" "stop" None None))

        let orchestrator =
            Orchestrator.create
                { Id = "llm-call-budget"
                  Name = "LLM call budget"
                  Description = "Tests LLM call budget enforcement"
                  Priority = 0
                  Responsibilities = []
                  Contract = AgentContract.Text
                  Provider = provider
                  Tools = []
                  SubAgents = []
                  Prompt = Prompt.Empty
                  Options = CompletionOptions.Default
                  MaxRounds = 1
                  Bus = EventBus.none
                  Scope = EventScope.CreateEmpty() }
                { OrchestratorDefinition.create Task.FromResult with
                    ParseActions = fun response -> [ Respond response ] }

        let context = AgentContext.unrestrictedForTests ()

        let executionRequest =
            { request context orchestrator "input" with
                Sandbox =
                    { SandboxConfig.Default with
                        Limits =
                            { ResourceLimits.Unlimited with
                                MaxLlmCalls = 0 } } }

        let result =
            (EtclovgHarness.runAsync EtclovgConfig.Default context orchestrator executionRequest).Result

        Assert.AreEqual(ExecutionTerminalStatus.LimitExceeded LimitExceeded.LlmCalls, result.Status)
        Assert.AreEqual(0, providerCalls)
        Assert.AreEqual(0, result.Usage.LlmCalls)

    [<TestMethod>]
    member _.HarnessAccountsProviderTokensAgainstBudget() =
        let provider =
            LlmProvider.create (fun () -> "token-provider") (fun _ _ _ ->
                let usage = { InputTokens = 4; OutputTokens = 3 }
                Task.FromResult(CompletionResult.create "done" "stop" (Some 7) (Some usage)))

        let orchestrator =
            Orchestrator.create
                { Id = "token-budget"
                  Name = "Token budget"
                  Description = "Tests token budget enforcement"
                  Priority = 0
                  Responsibilities = []
                  Contract = AgentContract.Text
                  Provider = provider
                  Tools = []
                  SubAgents = []
                  Prompt = Prompt.Empty
                  Options = CompletionOptions.Default
                  MaxRounds = 1
                  Bus = EventBus.none
                  Scope = EventScope.CreateEmpty() }
                { OrchestratorDefinition.create Task.FromResult with
                    ParseActions = fun response -> [ Respond response ] }

        let context = AgentContext.unrestrictedForTests ()

        let executionRequest =
            { request context orchestrator "input" with
                Sandbox =
                    { SandboxConfig.Default with
                        Limits =
                            { ResourceLimits.Unlimited with
                                MaxTotalTokens = 5 } } }

        let result =
            (EtclovgHarness.runAsync EtclovgConfig.Default context orchestrator executionRequest).Result

        Assert.AreEqual(ExecutionTerminalStatus.LimitExceeded LimitExceeded.TotalTokens, result.Status)
        Assert.AreEqual(1, result.Usage.LlmCalls)
        Assert.AreEqual(7, result.Usage.TotalTokens)

    [<TestMethod>]
    member _.RecordedProviderAndToolFixturesReplayWithoutLiveAccess() =
        let exchanges = ResizeArray<LlmExchange>()
        let bus = InMemoryEventBus.create ()

        EventBus.subscribe
            (EventConsumer.create (function
                | LlmExchangeRecorded(_, exchange) ->
                    exchanges.Add exchange
                    Task.CompletedTask
                | _ -> Task.CompletedTask))
            bus

        let liveResponses = ResizeArray [ "invoke"; "done" ]
        let mutable liveProviderCalls = 0
        let mutable liveToolCalls = 0

        let liveProvider =
            LlmProvider.create (fun () -> "recording-provider") (fun _ _ _ ->
                let response = liveResponses.[liveProviderCalls]
                liveProviderCalls <- liveProviderCalls + 1
                let usage = { InputTokens = 2; OutputTokens = 1 }
                Task.FromResult(CompletionResult.create response "stop" (Some 3) (Some usage)))

        let liveTool =
            Tool.create
                "echo"
                "Echoes input"
                0
                []
                ToolCodec.text
                ToolCodec.text
                (ToolOperation.create ToolReplaySafety.RetrySafe (fun _ input ->
                    liveToolCalls <- liveToolCalls + 1
                    Task.FromResult(Ok(sprintf "echo:%s" input))))

        let definition =
            { OrchestratorDefinition.create Task.FromResult with
                ParseActions =
                    function
                    | "invoke" -> [ InvokeTool("echo", "hello") ]
                    | response -> [ Respond response ] }

        let createOrchestrator provider tool =
            Orchestrator.create
                { Id = "deterministic-replay"
                  Name = "Deterministic replay"
                  Description = "Records and replays provider and tool fixtures"
                  Priority = 0
                  Responsibilities = []
                  Contract = AgentContract.Text
                  Provider = provider
                  Tools = [ tool ]
                  SubAgents = []
                  Prompt = Prompt.Empty
                  Options = CompletionOptions.Default
                  MaxRounds = 2
                  Bus = bus
                  Scope = EventScope.CreateEmpty() }
                definition

        let liveAgent = createOrchestrator liveProvider liveTool
        let journal = InMemoryExecutionJournal.create ()

        let liveConfig =
            { EtclovgConfig.Default with
                ExecutionJournal = Some journal }

        let context = AgentContext.unrestrictedForTests ()

        let liveResult =
            (EtclovgHarness.runAsync liveConfig context liveAgent (request context liveAgent "start")).Result

        let records = journal.GetHistoryAsync() |> _.Result
        let fixtures = RecordedReplayFixtures.create (List.ofSeq exchanges) records

        let mutable replayProviderCalled = false
        let mutable replayToolCalled = false

        let replayProvider =
            LlmProvider.create (fun () -> "recording-provider") (fun _ _ _ ->
                replayProviderCalled <- true
                invalidOp "Live provider was called during replay.")

        let replayTool =
            Tool.create
                "echo"
                "Echoes input"
                0
                []
                ToolCodec.text
                ToolCodec.text
                (ToolOperation.create ToolReplaySafety.RetrySafe (fun _ _ ->
                    replayToolCalled <- true
                    invalidOp "Live tool was called during replay."))

        let replayAgent = createOrchestrator replayProvider replayTool

        let replayContext =
            { context with
                Correlation = CorrelationContext.retry context.Correlation }

        let replayJournal = InMemoryExecutionJournal.create ()

        let replayConfig =
            { EtclovgConfig.Default with
                ReplayFixtures = Some fixtures
                ExecutionJournal = Some replayJournal }

        let replayResult =
            EtclovgHarness.runAsync replayConfig replayContext replayAgent (request replayContext replayAgent "start")
            |> _.Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, liveResult.Status)
        Assert.AreEqual(liveResult.Status, replayResult.Status)
        Assert.AreEqual(liveResult.Outputs, replayResult.Outputs)
        Assert.AreEqual(liveResult.Usage.LlmCalls, replayResult.Usage.LlmCalls)
        Assert.AreEqual(liveResult.Usage.TotalTokens, replayResult.Usage.TotalTokens)
        Assert.AreEqual(liveResult.Usage.ToolCalls, replayResult.Usage.ToolCalls)
        Assert.AreEqual(liveResult.Usage.EstimatedCostUsd, replayResult.Usage.EstimatedCostUsd)
        Assert.AreEqual(2, liveProviderCalls)
        Assert.AreEqual(1, liveToolCalls)
        Assert.IsFalse(replayProviderCalled)
        Assert.IsFalse(replayToolCalled)
        Assert.IsTrue((replayJournal.GetHistoryAsync() |> _.Result).IsEmpty)

        let replaySideEffects =
            replayJournal.SideEffects.GetByExecution replayResult.Correlation.ExecutionId
            |> _.Result

        Assert.IsTrue(replaySideEffects.IsEmpty)

    [<TestMethod>]
    member _.ReplayFailsClosedWhenProviderFixtureIsMissing() =
        let mutable providerCalled = false

        let provider =
            LlmProvider.create (fun () -> "disabled-provider") (fun _ _ _ ->
                providerCalled <- true
                invalidOp "Live provider was called during replay.")

        let orchestrator =
            Orchestrator.create
                { Id = "missing-replay-fixture"
                  Name = "Missing replay fixture"
                  Description = "Fails closed without a recording"
                  Priority = 0
                  Responsibilities = []
                  Contract = AgentContract.Text
                  Provider = provider
                  Tools = []
                  SubAgents = []
                  Prompt = Prompt.Empty
                  Options = CompletionOptions.Default
                  MaxRounds = 1
                  Bus = EventBus.none
                  Scope = EventScope.CreateEmpty() }
                { OrchestratorDefinition.create Task.FromResult with
                    ParseActions = fun response -> [ Respond response ] }

        let config =
            { EtclovgConfig.Default with
                ReplayFixtures = Some ReplayFixtures.empty }

        let context = AgentContext.unrestrictedForTests ()

        let result =
            (EtclovgHarness.runAsync config context orchestrator (request context orchestrator "start")).Result

        Assert.IsFalse(providerCalled)

        match result.Status with
        | ExecutionTerminalStatus.Failed(HarnessError.ExecutionFailed message) ->
            StringAssert.Contains(message, "live provider access is disabled")
        | status -> Assert.Fail(sprintf "Expected missing replay fixture failure, got %A." status)

    [<TestMethod>]
    member _.ReplayFailsClosedForExplicitlyNonReplayableProvider() =
        let mutable providerCalled = false

        let provider =
            LlmProvider.create (fun () -> "non-replayable-provider") (fun _ _ _ ->
                providerCalled <- true
                invalidOp "Live provider was called during replay.")

        let orchestrator =
            Orchestrator.create
                { Id = "non-replayable-provider"
                  Name = "Non-replayable provider"
                  Description = "Fails closed for an explicit replay refusal"
                  Priority = 0
                  Responsibilities = []
                  Contract = AgentContract.Text
                  Provider = provider
                  Tools = []
                  SubAgents = []
                  Prompt = Prompt.Empty
                  Options = CompletionOptions.Default
                  MaxRounds = 1
                  Bus = EventBus.none
                  Scope = EventScope.CreateEmpty() }
                { OrchestratorDefinition.create Task.FromResult with
                    ParseActions = fun response -> [ Respond response ] }

        let context = AgentContext.unrestrictedForTests ()
        let executionRequest = request context orchestrator "start"

        let providerKey =
            executionRequest.IdempotencyKey |> IdempotencyKey.operation "provider" 1

        let fixtures =
            { ReplayFixtures.empty with
                Providers = Map.ofList [ providerKey, ReplayFixture.NonReplayable "requires live authorization" ] }

        let config =
            { EtclovgConfig.Default with
                ReplayFixtures = Some fixtures }

        let result =
            (EtclovgHarness.runAsync config context orchestrator executionRequest).Result

        Assert.IsFalse(providerCalled)

        match result.Status with
        | ExecutionTerminalStatus.Failed(HarnessError.ExecutionFailed message) ->
            StringAssert.Contains(message, "explicitly non-replayable")
            StringAssert.Contains(message, "requires live authorization")
        | status -> Assert.Fail(sprintf "Expected non-replayable dependency failure, got %A." status)

    [<TestMethod>]
    member _.DirectToolReplayUsesPolicyNormalizedFixtureWithoutLiveAccess() =
        let mutable toolCalled = false

        let tool =
            Tool.create
                "normalized-tool"
                "Must be replayed"
                0
                []
                ToolCodec.text
                ToolCodec.text
                (ToolOperation.create ToolReplaySafety.ExternallySideEffecting (fun _ _ ->
                    toolCalled <- true
                    invalidOp "Live tool was called during replay."))

        let protocol = ToolProtocol.fromTools [ tool ]

        let agent =
            Agent.create
                "direct-tool-replay"
                "Direct tool replay"
                "Replays a direct tool call"
                0
                []
                AgentContract.Text
                (fun context input ->
                    task {
                        let! result = protocol.InvokeAsync context (context.NextToolCallKey()) tool.Name input
                        return result.Output
                    })

        let context = AgentContext.unrestrictedForTests ()
        let executionRequest = request context agent "raw"
        let operationKey = IdempotencyKey.operation "tool" 1 executionRequest.IdempotencyKey

        let fixtureResult: ToolInvocationResult =
            { IdempotencyKey = operationKey
              Success = true
              Output = "recorded"
              Error = None
              Failure = None
              DurationMs = 4L
              ReplaySafety = ToolReplaySafety.ExternallySideEffecting }

        let recordedFixture: ToolReplayFixture =
            { ToolName = tool.Name
              Input = "normalized"
              Result = fixtureResult }

        let fixtures =
            { ReplayFixtures.empty with
                Tools = Map.ofList [ operationKey, ReplayFixture.Recorded recordedFixture ] }

        let normalize =
            { Id = "normalize-replay-input"
              Description = "Normalizes replay input"
              Enforcement = PolicyEnforcement.Modify(fun _ -> "normalized")
              Evaluate =
                fun policyContext ->
                    if policyContext.Action = "tool.execute:normalized-tool" then
                        Some "normalize"
                    else
                        None }

        let config =
            { EtclovgConfig.Default with
                ReplayFixtures = Some fixtures
                PolicyEngine = Some(PolicyEngine.create [ normalize ]) }

        let result = (EtclovgHarness.runAsync config context agent executionRequest).Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        Assert.AreEqual(Some "recorded", result.Outputs.Response)
        Assert.AreEqual(1, result.Usage.ToolCalls)
        Assert.IsFalse(toolCalled)

    [<TestMethod>]
    member _.ReplayFailsClosedForExplicitlyNonReplayableTool() =
        let mutable toolCalled = false

        let tool =
            Tool.create
                "non-replayable-tool"
                "Cannot be replayed"
                0
                []
                ToolCodec.text
                ToolCodec.text
                (ToolOperation.create ToolReplaySafety.RetrySafe (fun _ _ ->
                    toolCalled <- true
                    invalidOp "Live tool was called during replay."))

        let protocol = ToolProtocol.fromTools [ tool ]

        let agent =
            Agent.create
                "non-replayable-tool"
                "Non-replayable tool"
                "Fails closed for an explicit replay refusal"
                0
                []
                AgentContract.Text
                (fun context input ->
                    task {
                        let! result = protocol.InvokeAsync context (context.NextToolCallKey()) tool.Name input
                        return result.Output
                    })

        let context = AgentContext.unrestrictedForTests ()
        let executionRequest = request context agent "input"

        let operationKey =
            executionRequest.IdempotencyKey |> IdempotencyKey.operation "tool" 1

        let fixtures =
            { ReplayFixtures.empty with
                Tools = Map.ofList [ operationKey, ReplayFixture.NonReplayable "depends on ephemeral state" ] }

        let config =
            { EtclovgConfig.Default with
                ReplayFixtures = Some fixtures }

        let result = (EtclovgHarness.runAsync config context agent executionRequest).Result

        Assert.IsFalse(toolCalled)

        match result.Status with
        | ExecutionTerminalStatus.Failed(HarnessError.ExecutionFailed message) ->
            StringAssert.Contains(message, "explicitly non-replayable")
            StringAssert.Contains(message, "depends on ephemeral state")
        | status -> Assert.Fail(sprintf "Expected non-replayable dependency failure, got %A." status)

    [<TestMethod>]
    member _.RecordedToolFailureReplaysProviderRecoveryWithoutLiveAccess() =
        let exchanges = ResizeArray<LlmExchange>()
        let bus = InMemoryEventBus.create ()

        EventBus.subscribe
            (EventConsumer.create (function
                | LlmExchangeRecorded(_, exchange) ->
                    exchanges.Add exchange
                    Task.CompletedTask
                | _ -> Task.CompletedTask))
            bus

        let responses = ResizeArray [ "invoke"; "recovered" ]
        let mutable providerCalls = 0
        let mutable toolCalls = 0

        let provider =
            LlmProvider.create (fun () -> "failure-provider") (fun _ _ _ ->
                let response = responses.[providerCalls]
                providerCalls <- providerCalls + 1
                Task.FromResult(CompletionResult.create response "stop" None None))

        let tool =
            Tool.create
                "failing"
                "Returns a recorded failure"
                0
                []
                ToolCodec.text
                ToolCodec.text
                (ToolOperation.create ToolReplaySafety.RetrySafe (fun _ _ ->
                    toolCalls <- toolCalls + 1
                    Task.FromResult(Error(ToolExecError.Failed "recorded failure"))))

        let definition =
            { OrchestratorDefinition.create Task.FromResult with
                ParseActions =
                    function
                    | "invoke" -> [ InvokeTool("failing", "input") ]
                    | response -> [ Respond response ] }

        let createOrchestrator provider tool =
            Orchestrator.create
                { Id = "failed-tool-replay"
                  Name = "Failed tool replay"
                  Description = "Records and replays a failed tool"
                  Priority = 0
                  Responsibilities = []
                  Contract = AgentContract.Text
                  Provider = provider
                  Tools = [ tool ]
                  SubAgents = []
                  Prompt = Prompt.Empty
                  Options = CompletionOptions.Default
                  MaxRounds = 2
                  Bus = bus
                  Scope = EventScope.CreateEmpty() }
                definition

        let liveAgent = createOrchestrator provider tool
        let journal = InMemoryExecutionJournal.create ()

        let liveConfig =
            { EtclovgConfig.Default with
                ExecutionJournal = Some journal }

        let context = AgentContext.unrestrictedForTests ()

        let liveResult =
            (EtclovgHarness.runAsync liveConfig context liveAgent (request context liveAgent "start")).Result

        let records = journal.GetHistoryAsync() |> _.Result
        let fixtures = RecordedReplayFixtures.create (List.ofSeq exchanges) records

        let replayProvider =
            LlmProvider.create (fun () -> "failure-provider") (fun _ _ _ ->
                invalidOp "Live provider was called during replay.")

        let replayTool =
            Tool.create
                "failing"
                "Returns a recorded failure"
                0
                []
                ToolCodec.text
                ToolCodec.text
                (ToolOperation.create ToolReplaySafety.RetrySafe (fun _ _ ->
                    invalidOp "Live tool was called during replay."))

        let replayAgent = createOrchestrator replayProvider replayTool

        let replayContext =
            { context with
                Correlation = CorrelationContext.retry context.Correlation }

        let replayConfig =
            { EtclovgConfig.Default with
                ReplayFixtures = Some fixtures }

        let replayResult =
            EtclovgHarness.runAsync replayConfig replayContext replayAgent (request replayContext replayAgent "start")
            |> _.Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, liveResult.Status)
        Assert.AreEqual(liveResult.Outputs, replayResult.Outputs)
        Assert.AreEqual(1, records.Length)
        Assert.AreEqual(Some "recorded failure", records.Head.Failure |> Option.map _.Message)
        Assert.IsTrue((journal.GetRevertibleAsync() |> _.Result).IsEmpty)
        Assert.AreEqual(2, providerCalls)
        Assert.AreEqual(1, toolCalls)

    [<TestMethod>]
    member _.FailedExecutionCompensatesReversibleToolsDurably() =
        let reverted = ResizeArray<RevertContext>()

        let operation =
            ToolOperation.create ToolReplaySafety.ExternallySideEffecting (fun _ input ->
                Task.FromResult(Ok(sprintf "created:%s" input)))
            |> ToolOperation.withRevert (fun context ->
                reverted.Add context
                Task.FromResult(Ok()))

        let tool =
            Tool.create "create" "Creates a reversible value" 0 [] ToolCodec.text ToolCodec.text operation

        let protocol = ToolProtocol.fromTools [ tool ]

        let agent =
            Agent.create
                "compensated-agent"
                "Compensated agent"
                "Fails after a reversible tool succeeds"
                0
                []
                AgentContract.Text
                (fun context input ->
                    task {
                        let! _ = protocol.InvokeAsync context (context.NextToolCallKey()) tool.Name input
                        let! _ = protocol.InvokeAsync context (context.NextToolCallKey()) tool.Name (input + "-second")
                        return raise (InvalidOperationException "fail after tool")
                    })

        let journal = InMemoryExecutionJournal.create ()
        let context = AgentContext.unrestrictedForTests ()
        let executionRequest = request context agent "value"

        let config =
            { EtclovgConfig.Default with
                ExecutionJournal = Some journal }

        let result = (EtclovgHarness.runAsync config context agent executionRequest).Result
        let history = journal.GetHistoryAsync() |> _.Result

        let transitions =
            journal.SideEffects.GetByExecution result.Correlation.ExecutionId |> _.Result

        match result.Status with
        | ExecutionTerminalStatus.Failed _ -> ()
        | status -> Assert.Fail(sprintf "Expected failed execution, got %A." status)

        Assert.AreEqual(2, reverted.Count)
        Assert.AreEqual("value-second", reverted.[0].Input)
        Assert.AreEqual("created:value-second", reverted.[0].Output)
        Assert.AreEqual("value", reverted.[1].Input)
        Assert.AreEqual("created:value", reverted.[1].Output)
        Assert.AreEqual(2, history.Length)
        Assert.IsTrue(history |> List.forall _.Reverted)
        Assert.IsTrue((journal.GetRevertibleAsync() |> _.Result).IsEmpty)

        CollectionAssert.AreEqual(
            [| ToolSideEffectPhase.IntentRecorded
               ToolSideEffectPhase.Succeeded
               ToolSideEffectPhase.IntentRecorded
               ToolSideEffectPhase.Succeeded
               ToolSideEffectPhase.CompensationSucceeded
               ToolSideEffectPhase.CompensationSucceeded |],
            transitions |> List.map _.Phase |> List.toArray
        )

        Assert.IsTrue(
            transitions
            |> List.forall (fun transition -> transition.Correlation = result.Correlation)
        )

        CollectionAssert.AreEqual(
            history |> List.map _.IdempotencyKey |> List.toArray,
            transitions
            |> List.filter (fun transition -> transition.Phase = ToolSideEffectPhase.CompensationSucceeded)
            |> List.map _.IdempotencyKey
            |> List.toArray
        )

    [<TestMethod>]
    member _.FailedCompensationRemainsDurableAndRevertible() =
        let operation =
            ToolOperation.create ToolReplaySafety.ExternallySideEffecting (fun _ input ->
                Task.FromResult(Ok(sprintf "created:%s" input)))
            |> ToolOperation.withRevert (fun _ -> Task.FromResult(Error "cannot undo"))

        let tool =
            Tool.create "create" "Creates a reversible value" 0 [] ToolCodec.text ToolCodec.text operation

        let protocol = ToolProtocol.fromTools [ tool ]

        let agent =
            Agent.create
                "failed-compensation-agent"
                "Failed compensation agent"
                "Fails after a reversible tool succeeds"
                0
                []
                AgentContract.Text
                (fun context input ->
                    task {
                        let! _ = protocol.InvokeAsync context (context.NextToolCallKey()) tool.Name input
                        return raise (InvalidOperationException "fail after tool")
                    })

        let journal = InMemoryExecutionJournal.create ()
        let context = AgentContext.unrestrictedForTests ()
        let executionRequest = request context agent "value"

        let config =
            { EtclovgConfig.Default with
                ExecutionJournal = Some journal }

        let result = (EtclovgHarness.runAsync config context agent executionRequest).Result
        let history = journal.GetHistoryAsync() |> _.Result

        let transitions =
            journal.SideEffects.GetByExecution result.Correlation.ExecutionId |> _.Result

        Assert.AreEqual(1, history.Length)
        Assert.IsFalse(history.Head.Reverted)
        Assert.AreEqual([ history.Head ], journal.GetRevertibleAsync() |> _.Result)
        Assert.AreEqual(ToolSideEffectPhase.CompensationFailed, transitions |> List.last |> _.Phase)
        Assert.AreEqual(Some "cannot undo", transitions |> List.last |> _.Detail)

    [<TestMethod>]
    member _.NetworkFailureQuarantinesAmbiguousExternalOutcome() =
        let mutable toolCalls = 0

        let tool =
            Tool.create
                "remote-write"
                "Writes to a remote dependency"
                0
                []
                ToolCodec.text
                ToolCodec.text
                (ToolOperation.create ToolReplaySafety.ExternallySideEffecting (fun _ _ ->
                    toolCalls <- toolCalls + 1
                    raise (Net.Http.HttpRequestException "connection lost after send")))

        let protocol = ToolProtocol.fromTools [ tool ]

        let agent =
            Agent.create
                "ambiguous-network-agent"
                "Ambiguous network agent"
                "Propagates an ambiguous remote write failure"
                0
                []
                AgentContract.Text
                (fun context input ->
                    task {
                        let! invocation = protocol.InvokeAsync context (context.NextToolCallKey()) tool.Name input

                        if invocation.Success then
                            return invocation.Output
                        else
                            return "agent attempted recovery"
                    })

        let journal = InMemoryExecutionJournal.create ()
        let context = AgentContext.unrestrictedForTests ()
        let executionRequest = request context agent "value"

        let config =
            { EtclovgConfig.Default with
                ExecutionJournal = Some journal }

        let result = (EtclovgHarness.runAsync config context agent executionRequest).Result

        let transitions =
            journal.SideEffects.GetByExecution result.Correlation.ExecutionId |> _.Result

        Assert.AreEqual(1, toolCalls)

        match result.Status with
        | ExecutionTerminalStatus.Indeterminate message -> StringAssert.Contains(message, "quarantined")
        | status -> Assert.Fail(sprintf "Expected indeterminate execution, got %A." status)

        CollectionAssert.AreEqual(
            [| ToolSideEffectPhase.IntentRecorded
               ToolSideEffectPhase.Failed
               ToolSideEffectPhase.Quarantined |],
            transitions |> List.map _.Phase |> List.toArray
        )

        Assert.IsTrue((journal.GetRevertibleAsync() |> _.Result).IsEmpty)

    [<TestMethod>]
    member _.HarnessDeadlineBoundsProviderCall() =
        let provider =
            LlmProvider.create (fun () -> "slow-provider") (fun _ _ _ ->
                task {
                    do! Task.Delay(TimeSpan.FromSeconds 5.0)
                    return CompletionResult.create "late" "stop" None None
                })

        let orchestrator =
            Orchestrator.create
                { Id = "provider-timeout"
                  Name = "Provider timeout"
                  Description = "Tests provider deadline enforcement"
                  Priority = 0
                  Responsibilities = []
                  Contract = AgentContract.Text
                  Provider = provider
                  Tools = []
                  SubAgents = []
                  Prompt = Prompt.Empty
                  Options = CompletionOptions.Default
                  MaxRounds = 1
                  Bus = EventBus.none
                  Scope = EventScope.CreateEmpty() }
                { OrchestratorDefinition.create Task.FromResult with
                    ParseActions = fun response -> [ Respond response ] }

        let context = AgentContext.unrestrictedForTests ()

        let executionRequest =
            { request context orchestrator "input" with
                Sandbox =
                    { SandboxConfig.Default with
                        Limits =
                            { ResourceLimits.Unlimited with
                                MaxDuration = TimeSpan.FromMilliseconds 25.0 } } }

        let result =
            (EtclovgHarness.runAsync EtclovgConfig.Default context orchestrator executionRequest).Result

        Assert.AreEqual(ExecutionTerminalStatus.TimedOut, result.Status)

    [<DataTestMethod>]
    [<DataRow("router-supervisor")>]
    [<DataRow("router-selected")>]
    member _.RouterAgentsReenterHarnessPolicy(deniedAgentId: string) =
        let mutable supervisorExecuted = false
        let mutable selectedExecuted = false

        let supervisor =
            Agent.create "router-supervisor" "Router supervisor" "Selects an agent" 0 [] AgentContract.Text (fun _ _ ->
                supervisorExecuted <- true
                Task.FromResult "selected")

        let selected =
            Agent.create "router-selected" "selected" "Handles the request" 0 [] AgentContract.Text (fun _ _ ->
                selectedExecuted <- true
                Task.FromResult "done")

        let router = Router.create [ selected ] (ByPrompt supervisor)

        let routerAgent =
            Agent.create "router" "Router" "Routes requests" 0 [] AgentContract.Text (fun context input ->
                Router.routeAsync context input router)

        let denyAgent =
            { Id = "deny-router-agent"
              Description = "Blocks one router agent"
              Enforcement = PolicyEnforcement.Block
              Evaluate =
                fun policyContext ->
                    if policyContext.AgentId = deniedAgentId then
                        Some "router agent denied"
                    else
                        None }

        let config =
            { EtclovgConfig.Default with
                PolicyEngine = Some(PolicyEngine.create [ denyAgent ]) }

        let context = AgentContext.unrestrictedForTests ()

        let result =
            (EtclovgHarness.runAsync config context routerAgent (request context routerAgent "input")).Result

        Assert.AreEqual(ExecutionTerminalStatus.Denied HarnessError.PermissionDenied, result.Status)

        if deniedAgentId = supervisor.Metadata.Id then
            Assert.IsFalse(supervisorExecuted)
            Assert.IsFalse(selectedExecuted)
        else
            Assert.IsTrue(supervisorExecuted)
            Assert.IsFalse(selectedExecuted)

    [<TestMethod>]
    member _.ReadinessCheckFailureBlocksExecution() =
        let agent = makeAgent "response"

        let failCheck =
            ReadinessCheck.create "prereq" (fun _ _ ->
                Task.FromResult(ReadinessResult.NotReady [ "missing dependency" ]))

        let config =
            { EtclovgConfig.Default with
                ReadinessChecks = [ failCheck ] }

        let context = AgentContext.unrestrictedForTests ()

        let result =
            (EtclovgHarness.runAsync config context agent (request context agent "test")).Result

        Assert.AreEqual(ExecutionTerminalStatus.Failed(HarnessError.NotReady [ "missing dependency" ]), result.Status)

    [<TestMethod>]
    member _.LifecycleHookCanBlockInit() =
        let agent = makeAgent "response"

        let blockHook =
            { LifecycleHook.passthrough with
                OnBeforeInit = fun _ -> Task.FromResult(Error "init blocked") }

        let config =
            { EtclovgConfig.Default with
                Lifecycle = [ blockHook ] }

        let context = AgentContext.unrestrictedForTests ()

        let result =
            (EtclovgHarness.runAsync config context agent (request context agent "test")).Result

        Assert.AreEqual(ExecutionTerminalStatus.Failed(HarnessError.InitializationFailed "init blocked"), result.Status)

    [<TestMethod>]
    member _.ConstitutionViolationBlocksOutput() =
        let agent = makeAgent "contact user@evil.com for info"

        let constitution =
            Constitution.empty "safety"
            |> Constitution.addRule Constitution.noPrivateDataRule

        let config =
            { EtclovgConfig.Default with
                Constitution = Some constitution }

        let context = AgentContext.unrestrictedForTests ()

        let result =
            (EtclovgHarness.runAsync config context agent (request context agent "test")).Result

        match result.Status with
        | ExecutionTerminalStatus.Denied(HarnessError.ConstitutionViolation _) -> ()
        | status -> Assert.Fail(sprintf "Expected constitution denial, got %A" status)

        Assert.IsTrue(result.PolicyDecisions.ConstitutionViolations.Length > 0)

    [<TestMethod>]
    member _.DeterministicAgentDoesNotRecordLlmCall() =
        let agent = makeAgent "done"
        let metrics = InMemory.metrics ()

        let config =
            { EtclovgConfig.Default with
                Metrics = Some metrics }

        let context =
            { (AgentContext.unrestrictedForTests ()) with
                SessionKey = "metrics/session" }

        let result =
            (EtclovgHarness.runAsync config context agent (request context agent "test")).Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        Assert.IsTrue(result.Evidence.Metrics.IsSome)
        Assert.AreEqual(0, result.Evidence.Metrics.Value.TotalLlmCalls)

    [<TestMethod>]
    member _.TraceStoredAfterExecution() =
        let agent = makeAgent "answer"
        let store = InMemoryTraceStore.create ()

        let config =
            { EtclovgConfig.Default with
                TraceStore = Some store }

        let context = AgentContext.unrestrictedForTests ()

        let result =
            (EtclovgHarness.runAsync config context agent (request context agent "question")).Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
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

        let context = AgentContext.unrestrictedForTests ()

        let result =
            (EtclovgHarness.runAsync config context agent (request context agent "test")).Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        Assert.AreEqual(1, result.Evidence.AuditEntries)
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
            { (AgentContext.unrestrictedForTests ()) with
                SessionKey = "metrics/session" }

        let result =
            (EtclovgHarness.runAsync config context agent (request context agent "hello")).Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        Assert.AreEqual(Some "safe response", result.Outputs.Response)
        Assert.IsTrue(result.Evidence.Metrics.IsSome)
        Assert.IsTrue(result.Evidence.Trace.IsSome)
        Assert.AreEqual(1, result.Evidence.AuditEntries)

    [<TestMethod>]
    member _.AgentIdentityMismatchFailsBeforeExecution() =
        let mutable executed = false

        let agent =
            Agent.create "actual-agent" "actual-agent" "test" 0 [] AgentContract.Text (fun _ _ ->
                executed <- true
                Task.FromResult "unexpected")

        let context = AgentContext.unrestrictedForTests ()

        let mismatched =
            { request context agent "test" with
                AgentId = "requested-agent" }

        let result =
            (EtclovgHarness.runAsync EtclovgConfig.Default context agent mismatched).Result

        Assert.AreEqual(ExecutionTerminalStatus.Denied HarnessError.PermissionDenied, result.Status)
        Assert.IsFalse(executed)
