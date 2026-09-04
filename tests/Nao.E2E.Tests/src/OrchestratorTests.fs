namespace Nao.E2E.Tests

open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

// --- Specialized sub-agents for orchestration demos ---

/// Functional sample agents used by the orchestration demos.
module SampleAgents =
    let private runTool context input (tool: Tool) =
        task {
            match! tool.RunAsync context input with
            | Ok output -> return output
            | Error failure -> return failure.Message
        }

    let private createToolAgent id description (prepareInput: string -> string) (tool: Tool) =
        let execute context input =
            runTool context (prepareInput input) tool

        let handleMessage context (message: AgentMessage) =
            task {
                let! result = runTool context (prepareInput message.Content) tool
                return Some(AgentMessage.create id message.From result)
            }

        Agent.create id id description 0 [] AgentContract.Text execute handleMessage

    let weather () =
        createToolAgent "weather-agent" "Handles weather queries" id DemoTools.getWeather

    /// Extract a math expression from natural language input
    let private extractExpression (input: string) =
        // Try to find a pattern like "X op Y" in the input
        let parts = input.Split(' ')
        let ops = [| "+"; "-"; "*"; "/" |]
        let mutable result = input

        for i in 0 .. parts.Length - 3 do
            if ops |> Array.contains parts.[i + 1] then
                result <- sprintf "%s %s %s" parts.[i] parts.[i + 1] parts.[i + 2]

        result

    let math () =
        createToolAgent "math-agent" "Handles math calculations" extractExpression DemoTools.calculator

    let greeting () =
        createToolAgent "greeting-agent" "Handles greetings and introductions" id DemoTools.greeter

    let summarizer () =
        let summarize (input: string) =
            let summary = sprintf "Summary: %s" (input.Substring(0, min 50 input.Length))
            Task.FromResult(summary)

        Agent.create
            "summarizer"
            "summarizer"
            "Summarizes and reformats text"
            0
            []
            AgentContract.Text
            (fun _context input -> summarize input)
            (fun _context message ->
                task {
                    let! summary = summarize message.Content
                    return Some(AgentMessage.create "summarizer" message.From summary)
                })

    /// An orchestrator agent that decides which sub-agent to route to.
    let router () =
        let execute _context (input: string) =
            // The orchestrator's job: analyze input and return the name of the best sub-agent
            let agentName =
                if input.Contains("weather") || input.Contains("temperature") then
                    "weather-agent"
                elif
                    input.Contains("calculate")
                    || input.Contains("math")
                    || input.Contains("+")
                    || input.Contains("*")
                then
                    "math-agent"
                elif input.Contains("hello") || input.Contains("greet") || input.Contains("welcome") then
                    "greeting-agent"
                else
                    "weather-agent" // default fallback

            Task.FromResult(agentName)

        Agent.createContextual
            "orchestrator"
            "orchestrator"
            "Routes requests to the appropriate specialist"
            0
            []
            AgentContract.Text
            execute


// =============================================================================
// Test: Router with ByPrompt strategy (orchestrator decides which agent to use)
// =============================================================================

[<TestClass>]
type OrchestratorByPromptTests() =

    let weatherAgent = SampleAgents.weather ()
    let mathAgent = SampleAgents.math ()
    let greetingAgent = SampleAgents.greeting ()
    let orchestrator = SampleAgents.router ()

    let router =
        Router.create [ weatherAgent; mathAgent; greetingAgent ] (ByPrompt orchestrator)

    [<TestMethod>]
    member _.OrchestratorRoutesToWeatherAgent() =
        let result =
            (Router.routeAsync AgentContext.allowAll "What is the weather in Tokyo?" router).Result

        Assert.IsTrue(result.Contains("Tokyo"), sprintf "Expected Tokyo in result, got: %s" result)
        Assert.IsTrue(result.Contains("18°C"))

    [<TestMethod>]
    member _.OrchestratorRoutesToMathAgent() =
        let result =
            (Router.routeAsync AgentContext.allowAll "calculate 2 + 2" router).Result

        Assert.IsTrue(result.Contains("4"), sprintf "Expected '4', got: %s" result)

    [<TestMethod>]
    member _.OrchestratorRoutesToGreetingAgent() =
        let result =
            (Router.routeAsync AgentContext.allowAll "Please greet Alice" router).Result

        Assert.IsTrue(result.Contains("Hello"), sprintf "Expected greeting, got: %s" result)
        Assert.IsTrue(result.Contains("Alice"))


