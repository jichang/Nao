namespace Nao.Providers.Tests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Providers

type private StubHttpMessageHandler(send: HttpRequestMessage -> HttpResponseMessage) =
    inherit HttpMessageHandler()

    override _.SendAsync(request, _cancellationToken: CancellationToken) =
        Task.FromResult(send request)

type private WaitingHttpMessageHandler() =
    inherit HttpMessageHandler()

    override _.SendAsync(_request, cancellationToken: CancellationToken) =
        task {
            do! Task.Delay(Timeout.Infinite, cancellationToken)
            return new HttpResponseMessage(HttpStatusCode.OK)
        }

[<TestClass>]
type McpJsonTests() =

    [<TestMethod>]
    member _.``Serializes escaped names and structured arguments``() =
        use argumentsDocument = JsonDocument.Parse("""{"text":"quoted value","count":2}""")
        let parameters = McpJson.ToolCallParamsDto()
        parameters.Name <- "tool\"name"
        parameters.Arguments <- argumentsDocument.RootElement.Clone()

        let json = McpJson.serializeRequest "request-id" "tools/call" parameters

        use requestDocument = JsonDocument.Parse(json)
        let root = requestDocument.RootElement
        Assert.AreEqual("2.0", root.GetProperty("jsonrpc").GetString())
        Assert.AreEqual("tool\"name", root.GetProperty("params").GetProperty("name").GetString())
        Assert.AreEqual(2, root.GetProperty("params").GetProperty("arguments").GetProperty("count").GetInt32())

[<TestClass>]
type ProviderFactoryTests () =

    [<TestMethod>]
    member _.CreatesOpenAIProvider () =
        let provider = ProviderFactory.create (OpenAI OpenAIConfig.Default)
        Assert.IsTrue(provider.Name.StartsWith "OpenAI")

    [<TestMethod>]
    member _.CreatesDeepSeekProvider () =
        let provider = ProviderFactory.create (DeepSeek DeepSeekConfig.Default)
        Assert.AreEqual("DeepSeek(deepseek-chat)", provider.Name)

    [<TestMethod>]
    member _.CreatesKimiProvider () =
        let provider = ProviderFactory.create (Kimi KimiConfig.Default)
        Assert.AreEqual("Kimi(kimi-k2.5)", provider.Name)

    [<TestMethod>]
    member _.CreatesAnthropicProvider () =
        let provider = ProviderFactory.create (Anthropic AnthropicConfig.Default)
        Assert.AreEqual("Anthropic(claude-sonnet-4-20250514)", provider.Name)

    [<TestMethod>]
    member _.CreatesVllmProvider () =
        let provider = ProviderFactory.create (Vllm VllmConfig.Default)
        Assert.IsTrue(provider.Name.StartsWith "vLLM")

    [<TestMethod>]
    member _.CreatesLlamaCppProvider () =
        let provider = ProviderFactory.create (LlamaCpp LlamaCppConfig.Default)
        Assert.IsTrue(provider.Name.StartsWith "llama.cpp")

    [<TestMethod>]
    member _.OpenAIProviderReturnsResultOnUnreachableServer () =
        // Against an unreachable endpoint the provider must return a graceful error
        // result rather than throwing.
        let provider = ProviderFactory.create (OpenAI { OpenAIConfig.Default with BaseUrl = "http://localhost:1" })
        let conversation = [ { Role = User; Content = "hi" } ]
        let result = (provider.CompleteAsync conversation CompletionOptions.Default).Result
        Assert.AreEqual("error", result.FinishReason)

    [<TestMethod>]
    member _.AnthropicProviderReturnsResultOnUnreachableServer () =
        let config = { AnthropicConfig.Default with BaseUrl = "http://localhost:1" }
        let provider = ProviderFactory.create (Anthropic config)
        let conversation = [ { Role = User; Content = "hi" } ]
        let result = (provider.CompleteAsync conversation CompletionOptions.Default).Result
        Assert.AreEqual("error", result.FinishReason)

[<TestClass>]
type OpenAIConfigTests () =

    [<TestMethod>]
    member _.DefaultHasExpectedValues () =
        let config = OpenAIConfig.Default
        Assert.AreEqual("gpt-4", config.Model)
        Assert.AreEqual("https://api.openai.com/v1/chat/completions", config.BaseUrl)
        Assert.AreEqual(None, config.TimeoutSeconds)

