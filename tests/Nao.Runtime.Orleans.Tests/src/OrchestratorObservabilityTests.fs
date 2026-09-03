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
        LlmProvider.create (fun () -> "scripted") (fun _ _ -> Task.FromResult(CompletionResult.create (responses.Dequeue()) "stop" None None))

module private TestOrchestrator =
    let create config parse =
        let definition =
            { OrchestratorDefinition.create Task.FromResult with
                ParseActions = parse }
        Orchestrator.create config definition

[<TestClass>]
type OrchestratorObservabilityTests() =
    let scope = EventScope.Create("user", "session", "conversation", "default", "turn", "user/session")

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

    [<TestMethod>]
    member _.``harness records tool metrics and successful execution``() =
        let provider = ScriptedProvider.create [ "invoke"; "done" ]
        let tool = TestTools.echo
        let agent =
            TestOrchestrator.create
                (config provider [ tool ] EventBus.none)
                (function
                | "invoke" -> [ InvokeTool("echo", "hello") ]
                | response -> [ Respond response ])
        let metrics = InMemory.metrics ()
        let journal = InMemory.executionJournal ()
        let harnessConfig =
            { EtclovgConfig.Default with
                Metrics = Some metrics
                ExecutionJournal = Some journal }

        let result = EtclovgHarness.runAsync harnessConfig AgentContext.allowAll agent "start" |> _.Result
        let history = journal.GetHistoryAsync() |> _.Result

        Assert.IsTrue(result.Success)
        Assert.AreEqual(1, metrics.GetMetrics().TotalToolCalls)
        Assert.AreEqual(1, history.Length)
        Assert.AreEqual("echo", history.Head.ToolName)
        Assert.AreEqual("hello", history.Head.Input)
        Assert.AreEqual("hello", history.Head.Output)

    [<TestMethod>]
    member _.``orchestrator executes tools through injected protocol middleware``() =
        let provider = ScriptedProvider.create [ "invoke"; "done" ]
        let mutable beforeCalls = 0
        let middleware =
            { BeforeExecute = fun _ _ ->
                  beforeCalls <- beforeCalls + 1
                  Task.FromResult(Error "blocked by middleware")
              AfterExecute = fun _ result -> Task.FromResult result }
        let protocol =
            ToolProtocol.fromTools [ TestTools.echo ]
            |> ToolProtocol.withMiddleware middleware
        let definition =
            { OrchestratorDefinition.create Task.FromResult with
                ParseActions = function
                    | "invoke" -> [ InvokeTool("echo", "hello") ]
                    | response -> [ Respond response ] }
        let agent =
            Orchestrator.createWithProtocol
                protocol
                (config provider [ TestTools.echo ] EventBus.none)
                definition

        let result = EtclovgHarness.runAsync EtclovgConfig.Default AgentContext.allowAll agent "start" |> _.Result

        Assert.IsTrue(result.Success)
        Assert.AreEqual(Some "done", result.Response)
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
        let child = TestOrchestrator.create (config childProvider [] bus) (fun response -> [ Respond response ])
        let childTool = AgentTool.create "memory" "Memory specialist." 1000 "string" "string" child
        let parentProvider = ScriptedProvider.create [ "invoke"; "parent done" ]
        let parent =
            TestOrchestrator.create
                (config parentProvider [ childTool ] bus)
                (function
                | "invoke" -> [ InvokeTool("memory", "recall") ]
                | response -> [ Respond response ])
        let tracer = InMemory.tracer ()
        let harnessConfig = { EtclovgConfig.Default with Tracer = Some tracer }

        let result = EtclovgHarness.runAsync harnessConfig AgentContext.allowAll parent "start" |> _.Result

        Assert.IsTrue(result.Success)
        Assert.IsTrue(
            events
            |> Seq.exists (function
                | LlmExchangeRecorded(eventScope, exchange) -> eventScope.ActionId = scope.ActionId && exchange.Response = "child done"
                | _ -> false))