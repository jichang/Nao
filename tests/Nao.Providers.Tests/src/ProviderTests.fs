namespace Nao.Providers.Tests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Providers

type private StubHttpMessageHandler(send: HttpRequestMessage -> HttpResponseMessage) =
    inherit HttpMessageHandler()
    override _.SendAsync(request, _cancellationToken: CancellationToken) = Task.FromResult(send request)

type private WaitingHttpMessageHandler() =
    inherit HttpMessageHandler()

    override _.SendAsync(_request, cancellationToken: CancellationToken) =
        task {
            do! Task.Delay(Timeout.Infinite, cancellationToken)
            return new HttpResponseMessage(HttpStatusCode.OK)
        }

module private ProviderFailure =
    let capture (operation: unit -> Task<'value>) =
        task {
            let! error = Assert.ThrowsExactlyAsync<PlatformFailureException>(Func<Task>(fun () -> operation () :> Task))

            match error :> exn with
            | PlatformFailureException failure -> return failure
            | _ -> return failwith "Expected a structured platform failure."
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
type ProviderFactoryTests() =
    [<TestMethod>]
    member _.CreatesOpenAIProvider() =
        let provider = ProviderFactory.create (OpenAI OpenAIConfig.Default)
        Assert.IsTrue((provider.Name()).StartsWith "OpenAI")

    [<TestMethod>]
    member _.CreatesDeepSeekProvider() =
        let provider = ProviderFactory.create (DeepSeek DeepSeekConfig.Default)
        Assert.AreEqual("DeepSeek(deepseek-chat)", provider.Name())

    [<TestMethod>]
    member _.CreatesKimiProvider() =
        let provider = ProviderFactory.create (Kimi KimiConfig.Default)
        Assert.AreEqual("Kimi(kimi-k2.5)", provider.Name())

    [<TestMethod>]
    member _.CreatesAnthropicProvider() =
        let provider = ProviderFactory.create (Anthropic AnthropicConfig.Default)
        Assert.AreEqual("Anthropic(claude-sonnet-4-20250514)", provider.Name())

    [<TestMethod>]
    member _.CreatesVllmProvider() =
        let provider = ProviderFactory.create (Vllm VllmConfig.Default)
        Assert.IsTrue((provider.Name()).StartsWith "vLLM")

    [<TestMethod>]
    member _.CreatesLlamaCppProvider() =
        let provider = ProviderFactory.create (LlamaCpp LlamaCppConfig.Default)
        Assert.IsTrue((provider.Name()).StartsWith "llama.cpp")

    [<TestMethod>]
    member _.OpenAIProviderRaisesTransientFailureOnUnreachableServer() : Task =
        let provider =
            ProviderFactory.create (
                OpenAI
                    { OpenAIConfig.Default with
                        BaseUrl = "http://localhost:1" }
            )

        (task {
            let! failure =
                ProviderFailure.capture (fun () ->
                    provider.CompleteAsync [ { Role = User; Content = "hi" } ] CompletionOptions.Default)

            Assert.AreEqual(PlatformErrorCategory.TransientDependency, failure.Category)
            Assert.IsTrue(failure.Retryable)
        }
        :> Task)

    [<TestMethod>]
    member _.AnthropicProviderRaisesTransientFailureOnUnreachableServer() : Task =
        let provider =
            ProviderFactory.create (
                Anthropic
                    { AnthropicConfig.Default with
                        BaseUrl = "http://localhost:1" }
            )

        (task {
            let! failure =
                ProviderFailure.capture (fun () ->
                    provider.CompleteAsync [ { Role = User; Content = "hi" } ] CompletionOptions.Default)

            Assert.AreEqual(PlatformErrorCategory.TransientDependency, failure.Category)
            Assert.IsTrue(failure.Retryable)
        }
        :> Task)

[<TestClass>]
type OpenAIConfigTests() =
    [<TestMethod>]
    member _.DefaultHasExpectedValues() =
        let config = OpenAIConfig.Default
        Assert.AreEqual("gpt-4", config.Model)
        Assert.AreEqual("https://api.openai.com/v1/chat/completions", config.BaseUrl)
        Assert.AreEqual(None, config.TimeoutSeconds)

[<TestClass>]
type OpenAICompatibleProviderTests() =
    [<TestMethod>]
    member _.UsesConfiguredUrlWithoutModification() =
        let mutable requestUrl = ""
        let mutable requestBody = ""

        let handler =
            StubHttpMessageHandler(fun request ->
                requestUrl <-
                    request.RequestUri
                    |> Option.ofObj
                    |> Option.map _.AbsoluteUri
                    |> Option.defaultValue ""

                requestBody <-
                    request.Content
                    |> Option.ofObj
                    |> Option.map (fun content -> content.ReadAsStringAsync().Result)
                    |> Option.defaultValue ""

                let response = new HttpResponseMessage(HttpStatusCode.OK)

                response.Content <-
                    new StringContent(
                        """{"choices":[{"message":{"content":"Hello"},"finish_reason":"stop"}],"usage":{"total_tokens":3}}""",
                        Encoding.UTF8,
                        "application/json"
                    )

                response)

        let endpoint = "https://compatible.test/custom/chat?version=2"

        let provider =
            OpenAICompatibleProvider.createWithHandler "Test" endpoint "model" None None (Some handler)

        let options =
            { CompletionOptions.Default with
                MaxTokens = Some 42
                StopSequences = [ "END" ] }

        let result =
            provider.CompleteAsync [ { Role = User; Content = "Hello" } ] options
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

    [<TestMethod>]
    [<DataRow("")>]
    [<DataRow("localhost:8000/v1/chat/completions")>]
    [<DataRow("ftp://localhost/v1/chat/completions")>]
    member _.RejectsInvalidUrl(url: string) =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            OpenAICompatibleProvider.create "Test" url "model" None None |> ignore)
        |> ignore

    [<TestMethod>]
    member _.RejectsMissingRequiredResponseFields() : Task =
        (task {
            let handler =
                StubHttpMessageHandler(fun _ ->
                    let response = new HttpResponseMessage(HttpStatusCode.OK)
                    response.Content <- new StringContent("{}", Encoding.UTF8, "application/json")
                    response)

            let provider =
                OpenAICompatibleProvider.createWithHandler
                    "Test"
                    "https://compatible.test/chat"
                    "model"
                    None
                    None
                    (Some handler)

            let! failure =
                ProviderFailure.capture (fun () ->
                    provider.CompleteAsync [ { Role = User; Content = "Hello" } ] CompletionOptions.Default)

            Assert.AreEqual(PlatformErrorCategory.InvalidOutput, failure.Category)
            Assert.IsFalse(failure.Retryable)
        }
        :> Task)

    [<TestMethod>]
    member _.MapsHttpFailuresToCanonicalCategories() : Task =
        (task {
            let cases =
                [ 400, PlatformErrorCategory.InvalidInput, false
                  401, PlatformErrorCategory.PermissionDenied, false
                  403, PlatformErrorCategory.PermissionDenied, false
                  404, PlatformErrorCategory.PermanentDependency, false
                  408, PlatformErrorCategory.ResourceExhausted, true
                  422, PlatformErrorCategory.InvalidInput, false
                  429, PlatformErrorCategory.ResourceExhausted, true
                  500, PlatformErrorCategory.TransientDependency, true
                  503, PlatformErrorCategory.TransientDependency, true ]

            for statusCode, expectedCategory, expectedRetryable in cases do
                let handler =
                    StubHttpMessageHandler(fun _ ->
                        let response = new HttpResponseMessage(enum<HttpStatusCode> statusCode)
                        response.Content <- new StringContent("provider failure")
                        response)

                let provider =
                    OpenAICompatibleProvider.createWithHandler
                        "Test"
                        "https://compatible.test/chat"
                        "model"
                        None
                        None
                        (Some handler)

                let! failure =
                    ProviderFailure.capture (fun () ->
                        provider.CompleteAsync [ { Role = User; Content = "Hello" } ] CompletionOptions.Default)

                Assert.AreEqual(expectedCategory, failure.Category, $"HTTP {statusCode}")
                Assert.AreEqual(expectedRetryable, failure.Retryable, $"HTTP {statusCode}")
        }
        :> Task)

[<TestClass>]
type DeepSeekConfigTests() =
    [<TestMethod>]
    member _.DefaultUsesDeepSeekApi() =
        let config = DeepSeekConfig.Default
        Assert.AreEqual("deepseek-chat", config.Model)
        Assert.AreEqual("https://api.deepseek.com/v1/chat/completions", config.BaseUrl)
        Assert.AreEqual(None, config.TimeoutSeconds)

[<TestClass>]
type KimiConfigTests() =
    [<TestMethod>]
    member _.DefaultUsesMoonshotApi() =
        let config = KimiConfig.Default
        Assert.AreEqual("kimi-k2.5", config.Model)
        Assert.AreEqual("https://api.moonshot.ai/v1/chat/completions", config.BaseUrl)
        Assert.AreEqual(None, config.TimeoutSeconds)

[<TestClass>]
type OllamaConfigTests() =
    [<TestMethod>]
    member _.DefaultDisablesReasoningForToolProtocols() =
        let config = OllamaConfig.Default
        Assert.AreEqual(Some "none", config.ReasoningEffort)
        Assert.AreEqual(None, config.TimeoutSeconds)

[<TestClass>]
type AnthropicConfigTests() =
    [<TestMethod>]
    member _.DefaultHasExpectedValues() =
        let config = AnthropicConfig.Default
        Assert.AreEqual("claude-sonnet-4-20250514", config.Model)
        Assert.AreEqual("https://api.anthropic.com", config.BaseUrl)
        Assert.AreEqual(None, config.TimeoutSeconds)

[<TestClass>]
type AnthropicProviderTests() =
    [<TestMethod>]
    member _.ConfiguredTimeoutCancelsRequest() : Task =
        (task {
            let config =
                { AnthropicConfig.Default with
                    BaseUrl = "https://anthropic.test"
                    TimeoutSeconds = Some 1 }

            let provider =
                AnthropicProvider.createWithHandler config (Some(WaitingHttpMessageHandler()))

            let! failure =
                ProviderFailure.capture (fun () ->
                    provider.CompleteAsync [ { Role = User; Content = "Wait." } ] CompletionOptions.Default)

            Assert.AreEqual(PlatformErrorCategory.TransientDependency, failure.Category)
            Assert.IsTrue(failure.Retryable)
        }
        :> Task)

    [<TestMethod>]
    member _.SendsNativeMessagesRequestAndParsesResponse() =
        let mutable requestUrl = ""
        let mutable apiKey = ""
        let mutable apiVersion = ""
        let mutable requestBody = ""

        let handler =
            StubHttpMessageHandler(fun request ->
                requestUrl <-
                    request.RequestUri
                    |> Option.ofObj
                    |> Option.map _.AbsoluteUri
                    |> Option.defaultValue ""

                apiKey <- request.Headers.GetValues("x-api-key") |> Seq.exactlyOne
                apiVersion <- request.Headers.GetValues("anthropic-version") |> Seq.exactlyOne

                requestBody <-
                    request.Content
                    |> Option.ofObj
                    |> Option.map (fun content -> content.ReadAsStringAsync().Result)
                    |> Option.defaultValue ""

                let response = new HttpResponseMessage(HttpStatusCode.OK)

                response.Content <-
                    new StringContent(
                        """{"content":[{"type":"text","text":"Hello"},{"type":"text","text":" world"}],"stop_reason":"max_tokens","usage":{"input_tokens":10,"output_tokens":5}}""",
                        Encoding.UTF8,
                        "application/json"
                    )

                response)

        let config =
            { AnthropicConfig.Default with
                ApiKey = "test-key"
                BaseUrl = "https://anthropic.test/" }

        let provider = AnthropicProvider.createWithHandler config (Some handler)

        let conversation =
            [ { Role = System
                Content = "Be concise." }
              { Role = User; Content = "Say hello." } ]

        let options =
            { CompletionOptions.Default with
                MaxTokens = Some 128
                StopSequences = [ "END" ] }

        let result = provider.CompleteAsync conversation options |> _.Result
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

    [<TestMethod>]
    member _.StreamsTextAndAggregatesUsage() =
        let stream =
            String.concat
                "\n"
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
            StubHttpMessageHandler(fun _ ->
                let response = new HttpResponseMessage(HttpStatusCode.OK)
                response.Content <- new StringContent(stream, Encoding.UTF8, "text/event-stream")
                response)

        let provider =
            AnthropicProvider.createWithHandler
                { AnthropicConfig.Default with
                    BaseUrl = "https://anthropic.test" }
                (Some handler)

        let chunks = ResizeArray<CompletionChunk>()

        let result =
            LlmProvider.streamAsync
                provider
                [ { Role = User; Content = "Say hello." } ]
                CompletionOptions.Default
                chunks.Add
            |> _.Result

        Assert.AreEqual("Hello world", result.Content)
        Assert.AreEqual("stop", result.FinishReason)
        Assert.AreEqual(Some 8, result.TokensUsed)
        CollectionAssert.AreEqual([| "Hello"; " world"; "" |], chunks |> Seq.map _.Delta |> Seq.toArray)
        Assert.AreEqual(Some "stop", chunks.[2].FinishReason)
        Assert.AreEqual(Some 8, chunks.[2].TokensUsed)

[<TestClass>]
type VllmConfigTests() =
    [<TestMethod>]
    member _.DefaultUsesLocalhost() =
        let config = VllmConfig.Default
        Assert.AreEqual("http://localhost:8000/v1/chat/completions", config.BaseUrl)
        Assert.AreEqual(None, config.ApiKey)
        Assert.AreEqual(None, config.TimeoutSeconds)

[<TestClass>]
type LlamaCppConfigTests() =
    [<TestMethod>]
    member _.DefaultUsesLocalhost() =
        let config = LlamaCppConfig.Default
        Assert.AreEqual("http://localhost:8080/v1/chat/completions", config.BaseUrl)
        Assert.AreEqual(None, config.NPredict)
        Assert.AreEqual(None, config.TimeoutSeconds)