[<TestClass>]
type OpenAICompatibleProviderTests () =

    [<TestMethod>]
    member _.UsesConfiguredUrlWithoutModification () =
        let mutable requestUrl = ""
        let mutable requestBody = ""
        let handler =
            new StubHttpMessageHandler(fun request ->
                requestUrl <- request.RequestUri |> Option.ofObj |> Option.map _.AbsoluteUri |> Option.defaultValue ""
                requestBody <- request.Content |> Option.ofObj |> Option.map (fun content -> content.ReadAsStringAsync().Result) |> Option.defaultValue ""
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <- new StringContent(
                    """{"choices":[{"message":{"content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}""",
                    Encoding.UTF8,
                    "application/json")
                response)
        let endpoint = "https://compatible.test/custom/chat?version=2"
        use provider = new OpenAICompatibleProvider("Test", endpoint, "model", None, httpHandler = handler)
        let options =
            { CompletionOptions.Default with
                MaxTokens = Some 42
                StopSequences = [ "END" ] }

        let result =
            (provider :> ILlmProvider).CompleteAsync
                [ { Role = User; Content = "Hello" } ]
                options
            |> _.Result

        Assert.AreEqual(endpoint, requestUrl)
        use body = JsonDocument.Parse(requestBody)
        Assert.AreEqual("model", body.RootElement.GetProperty("model").GetString())
        Assert.AreEqual("user", body.RootElement.GetProperty("messages").[0].GetProperty("role").GetString())
        Assert.AreEqual(42, body.RootElement.GetProperty("max_tokens").GetInt32())
        Assert.AreEqual("END", body.RootElement.GetProperty("stop").[0].GetString())
        Assert.IsFalse(fst (body.RootElement.TryGetProperty("stream_options")))
        Assert.AreEqual("Hello", result.Content)
        Assert.AreEqual("stop", result.FinishReason)
        Assert.AreEqual(Some { InputTokens = 2; OutputTokens = 1 }, result.Usage)

    [<TestMethod>]
    member _.KeepsAggregateOnlyUsageUnsplit () =
        let handler =
            new StubHttpMessageHandler(fun _ ->
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <- new StringContent("""{"choices":[{"message":{"content":"Hello"},"finish_reason":"stop"}],"usage":{"total_tokens":3}}""", Encoding.UTF8, "application/json")
                response)
        use provider = new OpenAICompatibleProvider("Test", "https://compatible.test/chat", "model", None, httpHandler = handler)

        let result = (provider :> ILlmProvider).CompleteAsync [ { Role = User; Content = "Hello" } ] CompletionOptions.Default |> _.Result

        Assert.AreEqual(Some 3, result.TokensUsed)
        Assert.AreEqual(None, result.Usage)

    [<TestMethod>]
    member _.StreamsSplitUsageOnTerminalChunk () =
        let stream =
            String.concat "\n"
                [ "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"},\"finish_reason\":null}]}"
                  ""
                  "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}"
                  ""
                  "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":2,\"total_tokens\":6}}"
                  ""
                  "data: [DONE]" ]
        let handler =
            new StubHttpMessageHandler(fun _ ->
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <- new StringContent(stream, Encoding.UTF8, "text/event-stream")
                response)
        use provider = new OpenAICompatibleProvider("Test", "https://compatible.test/chat", "model", None, httpHandler = handler)
        let chunks = ResizeArray<CompletionChunk>()

        let result = (provider :> IStreamingLlmProvider).StreamAsync [ { Role = User; Content = "Hello" } ] CompletionOptions.Default chunks.Add |> _.Result

        Assert.AreEqual(Some { InputTokens = 4; OutputTokens = 2 }, result.Usage)
        Assert.AreEqual(Some { InputTokens = 4; OutputTokens = 2 }, chunks.[chunks.Count - 1].Usage)

    [<TestMethod>]
    [<DataRow("")>]
    [<DataRow("localhost:8000/v1/chat/completions")>]
    [<DataRow("ftp://localhost/v1/chat/completions")>]
    member _.RejectsInvalidUrl (url: string) =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            new OpenAICompatibleProvider("Test", url, "model", None) |> ignore)
        |> ignore

    [<TestMethod>]
    member _.ReturnsErrorForMissingRequiredResponseFields () =
        let handler =
            new StubHttpMessageHandler(fun _ ->
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <- new StringContent("{}", Encoding.UTF8, "application/json")
                response)
        use provider = new OpenAICompatibleProvider("Test", "https://compatible.test/chat", "model", None, httpHandler = handler)

        let result =
            (provider :> ILlmProvider).CompleteAsync
                [ { Role = User; Content = "Hello" } ]
                CompletionOptions.Default
            |> _.Result

        Assert.AreEqual("error", result.FinishReason)
        StringAssert.StartsWith(result.Content, "Parse error:")