// =============================================================================
// Test: Router with Custom strategy (programmatic routing logic)
// =============================================================================

[<TestClass>]
type OrchestratorCustomRoutingTests() =

    let weatherAgent = SampleAgents.weather ()
    let mathAgent = SampleAgents.math ()
    let greetingAgent = SampleAgents.greeting ()

    /// Custom routing: keyword-based selector that returns the best agent
    let keywordRouter (input: string) (agents: Agent list) : Task<Agent> =
        let selected =
            if input.Contains("weather") then
                agents |> List.find (fun agent -> agent.Metadata.Name = "weather-agent")
            elif input.Contains("calculate") || input.Contains("math") then
                agents |> List.find (fun agent -> agent.Metadata.Name = "math-agent")
            else
                agents |> List.find (fun agent -> agent.Metadata.Name = "greeting-agent")

        Task.FromResult(selected)

    let router =
        Router.create [ weatherAgent; mathAgent; greetingAgent ] (RoutingStrategy.Custom keywordRouter)

    [<TestMethod>]
    member _.CustomRouterSelectsWeatherAgent() =
        let result =
            (Router.routeAsync AgentContext.allowAll "Tell me the weather in Paris" router).Result

        Assert.IsTrue(result.Contains("Paris"))
        Assert.IsTrue(result.Contains("18°C"))

    [<TestMethod>]
    member _.CustomRouterSelectsMathAgent() =
        let result =
            (Router.routeAsync AgentContext.allowAll "calculate 3 * 7" router).Result

        Assert.IsTrue(result.Contains("21"), sprintf "Expected '21', got: %s" result)

    [<TestMethod>]
    member _.CustomRouterFallsBackToGreeting() =
        let result = (Router.routeAsync AgentContext.allowAll "Hey there!" router).Result
        Assert.IsTrue(result.Contains("Hello"))


// =============================================================================
// Test: Router with ByName (direct dispatch)
// =============================================================================

[<TestClass>]
type OrchestratorByNameTests() =

    let weatherAgent = SampleAgents.weather ()
    let mathAgent = SampleAgents.math ()

    let router = Router.create [ weatherAgent; mathAgent ] (ByName "math-agent")

    [<TestMethod>]
    member _.ByNameRoutesDirectlyToNamedAgent() =
        let result = (Router.routeAsync AgentContext.allowAll "10 / 2" router).Result
        Assert.AreEqual("5", result)

    [<TestMethod>]
    member _.ByNameReturnsErrorForUnknownAgent() =
        let router = Router.create [ weatherAgent ] (ByName "nonexistent")
        let result = (Router.routeAsync AgentContext.allowAll "anything" router).Result
        Assert.AreEqual("No matching agent available", result)


// =============================================================================
// Test: Pipeline pattern (sequential processing through multiple agents)
// =============================================================================

[<TestClass>]
type PipelineOrchestratorTests() =

    let weatherAgent = SampleAgents.weather ()
    let summarizer = SampleAgents.summarizer ()

    [<TestMethod>]
    member _.PipelineRunsAgentsSequentially() =
        // First agent fetches weather, second agent summarizes the result
        let pipeline = Pipeline.create [ weatherAgent; summarizer ]
        let result = (Pipeline.runAsync AgentContext.allowAll "London" pipeline).Result
        // The summarizer should have received the weather output and summarized it
        Assert.IsTrue(result.Contains("Summary:"), sprintf "Expected summary, got: %s" result)
        Assert.IsTrue(result.Contains("18°C") || result.Contains("London"))

    [<TestMethod>]
    member _.PipelineSingleStagePassesThrough() =
        let pipeline = Pipeline.create [ weatherAgent ]
        let result = (Pipeline.runAsync AgentContext.allowAll "Berlin" pipeline).Result
        Assert.IsTrue(result.Contains("Berlin"))
        Assert.IsTrue(result.Contains("18°C"))


// =============================================================================
// Test: AgentGroup (collaborative multi-agent conversation)
// =============================================================================

