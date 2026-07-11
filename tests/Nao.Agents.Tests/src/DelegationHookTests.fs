namespace Nao.Agents.Tests

open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Assistant

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
                { Id = { Name = "orchestrator"; Description = "test orchestrator" }; Provider = provider; Tools = []; SubAgents = subAgents; Prompt = Prompt.Empty; Options = CompletionOptions.Default; MaxRounds = 5; Bus = EventBus.none; Scope = EventScope.Empty; Memory = OrchestratorMemoryConfig.None; Instructions = None; Context = ToolContext.allowAll }

    let withContext (context: ToolContext) (config: OrchestratorConfig) =
        { config with Context = context }

    let delegateJson (agent: string) (input: string) =
        sprintf "{\"action\":\"delegate\",\"name\":\"%s\",\"input\":\"%s\"}" agent input

    let respondJson (response: string) =
        sprintf "{\"type\":\"respond\",\"response\":\"%s\"}" response

    [<TestMethod>]
    member _.HandledDelegationReturnsTokenWithoutRunningSubAgent() =
        let invoked = ref false
        let agent = makeAgent "converter" "converted output" invoked
        let provider = scriptedProvider [ delegateJson "converter" "convert notes.md" ]
        let config = makeConfig provider [ agent ]
        let orchestrator =
            { new NaoOrchestrator(config) with
                member _.TryHandleDelegationAsync(_agentName, _input) =
                    Task.FromResult(Some { TaskId = "task-token-123"; Kind = "agent"; Title = "converter agent" }) }
        let result = (orchestrator :> IAgent).RunAsync("convert this file").Result
        Assert.AreEqual("task-token-123", result)
        Assert.IsFalse(invoked.Value, "Sub-agent must NOT run in-process when delegation is handled")

    [<TestMethod>]
    member _.UnhandledDelegationFallsBackToInProcessSubAgent() =
        let invoked = ref false
        let agent = makeAgent "converter" "converted output" invoked
        // Round 1: delegate. The orchestrator returns the specialist output directly.
        let provider = scriptedProvider [ delegateJson "converter" "convert notes.md"; "all done" ]
        let config = makeConfig provider [ agent ]
        // Default OrchestratorBase returns None from TryHandleDelegationAsync.
        let orchestrator = NaoOrchestrator(config)
        let result = (orchestrator :> IAgent).RunAsync("convert this file").Result
        Assert.AreEqual("converted output", result)
        Assert.IsTrue(invoked.Value, "Sub-agent should run in-process when delegation is not handled")

    [<TestMethod>]
    member _.PlainPlannerOutputRemainsFinalAnswer() =
        let invoked = ref false
        let agent = makeAgent "application_agent" "should not run" invoked
        let provider = scriptedProvider [ "I can answer this directly." ]
        let config = makeConfig provider [ agent ]
        let result = (NaoOrchestrator(config) :> IAgent).RunAsync("what can you do?").Result
        Assert.AreEqual("I can answer this directly.", result)
        Assert.IsFalse(invoked.Value, "Plain planner output should remain the final answer")

    [<TestMethod>]
    member _.RespondActionReturnsEncodedResponseWhenNoFallbackAgentIsConfigured() =
        let provider = scriptedProvider [ respondJson "specialist answer" ]
        let config = makeConfig provider []
        let result = (NaoOrchestrator(config) :> IAgent).RunAsync("say hello").Result
        Assert.AreEqual("specialist answer", result)

    [<TestMethod>]
    member _.RespondActionReturnsItsPayloadWhenOtherAgentsAreConfigured() =
        let invoked = ref false
        let agent = makeAgent "application_agent" "should not run" invoked
        let provider = scriptedProvider [ respondJson "router answer" ]
        let config = makeConfig provider [ agent ]
        let result = (NaoOrchestrator(config) :> IAgent).RunAsync("say hello").Result
        Assert.AreEqual("router answer", result)
        Assert.IsFalse(invoked.Value, "Respond actions should remain in the orchestrator")

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
                    Task.FromResult(Some { SessionExecution.BackgroundTaskHandle.TaskId = "task-123"; Kind = "agent"; Title = "converter agent" }) }
        let config = makeConfig provider [ agent ] |> withContext context
        let orchestrator = NaoOrchestrator(config)
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
                    Task.FromResult(Some { SessionExecution.BackgroundTaskHandle.TaskId = "task-123"; Kind = "agent"; Title = "converter agent" }) }
        let config = makeConfig provider [] |> withContext context
        let orchestrator = NaoOrchestrator(config)
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
                    Task.FromResult(Some { TaskId = "task-123"; Kind = "agent"; Title = "orchestrator agent" }) }
        let config = makeConfig provider [ selfAgent ] |> withContext context
        let orchestrator = NaoOrchestrator(config)
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
        let prompt = NaoOrchestrator(config).BuildSystemPrompt()
        StringAssert.Contains(prompt, "Available Agents")
        StringAssert.Contains(prompt, "\"delegate\",\"name\":\"converter\"")
        Assert.IsFalse(prompt.Contains("\"name\":\"convert_document\""), "Prompt must not show convert_document examples unless the tool is available")
