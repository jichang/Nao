namespace Nao.Runtime.Orleans.Tests

open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

type private EchoTool() =
    inherit TypedTool<string, string>("echo", "Echoes input.", [], ToolParameter.text, ToolParameter.text)

    override _.ExecuteAsync(_, input) = Task.FromResult(Ok input)

type private ScriptedProvider(responses: string list) =
    let responses = Queue<string>(responses)

    interface ILlmProvider with
        member _.Name = "scripted"

        member _.CompleteAsync _ _ =
            Task.FromResult(CompletionResult.create (responses.Dequeue()) "stop" None None)

type private TestOrchestrator(config: OrchestratorConfig, parse: string -> AgentAction list) =
    inherit OrchestratorBase(config)

    override _.GenerateReasoningPrompt(conversation) = Task.FromResult conversation
    override _.ParseActions(response) = parse response

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
        let provider = ScriptedProvider([ "invoke"; "done" ]) :> ILlmProvider
        let tool = EchoTool() :> ITool
        let agent =
            TestOrchestrator(
                config provider [ tool ] EventBus.none,
                function
                | "invoke" -> [ InvokeTool("echo", "hello") ]
                | response -> [ Respond response ])
            :> IAgent
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
    member _.``agent-backed tool inherits active event context``() =
        let events = ResizeArray<NaoEvent>()
        let bus = InMemory.eventBus ()
        let consumer =
            { new IEventConsumer with
                member _.HandleAsync event =
                    events.Add event
                    Task.CompletedTask }
        bus.Subscribe consumer

        let childProvider = ScriptedProvider([ "child done" ]) :> ILlmProvider
        let child = TestOrchestrator(config childProvider [] EventBus.none, fun response -> [ Respond response ])
        let childTool = AgentTool.create "memory" "Memory specialist." 1000 "string" "string" child
        let parentProvider = ScriptedProvider([ "invoke"; "parent done" ]) :> ILlmProvider
        let parent =
            TestOrchestrator(
                config parentProvider [ childTool ] bus,
                function
                | "invoke" -> [ InvokeTool("memory", "recall") ]
                | response -> [ Respond response ])
            :> IAgent
        let tracer = InMemory.tracer ()
        let harnessConfig = { EtclovgConfig.Default with Tracer = Some tracer }

        let result = EtclovgHarness.runAsync harnessConfig AgentContext.allowAll parent "start" |> _.Result

        Assert.IsTrue(result.Success)
        Assert.IsTrue(child.TraceContext.IsSome)
        Assert.IsTrue(
            events
            |> Seq.exists (function
                | LlmExchangeRecorded(eventScope, exchange) -> eventScope.ActionId = scope.ActionId && exchange.Response = "child done"
                | _ -> false))