[<TestClass>]
type DeepSeekConfigTests () =

    [<TestMethod>]
    member _.DefaultUsesDeepSeekApi () =
        let config = DeepSeekConfig.Default
        Assert.AreEqual("deepseek-chat", config.Model)
        Assert.AreEqual("https://api.deepseek.com/v1/chat/completions", config.BaseUrl)
        Assert.AreEqual(None, config.TimeoutSeconds)

[<TestClass>]
type KimiConfigTests () =

    [<TestMethod>]
    member _.DefaultUsesMoonshotApi () =
        let config = KimiConfig.Default
        Assert.AreEqual("kimi-k2.5", config.Model)
        Assert.AreEqual("https://api.moonshot.ai/v1/chat/completions", config.BaseUrl)
        Assert.AreEqual(None, config.TimeoutSeconds)

[<TestClass>]
type OllamaConfigTests () =

    [<TestMethod>]
    member _.DefaultDisablesReasoningForToolProtocols () =
        let config = OllamaConfig.Default
        Assert.AreEqual(Some "none", config.ReasoningEffort)
        Assert.AreEqual(None, config.TimeoutSeconds)

    [<TestMethod>]
    member _.ProviderReportsSplitUsage () =
        let handler =
            new StubHttpMessageHandler(fun request ->
                let requestUrl = request.RequestUri |> Option.ofObj |> Option.map _.AbsoluteUri |> Option.defaultValue ""
                Assert.AreEqual("https://ollama.test/v1/chat/completions", requestUrl)
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <- new StringContent("""{"choices":[{"message":{"content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}""", Encoding.UTF8, "application/json")
                response)
        let config = { OllamaConfig.Default with BaseUrl = "https://ollama.test" }
        use provider = new OllamaProvider(config, httpHandler = handler)

        let result = (provider :> ILlmProvider).CompleteAsync [ { Role = User; Content = "Hello" } ] CompletionOptions.Default |> _.Result

        Assert.AreEqual(Some 5, result.TokensUsed)
        Assert.AreEqual(Some { InputTokens = 3; OutputTokens = 2 }, result.Usage)

[<TestClass>]
type AnthropicConfigTests () =

    [<TestMethod>]
    member _.DefaultHasExpectedValues () =
        let config = AnthropicConfig.Default
        Assert.AreEqual("claude-sonnet-4-20250514", config.Model)
        Assert.AreEqual("https://api.anthropic.com", config.BaseUrl)
        Assert.AreEqual(None, config.TimeoutSeconds)

