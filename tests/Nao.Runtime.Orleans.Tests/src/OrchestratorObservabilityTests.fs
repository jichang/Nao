namespace Nao.Runtime.Orleans.Tests

open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

module private TestTools =
    let echo =
        Tool.create
            "echo"
            "Echoes input."
            0
            []
            ToolCodec.text
            ToolCodec.text
            (ToolOperation.create (fun _ input -> Task.FromResult(Ok input)))

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

    let request (context: AgentContext) (agent: Agent) input =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse scope.UserId) []

        let authorization =
            AuthorizationScope.tryCreate
                principal
                None
                (WorkspaceId.parse scope.WorkspaceKey)
                (Some(SessionId.parse scope.SessionId))
            |> Option.get

        ExecutionRequest.create
            authorization
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

        let context = AgentContext.allowAll ()
        let result = Agent.runAsync context "start" agent |> _.Result

        Assert.AreEqual("done", result)
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
            { (AgentContext.allowAll ()) with
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

        let history = journal.GetHistoryAsync() |> _.Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        let aggregate = metrics.GetMetrics context.SessionKey
        Assert.AreEqual(1, aggregate.TotalToolCalls)
        let executionMetrics = metrics.GetByExecution context.Correlation.ExecutionId
        Assert.AreEqual(aggregate.TotalLlmCalls + aggregate.TotalToolCalls, executionMetrics.Length)

        Assert.IsTrue(
            executionMetrics
            |> List.forall (fun metric -> metric.Correlation = context.Correlation)
        )

        let executionSpans = tracer.GetByExecution context.Correlation.ExecutionId
        Assert.IsTrue(executionSpans.Length >= 2)

        Assert.IsTrue(
            executionSpans
            |> List.forall (fun span -> span.Correlation = context.Correlation)
        )

        Assert.AreEqual(1, history.Length)
        Assert.AreEqual("echo", history.Head.ToolName)
        Assert.AreEqual("hello", history.Head.Input)
        Assert.AreEqual("hello", history.Head.Output)
        Assert.AreEqual("user/session", history.Head.Owner)
        Assert.AreEqual(context.Correlation, history.Head.Correlation)

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

        let context = AgentContext.allowAll ()

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
            TestOrchestrator.create (config childProvider [] bus) (fun response -> [ Respond response ])

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
                Tracer = Some tracer }

        let context = AgentContext.allowAll ()

        let result =
            EtclovgHarness.runAsync harnessConfig context parent (request context parent "start")
            |> _.Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)

        Assert.IsTrue(
            events
            |> Seq.exists (function
                | LlmExchangeRecorded(eventScope, exchange) ->
                    eventScope.ActionId = scope.ActionId && exchange.Response = "child done"
                | _ -> false)
        )
