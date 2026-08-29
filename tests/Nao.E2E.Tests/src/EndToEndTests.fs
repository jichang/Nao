namespace Nao.E2E.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Agents
open Nao.Runtime.Orleans.Grains

[<TestClass>]
type EndToEndAgentTests () =

    let provider = LocalLlmProvider() :> ILlmProvider
    let tools = [ DemoTools.getWeather; DemoTools.calculator; DemoTools.greeter ]
    let prompt =
        { Prompt.Empty with
            Role = "You are a helpful assistant with access to tools."
            Objective = "Help the user by answering questions. Use tools when needed."
            Constraints = ["Always use a tool when the user asks about weather or math."] }

    let createAgent () = DemoAgent(provider, tools, prompt) :> IAgent

    [<TestMethod>]
    member _.AgentRespondsToSimplePrompt () =
        let agent = createAgent ()
        let result = agent.RunAsync(AgentContext.allowAll, "Hello, how are you?").Result
        Assert.IsTrue(result.Contains("You said:"))
        Assert.IsTrue(result.Contains("Hello, how are you?"))

    [<TestMethod>]
    member _.AgentInvokesWeatherTool () =
        let agent = createAgent ()
        let result = agent.RunAsync(AgentContext.allowAll, "What is the weather in London?").Result
        Assert.IsTrue(result.Contains("18°C"), sprintf "Expected weather info, got: %s" result)
        Assert.IsTrue(result.Contains("sunny"))

    [<TestMethod>]
    member _.AgentInvokesCalculatorTool () =
        let agent = createAgent ()
        let result = agent.RunAsync(AgentContext.allowAll, "Please calculate 2 + 2").Result
        Assert.IsTrue(result.Contains("4"), sprintf "Expected '4', got: %s" result)

    [<TestMethod>]
    member _.AgentHandlesMessageFromAnotherAgent () =
        let agent = createAgent ()
        let sender = { Name = "coordinator"; Description = "orchestrator" }
        let msg = AgentMessage.broadcast sender "Tell me about the weather in Tokyo"
        let reply = agent.HandleMessageAsync(AgentContext.allowAll, msg).Result
        Assert.IsTrue(reply.IsSome)
        Assert.IsTrue(reply.Value.Content.Contains("18°C"))
        Assert.AreEqual("coordinator", reply.Value.To.Value.Name)

[<TestClass>]
type EndToEndWorkspaceTests () =

    [<TestMethod>]
    member _.WorkspaceAgentProcessesToolCall () =
        let agent = DemoWorkspace.createAgent ()
        let result = agent.RunAsync(AgentContext.allowAll, "What is the weather in Berlin?").Result
        Assert.IsTrue(result.Contains("18°C"), sprintf "Expected weather, got: %s" result)

    [<TestMethod>]
    member _.WorkspaceAgentUsesCalculator () =
        let agent = DemoWorkspace.createAgent ()
        let result = agent.RunAsync(AgentContext.allowAll, "calculate 2 + 2 for me").Result
        Assert.IsTrue(result.Contains("4"), sprintf "Expected '4', got: %s" result)

    [<TestMethod>]
    member _.WorkspaceResolvesTool () =
        let tool = DemoWorkspace.definitions.Tools |> List.tryFind (fun t -> t.Name = "get_weather")
        Assert.IsTrue(tool.IsSome)
        Assert.AreEqual("get_weather", tool.Value.Name)

    [<TestMethod>]
    member _.EachAgentInstanceIsIsolated () =
        let a1 = DemoWorkspace.createAgent ()
        let a2 = DemoWorkspace.createAgent ()
        let r1 = a1.RunAsync(AgentContext.allowAll, "hello").Result
        // Agents are stateless per call; distinct instances run independently.
        Assert.IsTrue(r1.Length > 0)
        Assert.IsFalse(System.Object.ReferenceEquals(a1, a2))

[<TestClass>]
type EndToEndToolTests () =

    [<TestMethod>]
    member _.WeatherToolReturnsData () =
        let result = (DemoTools.getWeather.Execute AgentContext.allowAll "London").Result
        Assert.IsTrue(result.Contains("18°C"))
        Assert.IsTrue(result.Contains("London"))

    [<TestMethod>]
    member _.CalculatorEvaluatesExpressions () =
        Assert.AreEqual("4", (DemoTools.calculator.Execute AgentContext.allowAll "2 + 2").Result)
        Assert.AreEqual("21", (DemoTools.calculator.Execute AgentContext.allowAll "3 * 7").Result)
        Assert.AreEqual("5", (DemoTools.calculator.Execute AgentContext.allowAll "10 / 2").Result)

    [<TestMethod>]
    member _.GreeterGeneratesGreeting () =
        let result = (DemoTools.greeter.Execute AgentContext.allowAll "Alice").Result
        Assert.IsTrue(result.Contains("Alice"))
        Assert.IsTrue(result.Contains("Hello"))

[<TestClass>]
type EndToEndProviderTests () =

    [<TestMethod>]
    member _.LocalProviderHandlesWeatherPrompt () =
        let provider = LocalLlmProvider() :> ILlmProvider
        let conversation = [ { Role = User; Content = "What's the weather?" } ]
        let result = provider.CompleteAsync conversation CompletionOptions.Default
        let r = result.Result
        Assert.AreEqual("stop", r.FinishReason)
        Assert.IsTrue(r.Content.Contains("tool"))
        Assert.IsTrue(r.Content.Contains("get_weather"))

    [<TestMethod>]
    member _.LocalProviderHandlesToolResult () =
        let provider = LocalLlmProvider() :> ILlmProvider
        let conversation = [ { Role = User; Content = "tool_result: 42" } ]
        let result = provider.CompleteAsync conversation CompletionOptions.Default
        let r = result.Result
        Assert.IsTrue(r.Content.Contains("42"))

    [<TestMethod>]
    member _.LocalProviderReportsTokensUsed () =
        let provider = LocalLlmProvider() :> ILlmProvider
        let conversation = [ { Role = User; Content = "hello" } ]
        let r = (provider.CompleteAsync conversation CompletionOptions.Default).Result
        Assert.IsTrue(r.TokensUsed.IsSome)
        Assert.IsTrue(r.TokensUsed.Value > 0)

