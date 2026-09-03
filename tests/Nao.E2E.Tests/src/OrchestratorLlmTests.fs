namespace Nao.E2E.Tests

open System
open System.Net.Http
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Assistant
open Nao.Agents
open Nao.Providers

module private TestTools =
    let text name description execute =
        Tool.create
            name
            description
            0
            []
            ToolCodec.text
            ToolCodec.text
            (ToolOperation.create (fun _ input -> task {
                let! output = execute input
                return Ok output }))

/// Helper to check if a local LLM is available
module LocalLlm =
    let endpoint =
        Environment.GetEnvironmentVariable("NAO_LLM_ENDPOINT")
        |> Option.ofObj
        |> Option.defaultValue "http://localhost:11434"

    let model =
        Environment.GetEnvironmentVariable("NAO_LLM_MODEL")
        |> Option.ofObj
        |> Option.defaultValue "qwen2.5:3b"

    let isAvailable () =
        try
            use client = new HttpClient()
            let response = client.GetAsync(sprintf "%s/api/tags" endpoint).Result
            if not response.IsSuccessStatusCode then false
            else
                let body = response.Content.ReadAsStringAsync().Result
                body.Contains(model)
        with _ -> false

    let createProvider () =
        let config = { OllamaConfig.Default with BaseUrl = endpoint; Model = model } in
        OllamaProvider.create config


/// E2E tests using a real local LLM (Ollama) with the Orchestrator.
/// These tests are skipped if Ollama is not running.
/// Run `scripts/start-local-llm.sh` to set up the local LLM before running these tests.
[<TestClass>]
type OrchestratorWithLocalLlmTests() =

    static let mutable skipTests = not (LocalLlm.isAvailable())

    let shouldSkip () =
        if skipTests then Assert.Inconclusive("Local LLM (Ollama) not available. Run scripts/start-local-llm.sh first.")

    let tools = [
        TestTools.text "get_weather" "Get the current weather for a city. Input: city name."
            (fun city -> Task.FromResult(sprintf """{"city":"%s","temp_c":22,"condition":"partly cloudy","humidity":65}""" city))

        TestTools.text "calculate" "Evaluate a math expression. Input: a math expression like '2 + 2' or '15 * 3'."
            (fun expr ->
                let result =
                    if expr.Contains("2 + 2") then "4"
                    elif expr.Contains("15 * 3") then "45"
                    elif expr.Contains("100 / 4") then "25"
                    elif expr.Contains("7 * 8") then "56"
                    else sprintf "Result of %s = (computed)" expr
                Task.FromResult(result))

        TestTools.text "lookup_capital" "Look up the capital city of a country. Input: country name."
            (fun country ->
                let capital =
                    match country.Trim().ToLower() with
                    | "france" -> "Paris"
                    | "japan" -> "Tokyo"
                    | "brazil" -> "Brasilia"
                    | "australia" -> "Canberra"
                    | c -> sprintf "Unknown capital for %s" c
                Task.FromResult(capital))
    ]

    [<TestMethod>]
    member _.OrchestratorUsesToolForWeather() =
        shouldSkip ()
        let provider = LocalLlm.createProvider()
        let orchestrator = NaoOrchestrator.create provider tools []
        let result = (Agent.runAsync AgentContext.allowAll "What is the weather in Tokyo?" orchestrator).Result
        // The orchestrator should have invoked the weather tool and produced a response
        Assert.IsTrue(
            result.Contains("22") || result.Contains("Tokyo") || result.Contains("cloudy"),
            sprintf "Expected weather info in response, got: %s" result)

    [<TestMethod>]
    member _.OrchestratorUsesToolForMath() =
        shouldSkip ()
        let provider = LocalLlm.createProvider()
        let orchestrator = NaoOrchestrator.create provider tools []
        let result = (Agent.runAsync AgentContext.allowAll "What is 15 * 3?" orchestrator).Result
        Assert.IsTrue(
            result.Contains("45"),
            sprintf "Expected '45' in response, got: %s" result)

    [<TestMethod>]
    member _.OrchestratorUsesToolForLookup() =
        shouldSkip ()
        let provider = LocalLlm.createProvider()
        let orchestrator = NaoOrchestrator.create provider tools []
        let result = (Agent.runAsync AgentContext.allowAll "What is the capital of France?" orchestrator).Result
        Assert.IsTrue(
            result.Contains("Paris"),
            sprintf "Expected 'Paris' in response, got: %s" result)

    [<TestMethod>]
    member _.OrchestratorAnswersDirectlyWhenNoToolNeeded() =
        shouldSkip ()
        let provider = LocalLlm.createProvider()
        let orchestrator = NaoOrchestrator.create provider tools []
        let result = (Agent.runAsync AgentContext.allowAll "Say hello" orchestrator).Result
        // Should respond without invoking any tool
        Assert.IsTrue(result.Length > 0, "Expected non-empty response")
        Assert.IsFalse(
            result.Contains("{\"action\""),
            sprintf "Expected natural response, not JSON action: %s" result)

    [<TestMethod>]
    member _.OrchestratorDelegatesToSubAgent() =
        shouldSkip ()
        let provider = LocalLlm.createProvider()

        // Create a specialist sub-agent
        let specialist =
            Agent.create
                "poetry-agent"
                "poetry-agent"
                "Writes short poems on any topic"
                0
                []
                AgentContract.Text
                (fun _context input ->
                    Task.FromResult(sprintf "Roses are red, violets are blue, %s is great, and so are you." input)
                )
                (fun _context _message -> Task.FromResult None)

        let orchestrator = NaoOrchestrator.create provider tools [ specialist ]
        let result = (Agent.runAsync AgentContext.allowAll "Write me a poem about coding" orchestrator).Result
        // The LLM may or may not delegate; if it does, the poem agent output will be in the result
        // Either way, we should get a non-empty response
        Assert.IsTrue(result.Length > 0, "Expected non-empty response")


