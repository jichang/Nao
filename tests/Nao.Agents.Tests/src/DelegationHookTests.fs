namespace Nao.Agents.Tests

open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Agents

/// Tests for OrchestratorBase.TryHandleDelegationAsync — the hook that lets a
/// subclass intercept delegation (e.g. hand it off to a background task) and
/// reply with a token instead of running the sub-agent in-process.
[<TestClass>]
type DelegationHookTests() =

    /// A provider that returns a fixed sequence of completions, one per round.
    let scriptedProvider (responses: string list) : ILlmProvider =
        let queue = System.Collections.Generic.Queue<string>(responses)
        { new ILlmProvider with
            member _.CompleteAsync _conversation _options =
                let content = if queue.Count > 0 then queue.Dequeue() else "done"
                Task.FromResult { Content = content; FinishReason = "stop"; TokensUsed = None }
            member _.Name = "scripted" }
    let makeAgent (name: string) (response: string) (invoked: bool ref) : IAgent =
        { new IAgent with
            member _.Id = { Name = name; Description = "test sub-agent" }
            member _.RunAsync(_input) =
                invoked.Value <- true
                Task.FromResult response
            member _.HandleMessageAsync(_msg) = Task.FromResult None }

    let makeConfig (provider: ILlmProvider) (subAgents: IAgent list) : OrchestratorConfig =
        { Provider = provider
          Tools = []
          SubAgents = subAgents
          Prompt = Prompt.Empty
          Options = CompletionOptions.Default
          MaxRounds = 5
          Bus = EventBus.none
          Scope = EventScope.Empty
          Memory = OrchestratorMemoryConfig.None
          Instructions = None
          Context = ToolContext.allowAll }

    let withContext (context: ToolContext) (config: OrchestratorConfig) =
        { config with Context = context }

    let delegateJson (agent: string) (input: string) =
        sprintf "{\"action\":\"delegate\",\"name\":\"%s\",\"input\":\"%s\"}" agent input

    [<TestMethod>]
    member _.HandledDelegationReturnsTokenWithoutRunningSubAgent() =
        let invoked = ref false
        let agent = makeAgent "converter" "converted output" invoked
        let provider = scriptedProvider [ delegateJson "converter" "convert notes.md" ]
        let config = makeConfig provider [ agent ]
        let orchestrator =
            { new OrchestratorBase(config) with
                member _.TryHandleDelegationAsync(_agentName, _input) =
                    Task.FromResult(Some "task-token-123") }
        let result = (orchestrator :> IAgent).RunAsync("convert this file").Result
        Assert.AreEqual("task-token-123", result)
        Assert.IsFalse(invoked.Value, "Sub-agent must NOT run in-process when delegation is handled")

    [<TestMethod>]
    member _.UnhandledDelegationFallsBackToInProcessSubAgent() =
        let invoked = ref false
        let agent = makeAgent "converter" "converted output" invoked
        // Round 1: delegate. Round 2: final answer once the agent result is fed back.
        let provider = scriptedProvider [ delegateJson "converter" "convert notes.md"; "all done" ]
        let config = makeConfig provider [ agent ]
        // Default OrchestratorBase returns None from TryHandleDelegationAsync.
        let orchestrator = Orchestrator(config)
        let result = (orchestrator :> IAgent).RunAsync("convert this file").Result
        Assert.AreEqual("all done", result)
        Assert.IsTrue(invoked.Value, "Sub-agent should run in-process when delegation is not handled")

    [<TestMethod>]
    member _.DefaultAsyncDelegationSpawnsTaskWithoutRunningSubAgent() =
        let invoked = ref false
        let spawned = ref false
        let agent = makeAgent "converter" "converted output" invoked
        let provider = scriptedProvider [ delegateJson "converter" "convert notes.md" ]
        let context =
            { ToolContext.allowAll with
                AsyncAgents = Set.singleton "converter"
                SpawnTask = fun spec ->
                    spawned.Value <- true
                    Assert.AreEqual("agent", spec.Kind)
                    Assert.AreEqual("converter", spec.Params.["agent"])
                    Assert.AreEqual("convert notes.md", spec.Params.["input"])
                    Task.FromResult "task-123" }
        let config = makeConfig provider [ agent ] |> withContext context
        let orchestrator = Orchestrator(config)
        let result = (orchestrator :> IAgent).RunAsync("convert this file").Result
        StringAssert.Contains(result, "task-123")
        Assert.IsTrue(spawned.Value, "Async delegation should spawn a background task")
        Assert.IsFalse(invoked.Value, "Async sub-agent should not run in-process when task spawning succeeds")

    [<TestMethod>]
    member _.AsyncAgentNameNotInSubAgentsDoesNotSpawnTask() =
        let spawned = ref false
        let provider = scriptedProvider [ delegateJson "converter" "convert notes.md" ]
        let context =
            { ToolContext.allowAll with
                AsyncAgents = Set.singleton "converter"
                SpawnTask = fun _ ->
                    spawned.Value <- true
                    Task.FromResult "task-123" }
        let config = makeConfig provider [] |> withContext context
        let orchestrator = Orchestrator(config)
        let result = (orchestrator :> IAgent).RunAsync("convert this file").Result
        Assert.AreEqual("done", result)
        Assert.IsFalse(spawned.Value, "An async agent name must not spawn unless it is configured as a sub-agent")

    [<TestMethod>]
    member _.SelfDelegationDoesNotSpawnOrInvokeSubAgent() =
        let invoked = ref false
        let spawned = ref false
        let selfAgent = makeAgent "orchestrator" "self output" invoked
        let provider = scriptedProvider [ delegateJson "orchestrator" "loop"; "done" ]
        let context =
            { ToolContext.allowAll with
                AsyncAgents = Set.singleton "orchestrator"
                SpawnTask = fun _ ->
                    spawned.Value <- true
                    Task.FromResult "task-123" }
        let config = makeConfig provider [ selfAgent ] |> withContext context
        let orchestrator = Orchestrator(config)
        let result = (orchestrator :> IAgent).RunAsync("delegate to yourself").Result
        Assert.AreEqual("done", result)
        Assert.IsFalse(spawned.Value, "Self-delegation must not spawn a task")
        Assert.IsFalse(invoked.Value, "Self-delegation must not invoke the self agent")

    [<TestMethod>]
    member _.PromptShowsOnlyCapabilitySpecificConversionExample() =
        let invoked = ref false
        let converter = makeAgent "converter" "converted output" invoked
        let provider = scriptedProvider [ "done" ]
        let config = makeConfig provider [ converter ]
        let prompt = Orchestrator(config).BuildSystemPrompt()
        StringAssert.Contains(prompt, "Available Agents")
        StringAssert.Contains(prompt, "delegate\",\"name\":\"converter")
        Assert.IsFalse(prompt.Contains("\"name\":\"convert_document\""), "Prompt must not show convert_document examples unless the tool is available")
