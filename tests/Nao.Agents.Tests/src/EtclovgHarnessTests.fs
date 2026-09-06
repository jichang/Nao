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
                    let artifact = Artifact.create "report" "application/json" "{\"value\":42}"
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
                (ToolOperation.create (fun _ input ->
                    executedInput <- input
                    Task.FromResult(Ok input)))

        let protocol = ToolProtocol.fromTools [ tool ]

        let agent =
            Agent.create "tool-policy-agent" "tool-policy-agent" "test" 0 [] AgentContract.Text (fun context input ->
                task {
                    let! result = protocol.InvokeAsync context tool.Name input
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
                (ToolOperation.create (fun _ input ->
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
                        let! result = protocol.InvokeAsync context tool.Name input

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
                (ToolOperation.create (fun context _ ->
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
                    let! result = protocol.InvokeAsync context tool.Name input
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
                (ToolOperation.create (fun _ input ->
                    toolExecutions <- toolExecutions + 1
                    Task.FromResult(Ok input)))

        let protocol = ToolProtocol.fromTools [ tool ]

        let invokeTool context input =
            task {
                let! result = protocol.InvokeAsync context tool.Name input

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