/// Tests that the Orchestrator works correctly with the mock provider
/// (these always run, no Ollama needed)
[<TestClass>]
type OrchestratorWithMockProviderTests() =

    /// A mock provider that simulates orchestrator-style tool calls
    let mockProvider =
        LlmProvider.create
            (fun () -> "MockOrchestrator")
            (fun (conversation: Conversation) (_options: CompletionOptions) ->
                let lastMsg =
                    conversation
                    |> List.tryFindBack (fun m -> m.Role = User)
                    |> Option.map (fun m -> m.Content)
                    |> Option.defaultValue ""

                let response =
                    if lastMsg.Contains("[Tool Result") || lastMsg.Contains("[Agent Result") then
                        // After receiving a tool/agent result, produce the final answer
                        let result = lastMsg.Split("]:") |> Array.last |> fun s -> s.Trim()
                        sprintf "Based on the information I found: %s" result
                    elif lastMsg.Contains("weather") then
                        """{"action":"tool","name":"get_weather","input":"London"}"""
                    elif lastMsg.Contains("capital") then
                        """{"action":"tool","name":"lookup_capital","input":"france"}"""
                    elif lastMsg.Contains("poem") then
                        """{"action":"delegate","name":"poetry-agent","input":"coding"}"""
                    else
                        "I can help you with that directly."

                Task.FromResult(CompletionResult.create response "stop" (Some 10) None))

    let tools = [
        TestTools.text "get_weather" "Get weather for a city"
            (fun city -> Task.FromResult(sprintf "Sunny, 20°C in %s" city))
        TestTools.text "lookup_capital" "Look up capital of a country"
            (fun country -> Task.FromResult(sprintf "The capital of %s is Paris" country))
    ]

    let poetryAgent =
        Agent.create
            "poetry-agent"
            "poetry-agent"
            "Writes poems"
            0
            []
            AgentContract.Text
            (fun _context input ->
                Task.FromResult(sprintf "A poem about %s: roses are red..." input)
            )
            (fun _context _message -> Task.FromResult None)

    [<TestMethod>]
    member _.OrchestratorInvokesToolAndReturnsResult() =
        let orchestrator = NaoOrchestrator.create mockProvider tools [ poetryAgent ]
        let result = (Agent.runAsync AgentContext.allowAll "What is the weather in London?" orchestrator).Result
        Assert.IsTrue(result.Contains("Sunny") || result.Contains("20°C"), sprintf "Got: %s" result)

    [<TestMethod>]
    member _.OrchestratorDelegatesAndReturnsAgentResult() =
        let orchestrator = NaoOrchestrator.create mockProvider tools [ poetryAgent ]
        let result = (Agent.runAsync AgentContext.allowAll "Write a poem about trees" orchestrator).Result
        Assert.IsTrue(result.Contains("poem") || result.Contains("roses"), sprintf "Got: %s" result)

    [<TestMethod>]
    member _.OrchestratorRespondsDirectlyWhenAppropriate() =
        let orchestrator = NaoOrchestrator.create mockProvider tools [ poetryAgent ]
        let result = (Agent.runAsync AgentContext.allowAll "Hello there" orchestrator).Result
        Assert.AreEqual("I can help you with that directly.", result)

    [<TestMethod>]
    member _.OrchestratorHandlesUnknownTool() =
        // Provider that references a tool that doesn't exist
        let badProvider =
            LlmProvider.create
                (fun () -> "Bad")
                (fun (conversation: Conversation) (_options: CompletionOptions) ->
                    let lastMsg =
                        conversation
                        |> List.tryFindBack (fun m -> m.Role = User)
                        |> Option.map (fun m -> m.Content)
                        |> Option.defaultValue ""
                    let response =
                        if lastMsg.Contains("[Error]") then
                            "Sorry, I couldn't find that tool. Let me answer directly: I don't know."
                        else
                            """{"action":"tool","name":"nonexistent","input":"test"}"""
                    Task.FromResult(CompletionResult.create response "stop" (Some 5) None))

        let orchestrator = NaoOrchestrator.create badProvider tools []
        let result = (Agent.runAsync AgentContext.allowAll "Do something" orchestrator).Result
        // Should gracefully handle the error and produce a response
        Assert.IsTrue(result.Length > 0)

    [<TestMethod>]
    member _.OrchestratorRespectsMaxRounds() =
        // Provider that always returns tool calls (infinite loop scenario)
        let loopProvider =
            LlmProvider.create
                (fun () -> "Loop")
                (fun (_conversation: Conversation) (_options: CompletionOptions) ->
                    Task.FromResult(CompletionResult.create """{"action":"tool","name":"get_weather","input":"London"}""" "stop" (Some 5) None))

        let config : OrchestratorConfig = { Id = { Name = "orchestrator"; Description = "test orchestrator" }; Provider = loopProvider; Tools = tools; SubAgents = []; Prompt = Prompt.Empty; Options = CompletionOptions.Default; MaxRounds = 3; Bus = EventBus.none; Scope = EventScope.Empty }

        let orchestrator = NaoOrchestrator.createWithConfig config
        let result = (Agent.runAsync AgentContext.allowAll "Loop me" orchestrator).Result
        // Should stop after max rounds and force a final answer
        Assert.IsTrue(result.Length > 0)
