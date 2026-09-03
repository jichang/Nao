namespace Nao.Agents.Tests

open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Assistant

/// Tests for in-process delegation and action handling in the orchestrator.
[<TestClass>]
type DelegationHookTests() =

    /// A provider that returns a fixed sequence of completions, one per round.
    let scriptedProvider (responses: string list) =
        let queue = System.Collections.Generic.Queue<string>(responses)
        LlmProvider.create
            (fun () -> "scripted")
            (fun _conversation _options ->
                let content = if queue.Count > 0 then queue.Dequeue() else "done"
                Task.FromResult(CompletionResult.create content "stop" None None))
    let makeAgent (name: string) (response: string) (invoked: bool ref) =
        Agent.create
            name
            name
            "test sub-agent"
            0
            []
            AgentContract.Text
            (fun _context _input ->
                invoked.Value <- true
                Task.FromResult response)
            (fun _context _message -> Task.FromResult None)

    let makeConfig (provider: LlmProvider) (subAgents: Agent list) : OrchestratorConfig =
                { Id = { Name = "orchestrator"; Description = "test orchestrator" }; Provider = provider; Tools = []; SubAgents = subAgents; Prompt = Prompt.Empty; Options = CompletionOptions.Default; MaxRounds = 5; Bus = EventBus.none; Scope = EventScope.Empty }

    let delegateJson (agent: string) (input: string) =
        sprintf "{\"action\":\"delegate\",\"name\":\"%s\",\"input\":\"%s\"}" agent input

    let respondJson (response: string) =
        sprintf "{\"type\":\"respond\",\"response\":\"%s\"}" response

    [<TestMethod>]
    member _.UnhandledDelegationFallsBackToInProcessSubAgent() =
        let invoked = ref false
        let agent = makeAgent "converter" "converted output" invoked
        // Round 1: delegate. The orchestrator returns the specialist output directly.
        let provider = scriptedProvider [ delegateJson "converter" "convert notes.md"; "all done" ]
        let config = makeConfig provider [ agent ]
        // Delegation runs synchronously when the configured sub-agent is available.
        let orchestrator = NaoOrchestrator(config)
        let result = (Agent.runAsync AgentContext.allowAll "convert this file" orchestrator).Result
        Assert.AreEqual("converted output", result)
        Assert.IsTrue(invoked.Value, "Sub-agent should run in-process when delegation is not handled")

    [<TestMethod>]
    member _.PlainPlannerOutputRemainsFinalAnswer() =
        let invoked = ref false
        let agent = makeAgent "application_agent" "should not run" invoked
        let provider = scriptedProvider [ "I can answer this directly." ]
        let config = makeConfig provider [ agent ]
        let result = (Agent.runAsync AgentContext.allowAll "what can you do?" (NaoOrchestrator(config))).Result
        Assert.AreEqual("I can answer this directly.", result)
        Assert.IsFalse(invoked.Value, "Plain planner output should remain the final answer")

    [<TestMethod>]
    member _.RespondActionReturnsEncodedResponseWhenNoFallbackAgentIsConfigured() =
        let provider = scriptedProvider [ respondJson "specialist answer" ]
        let config = makeConfig provider []
        let result = (Agent.runAsync AgentContext.allowAll "say hello" (NaoOrchestrator(config))).Result
        Assert.AreEqual("specialist answer", result)

    [<TestMethod>]
    member _.RespondActionReturnsItsPayloadWhenOtherAgentsAreConfigured() =
        let invoked = ref false
        let agent = makeAgent "application_agent" "should not run" invoked
        let provider = scriptedProvider [ respondJson "router answer" ]
        let config = makeConfig provider [ agent ]
        let result = (Agent.runAsync AgentContext.allowAll "say hello" (NaoOrchestrator(config))).Result
        Assert.AreEqual("router answer", result)
        Assert.IsFalse(invoked.Value, "Respond actions should remain in the orchestrator")

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