[<TestClass>]
type AgentGroupOrchestratorTests() =

    let weatherAgent = SampleAgents.weather ()
    let mathAgent = SampleAgents.math ()

    [<TestMethod>]
    member _.GroupTerminatesAfterMaxRounds() =
        let group = AgentGroup.create [ weatherAgent; mathAgent ] (MaxRounds 2)
        let history = (AgentGroup.runAsync AgentContext.allowAll "London" group).Result
        // Should have: seed + agent replies, limited by max rounds
        Assert.IsTrue(history.Length > 1, sprintf "Expected messages, got %d" history.Length)
        Assert.IsTrue(history.Length <= 5, sprintf "Expected <= 5 messages, got %d" history.Length)

    [<TestMethod>]
    member _.GroupTerminatesOnKeyword() =
        // The weather agent always responds with "sunny", so ContentContains "sunny" should stop it
        let group = AgentGroup.create [ weatherAgent; mathAgent ] (ContentContains "sunny")
        let history = (AgentGroup.runAsync AgentContext.allowAll "London" group).Result
        let lastMessages = history |> List.map (fun m -> m.Content)

        Assert.IsTrue(
            lastMessages |> List.exists (fun c -> c.Contains("sunny")),
            sprintf "Expected 'sunny' in conversation: %A" lastMessages
        )

    [<TestMethod>]
    member _.GroupSeedMessageIsFromUser() =
        let group = AgentGroup.create [ weatherAgent ] (MaxRounds 1)
        let history = (AgentGroup.runAsync AgentContext.allowAll "test input" group).Result
        let firstMsg = history |> List.head
        Assert.AreEqual("user", firstMsg.From)
        Assert.AreEqual("test input", firstMsg.Content)


// =============================================================================
// Test: Full orchestrator pattern combining router + tools + sub-agents
// =============================================================================

[<TestClass>]
type FullOrchestratorPatternTests() =

    /// Demonstrates the complete pattern: a single entry-point agent that
    /// accepts user input, decides the routing strategy, and delegates to
    /// specialized sub-agents with their own tools.
    [<TestMethod>]
    member _.OrchestratorAcceptsInputAndDelegatesToCorrectSpecialist() =
        // Setup: specialized sub-agents
        let weatherAgent = SampleAgents.weather ()
        let mathAgent = SampleAgents.math ()
        let greetingAgent = SampleAgents.greeting ()

        // The orchestrator agent decides routing
        let orchestrator = SampleAgents.router ()

        // Router uses the orchestrator's LLM to pick the right sub-agent
        let router =
            Router.create [ weatherAgent; mathAgent; greetingAgent ] (ByPrompt orchestrator)

        // User sends different types of requests through the same entry point
        let weatherResult =
            (Router.routeAsync AgentContext.allowAll "What's the weather in NYC?" router).Result

        let mathResult =
            (Router.routeAsync AgentContext.allowAll "calculate 100 - 37" router).Result
        // Note: MathAgent passes full input to calculator; calculator matches exact expressions
        let greetResult =
            (Router.routeAsync AgentContext.allowAll "hello Bob" router).Result

        // Each request was routed to the correct specialist
        Assert.IsTrue(weatherResult.Contains("NYC"), sprintf "Weather: %s" weatherResult)
        Assert.AreEqual("63", mathResult)
        Assert.IsTrue(greetResult.Contains("Bob"), sprintf "Greet: %s" greetResult)

    [<TestMethod>]
    member _.OrchestratorThenPipelineForPostProcessing() =
        // Pattern: orchestrator routes to specialist, then result goes through a pipeline
        let weatherAgent = SampleAgents.weather ()
        let summarizer = SampleAgents.summarizer ()
        let orchestrator = SampleAgents.router ()

        let router = Router.create [ weatherAgent ] (ByPrompt orchestrator)

        // Step 1: Route to the right agent
        let rawResult =
            (Router.routeAsync AgentContext.allowAll "weather in London" router).Result

        // Step 2: Post-process through a pipeline (e.g., summarize/format)
        let pipeline = Pipeline.create [ summarizer ]

        let finalResult =
            (Pipeline.runAsync AgentContext.allowAll rawResult pipeline).Result

        Assert.IsTrue(finalResult.Contains("Summary:"))
        Assert.IsTrue(finalResult.Contains("18°C") || finalResult.Contains("sunny"))