[<TestClass>]
type AnthropicProviderTests () =

    [<TestMethod>]
    member _.ConfiguredTimeoutCancelsRequest () =
        let config =
            { AnthropicConfig.Default with
                BaseUrl = "https://anthropic.test"
                TimeoutSeconds = Some 1 }
        use provider = new AnthropicProvider(config, new WaitingHttpMessageHandler())

        let result =
            (provider :> ILlmProvider).CompleteAsync
                [ { Role = User; Content = "Wait." } ]
                CompletionOptions.Default
            |> _.Result

        Assert.AreEqual("error", result.FinishReason)
        StringAssert.Contains(result.Content, "canceled")

    [<TestMethod>]
    member _.SendsNativeMessagesRequestAndParsesResponse () =
        let mutable requestUrl = ""
        let mutable apiKey = ""
        let mutable apiVersion = ""
        let mutable requestBody = ""
        let handler =
            new StubHttpMessageHandler(fun request ->
                requestUrl <- request.RequestUri |> Option.ofObj |> Option.map _.AbsoluteUri |> Option.defaultValue ""
                apiKey <- request.Headers.GetValues("x-api-key") |> Seq.exactlyOne
                apiVersion <- request.Headers.GetValues("anthropic-version") |> Seq.exactlyOne
                requestBody <- request.Content |> Option.ofObj |> Option.map (fun content -> content.ReadAsStringAsync().Result) |> Option.defaultValue ""
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <- new StringContent(
                    """{"content":[{"type":"text","text":"Hello"},{"type":"text","text":" world"}],"stop_reason":"max_tokens","usage":{"input_tokens":10,"output_tokens":5}}""",
                    Encoding.UTF8,
                    "application/json")
                response)
        let config =
            { AnthropicConfig.Default with
                ApiKey = "test-key"
                BaseUrl = "https://anthropic.test/" }
        use provider = new AnthropicProvider(config, handler)
        let conversation =
            [ { Role = System; Content = "Be concise." }
              { Role = User; Content = "Say hello." } ]
        let options =
            { CompletionOptions.Default with
                MaxTokens = Some 128
                StopSequences = [ "END" ] }

        let result = (provider :> ILlmProvider).CompleteAsync conversation options |> _.Result

        Assert.AreEqual("https://anthropic.test/v1/messages", requestUrl)
        Assert.AreEqual("test-key", apiKey)
        Assert.AreEqual("2023-06-01", apiVersion)
        use body = JsonDocument.Parse(requestBody)
        Assert.AreEqual("claude-sonnet-4-20250514", body.RootElement.GetProperty("model").GetString())
        Assert.AreEqual("Be concise.", body.RootElement.GetProperty("system").GetString())
        Assert.AreEqual(128, body.RootElement.GetProperty("max_tokens").GetInt32())
        Assert.AreEqual("user", body.RootElement.GetProperty("messages").[0].GetProperty("role").GetString())
        Assert.AreEqual("END", body.RootElement.GetProperty("stop_sequences").[0].GetString())
        Assert.AreEqual("Hello world", result.Content)
        Assert.AreEqual("length", result.FinishReason)
        Assert.AreEqual(Some 15, result.TokensUsed)
        Assert.AreEqual(Some { InputTokens = 10; OutputTokens = 5 }, result.Usage)

    [<TestMethod>]
    member _.StreamsTextAndAggregatesUsage () =
        let stream =
            String.concat "\n"
                [ "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":5,\"output_tokens\":0}}}"
                  ""
                  "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}"
                  ""
                  "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\" world\"}}"
                  ""
                  "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":3}}"
                  ""
                  "data: {\"type\":\"message_stop\"}" ]
        let handler =
            new StubHttpMessageHandler(fun _ ->
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <- new StringContent(stream, Encoding.UTF8, "text/event-stream")
                response)
        use provider = new AnthropicProvider({ AnthropicConfig.Default with BaseUrl = "https://anthropic.test" }, handler)
        let chunks = ResizeArray<CompletionChunk>()

        let result =
            (provider :> IStreamingLlmProvider).StreamAsync
                [ { Role = User; Content = "Say hello." } ]
                CompletionOptions.Default
                chunks.Add
            |> _.Result

        Assert.AreEqual("Hello world", result.Content)
        Assert.AreEqual("stop", result.FinishReason)
        Assert.AreEqual(Some 8, result.TokensUsed)
        Assert.AreEqual(Some { InputTokens = 5; OutputTokens = 3 }, result.Usage)
        CollectionAssert.AreEqual([| "Hello"; " world"; "" |], chunks |> Seq.map _.Delta |> Seq.toArray)
        Assert.AreEqual(Some "stop", chunks.[2].FinishReason)
        Assert.AreEqual(Some 8, chunks.[2].TokensUsed)
        Assert.AreEqual(Some { InputTokens = 5; OutputTokens = 3 }, chunks.[2].Usage)

[<TestClass>]
type VllmConfigTests () =

    [<TestMethod>]
    member _.DefaultUsesLocalhost () =
        let config = VllmConfig.Default
        Assert.AreEqual("http://localhost:8000/v1/chat/completions", config.BaseUrl)
        Assert.AreEqual(None, config.ApiKey)
        Assert.AreEqual(None, config.TimeoutSeconds)

[<TestClass>]
type LlamaCppConfigTests () =

    [<TestMethod>]
    member _.DefaultUsesLocalhost () =
        let config = LlamaCppConfig.Default
        Assert.AreEqual("http://localhost:8080/v1/chat/completions", config.BaseUrl)
        Assert.AreEqual(None, config.NPredict)
        Assert.AreEqual(None, config.TimeoutSeconds)
