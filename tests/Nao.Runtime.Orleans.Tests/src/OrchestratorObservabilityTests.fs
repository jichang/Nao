namespace Nao.Runtime.Orleans.Tests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence
open Nao.Runtime.Orleans

module private EtclovgHarness =
    let runAsync config context agent request =
        Nao.Agents.EtclovgHarness.runAsync config context agent request System.Threading.CancellationToken.None

module private TestTools =
    let echo =
        Tool.create
            "echo"
            "Echoes input."
            0
            []
            ToolCodec.text
            ToolCodec.text
            (ToolOperation.create ToolReplaySafety.RetrySafe (fun _ input -> Task.FromResult(Ok input)))

module private ScriptedProvider =
    let create (responses: string list) =
        let responses = Queue<string>(responses)

        LlmProvider.create (fun () -> "scripted") (fun _ _ _ ->
            Task.FromResult(CompletionResult.create (responses.Dequeue()) "stop" None None))

module private TestOrchestrator =
    let create config parse =
        let definition =
            { OrchestratorDefinition.create Task.FromResult with
                ParseActions = parse }

        Orchestrator.create config definition

[<TestClass>]
type OrchestratorObservabilityTests() =
    let scope =
        EventScope.Create(
            "user",
            "session",
            "conversation",
            "default",
            "turn",
            "user/session",
            CorrelationContext.root ()
        )

    let config provider tools bus =
        { Id = "test"
          Name = "test"
          Description = "Test orchestrator"
          Priority = 0
          Responsibilities = []
          Contract = AgentContract.Text
          Provider = provider
          Tools = tools
          SubAgents = []
          Prompt = Prompt.Empty
          Options = CompletionOptions.Default
          MaxRounds = 2
          Bus = bus
          Scope = scope }

    let authorization () =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse scope.UserId) []

        AuthorizationScope.tryCreate
            principal
            None
            (WorkspaceId.parse scope.WorkspaceKey)
            (Some(SessionId.parse scope.SessionId))
        |> Option.get

    let request (context: AgentContext) (agent: Agent) input =
        ExecutionRequest.create
            (authorization ())
            (TurnId.parse scope.ActionId)
            scope.ConversationId
            agent.Metadata.Id
            input
            SandboxConfig.Default
            Map.empty
            Map.empty
            context.Correlation

    [<TestMethod>]
    member _.``provider receives the active event correlation``() =
        let observed = ResizeArray<CorrelationContext>()

        let provider =
            LlmProvider.create (fun () -> "capturing") (fun correlation _ _ ->
                observed.Add correlation
                Task.FromResult(CompletionResult.create "done" "stop" None None))

        let agent =
            TestOrchestrator.create (config provider [] EventBus.none) (fun response -> [ Respond response ])

        let context = AgentContext.unrestrictedForTests ()

        let result =
            EtclovgHarness.runAsync EtclovgConfig.Default context agent (request context agent "start")
            |> _.Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        Assert.AreEqual(Some "done", result.Outputs.Response)
        CollectionAssert.AreEqual([| context.Correlation |], observed.ToArray())

    [<TestMethod>]
    member _.``harness records tool metrics and successful execution``() =
        let provider = ScriptedProvider.create [ "invoke"; "done" ]
        let tool = TestTools.echo

        let agent =
            TestOrchestrator.create (config provider [ tool ] EventBus.none) (function
                | "invoke" -> [ InvokeTool("echo", "hello") ]
                | response -> [ Respond response ])

        let metrics = InMemory.metrics ()
        let tracer = InMemory.tracer ()
        let journal = InMemory.executionJournal ()

        let context =
            { (AgentContext.unrestrictedForTests ()) with
                SessionKey = "user/session"
                TurnId = "turn" }

        let harnessConfig =
            { EtclovgConfig.Default with
                Metrics = Some metrics
                Tracer = Some tracer
                ExecutionJournal = Some journal }

        let result =
            EtclovgHarness.runAsync harnessConfig context agent (request context agent "start")
            |> _.Result

        let authorized = authorization ()
        let history = journal.GetHistoryAsync authorized |> _.Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        let aggregate = metrics.GetMetrics authorized
        Assert.AreEqual(1, aggregate.TotalToolCalls)

        let executionMetrics =
            metrics.GetByExecution authorized context.Correlation.ExecutionId

        Assert.AreEqual(aggregate.TotalLlmCalls + aggregate.TotalToolCalls, executionMetrics.Length)

        Assert.IsTrue(
            executionMetrics
            |> List.forall (fun metric -> metric.Correlation = context.Correlation)
        )

        let executionSpans =
            tracer.GetByExecution authorized context.Correlation.ExecutionId

        Assert.IsTrue(executionSpans.Length >= 2)

        Assert.IsTrue(
            executionSpans
            |> List.forall (fun span -> span.Correlation = context.Correlation)
        )

        Assert.AreEqual(1, history.Length)
        Assert.AreEqual("echo", history.Head.ToolName)
        Assert.AreEqual("hello", history.Head.Input)
        Assert.AreEqual("hello", history.Head.Output)
        Assert.AreEqual(context.Correlation, history.Head.Correlation)
        StringAssert.StartsWith(IdempotencyKey.value history.Head.IdempotencyKey, "tool:")
        Assert.AreEqual(ToolReplaySafety.RetrySafe, history.Head.ReplaySafety)

    [<TestMethod>]
    member _.``external tool intent is durable before execution and outcome follows``() =
        let journal = InMemory.executionJournal ()
        let mutable expectedKey = None
        let mutable intentVisibleDuringExecution = false

        let tool =
            Tool.create
                "external"
                "External side effect."
                0
                []
                ToolCodec.text
                ToolCodec.text
                (ToolOperation.create ToolReplaySafety.ExternallySideEffecting (fun _ input ->
                    task {
                        let! transitions = journal.SideEffects.GetByIdempotencyKey (authorization ()) expectedKey.Value

                        intentVisibleDuringExecution <-
                            transitions |> List.map _.Phase = [ ToolSideEffectPhase.IntentRecorded ]

                        return Ok input
                    }))

        let provider = ScriptedProvider.create [ "invoke"; "done" ]

        let agent =
            TestOrchestrator.create (config provider [ tool ] EventBus.none) (function
                | "invoke" -> [ InvokeTool("external", "payload") ]
                | response -> [ Respond response ])

        let context =
            { (AgentContext.unrestrictedForTests ()) with
                SessionKey = "user/session"
                TurnId = "turn" }

        let executionRequest = request context agent "start"
        let toolKey = IdempotencyKey.operation "tool" 1 executionRequest.IdempotencyKey
        expectedKey <- Some toolKey

        let harnessConfig =
            { EtclovgConfig.Default with
                ExecutionJournal = Some journal }

        let result =
            EtclovgHarness.runAsync harnessConfig context agent executionRequest |> _.Result

        let transitions =
            journal.SideEffects.GetByIdempotencyKey executionRequest.Authorization toolKey
            |> _.Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        Assert.IsTrue(intentVisibleDuringExecution)

        CollectionAssert.AreEqual(
            [| ToolSideEffectPhase.IntentRecorded; ToolSideEffectPhase.Succeeded |],
            transitions |> List.map _.Phase |> List.toArray
        )

    [<TestMethod>]
    member _.``external tool does not execute without durable journal``() =
        let mutable executed = false

        let tool =
            Tool.create
                "external"
                "External side effect."
                0
                []
                ToolCodec.text
                ToolCodec.text
                (ToolOperation.create ToolReplaySafety.ExternallySideEffecting (fun _ input ->
                    executed <- true
                    Task.FromResult(Ok input)))

        let provider = ScriptedProvider.create [ "invoke" ]

        let agent =
            TestOrchestrator.create (config provider [ tool ] EventBus.none) (fun _ ->
                [ InvokeTool("external", "payload") ])

        let context = AgentContext.unrestrictedForTests ()

        let result =
            EtclovgHarness.runAsync EtclovgConfig.Default context agent (request context agent "start")
            |> _.Result

        Assert.IsFalse(executed)

        match result.Status with
        | ExecutionTerminalStatus.Failed(HarnessError.ExecutionFailed message) ->
            StringAssert.Contains(message, "durable execution journal")
        | status -> Assert.Fail(sprintf "Expected fail-closed execution, got %A." status)

    [<TestMethod>]
    member _.``external tool failure persists terminal outcome``() =
        let journal = InMemory.executionJournal ()

        let tool =
            Tool.create
                "external"
                "External side effect."
                0
                []
                ToolCodec.text
                ToolCodec.text
                (ToolOperation.create ToolReplaySafety.ExternallySideEffecting (fun _ _ ->
                    Task.FromResult(Error(ToolExecError.Failed "declined"))))

        let provider = ScriptedProvider.create [ "invoke"; "done" ]

        let agent =
            TestOrchestrator.create (config provider [ tool ] EventBus.none) (function
                | "invoke" -> [ InvokeTool("external", "payload") ]
                | response -> [ Respond response ])

        let context =
            { (AgentContext.unrestrictedForTests ()) with
                SessionKey = "user/session"
                TurnId = "turn" }

        let executionRequest = request context agent "start"
        let toolKey = IdempotencyKey.operation "tool" 1 executionRequest.IdempotencyKey

        let harnessConfig =
            { EtclovgConfig.Default with
                ExecutionJournal = Some journal }

        let result =
            EtclovgHarness.runAsync harnessConfig context agent executionRequest |> _.Result

        let transitions =
            journal.SideEffects.GetByIdempotencyKey executionRequest.Authorization toolKey
            |> _.Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)

        CollectionAssert.AreEqual(
            [| ToolSideEffectPhase.IntentRecorded; ToolSideEffectPhase.Failed |],
            transitions |> List.map _.Phase |> List.toArray
        )

        Assert.AreEqual(Some "declined", transitions.[1].Detail)

    [<TestMethod>]
    member _.``orchestrator executes tools through injected protocol middleware``() =
        let provider = ScriptedProvider.create [ "invoke"; "done" ]
        let mutable beforeCalls = 0

        let middleware =
            { BeforeExecute =
                fun _ _ ->
                    beforeCalls <- beforeCalls + 1

                    Task.FromResult(
                        Error
                            { Kind = ToolFailureKind.PermissionDenied
                              Message = "blocked by middleware"
                              Retryable = false }
                    )
              AfterExecute = fun _ result -> Task.FromResult result }

        let protocol =
            ToolProtocol.fromTools [ TestTools.echo ]
            |> ToolProtocol.withMiddleware middleware

        let definition =
            { OrchestratorDefinition.create Task.FromResult with
                ParseActions =
                    function
                    | "invoke" -> [ InvokeTool("echo", "hello") ]
                    | response -> [ Respond response ] }

        let agent =
            Orchestrator.createWithProtocol protocol (config provider [ TestTools.echo ] EventBus.none) definition

        let context = AgentContext.unrestrictedForTests ()

        let result =
            EtclovgHarness.runAsync EtclovgConfig.Default context agent (request context agent "start")
            |> _.Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        Assert.AreEqual(Some "done", result.Outputs.Response)
        Assert.AreEqual(1, beforeCalls)

    [<TestMethod>]
    member _.``agent-backed tool inherits active event context``() =
        let events = ResizeArray<NaoEvent>()
        let bus = InMemory.eventBus ()

        let consumer =
            EventConsumer.create (fun event ->
                events.Add event
                Task.CompletedTask)

        EventBus.subscribe consumer bus

        let childProvider = ScriptedProvider.create [ "child done" ]

        let child =
            TestOrchestrator.create
                { config childProvider [] bus with
                    Id = "memory-agent" }
                (fun response -> [ Respond response ])

        let childTool =
            AgentTool.create "memory" "Memory specialist." 1000 "string" "string" child

        let parentProvider = ScriptedProvider.create [ "invoke"; "parent done" ]

        let parent =
            TestOrchestrator.create (config parentProvider [ childTool ] bus) (function
                | "invoke" -> [ InvokeTool("memory", "recall") ]
                | response -> [ Respond response ])

        let tracer = InMemory.tracer ()

        let harnessConfig =
            { EtclovgConfig.Default with
                Tracer = Some tracer
                ExecutionJournal = Some(InMemory.executionJournal ()) }

        let context = AgentContext.unrestrictedForTests ()

        let result =
            EtclovgHarness.runAsync harnessConfig context parent (request context parent "start")
            |> _.Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)

        Assert.IsTrue(
            events
            |> Seq.exists (function
                | LlmExchangeRecorded(eventScope, exchange) ->
                    eventScope.ActionId = scope.ActionId
                    && exchange.Result.Content = "child done"
                    && IdempotencyKey.value exchange.IdempotencyKey |> _.StartsWith("provider:")
                | _ -> false)
        )

    [<TestMethod>]
    member _.``agent-backed tool re-enters harness policy``() =
        let mutable childExecuted = false

        let child =
            Agent.create "blocked-child" "Blocked child" "Must not execute" 0 [] AgentContract.Text (fun _ _ ->
                childExecuted <- true
                Task.FromResult "unexpected")

        let childTool = AgentTool.create "child" "Blocked child." 0 "string" "string" child

        let parentProvider = ScriptedProvider.create [ "invoke"; "parent done" ]

        let parent =
            TestOrchestrator.create (config parentProvider [ childTool ] EventBus.none) (function
                | "invoke" -> [ InvokeTool("child", "run") ]
                | response -> [ Respond response ])

        let denyChild =
            { Id = "deny-child"
              Version = "test-v1"
              Description = "Blocks only the child agent"
              Scope = PolicyScope.platform
              Enforcement = PolicyEnforcement.Block
              EvaluateAsync =
                Policy.evaluateSynchronously (fun policyContext ->
                    if policyContext.AgentId = child.Metadata.Id then
                        Some "child execution denied"
                    else
                        None) }

        let harnessConfig =
            { EtclovgConfig.Default with
                PolicyEngine = Some(PolicyEngine.create (System.TimeSpan.FromSeconds 1.0) [ denyChild ])
                AuditLog = Some(InMemory.auditLog ())
                ExecutionJournal = Some(InMemory.executionJournal ()) }

        let context = AgentContext.unrestrictedForTests ()

        let result =
            EtclovgHarness.runAsync harnessConfig context parent (request context parent "start")
            |> _.Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        Assert.AreEqual(Some "parent done", result.Outputs.Response)
        Assert.IsFalse(childExecuted)

    [<TestMethod>]
    member _.``direct delegation re-enters harness policy``() =
        let mutable childExecuted = false

        let child =
            Agent.create "blocked-delegate" "Blocked delegate" "Must not execute" 0 [] AgentContract.Text (fun _ _ ->
                childExecuted <- true
                Task.FromResult "unexpected")

        let parentProvider = ScriptedProvider.create [ "delegate"; "parent done" ]

        let parentConfig =
            { config parentProvider [] EventBus.none with
                SubAgents = [ child ] }

        let parent =
            TestOrchestrator.create parentConfig (function
                | "delegate" -> [ DelegateToAgent(child.Metadata.Id, "run") ]
                | response -> [ Respond response ])

        let denyChild =
            { Id = "deny-delegate"
              Version = "test-v1"
              Description = "Blocks only the delegated agent"
              Scope = PolicyScope.platform
              Enforcement = PolicyEnforcement.Block
              EvaluateAsync =
                Policy.evaluateSynchronously (fun policyContext ->
                    if policyContext.AgentId = child.Metadata.Id then
                        Some "delegation denied"
                    else
                        None) }

        let harnessConfig =
            { EtclovgConfig.Default with
                PolicyEngine = Some(PolicyEngine.create (System.TimeSpan.FromSeconds 1.0) [ denyChild ])
                AuditLog = Some(InMemory.auditLog ()) }

        let context = AgentContext.unrestrictedForTests ()

        let result =
            EtclovgHarness.runAsync harnessConfig context parent (request context parent "start")
            |> _.Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        Assert.AreEqual(Some "parent done", result.Outputs.Response)
        Assert.IsFalse(childExecuted)

[<TestClass>]
type WorkspaceConstitutionVersionTests() =

    [<TestMethod>]
    member _.MultipleConstitutionsRequireExplicitHostComposition() =
        let definitions =
            WorkspaceDefinitions.create
                []
                []
                [ Constitution.create "platform" "v1"; Constitution.create "workspace" "v2" ]

        Assert.ThrowsExactly<ArgumentException>(fun () -> WorkspaceDefinitions.mergedConstitution definitions |> ignore)
        |> ignore
