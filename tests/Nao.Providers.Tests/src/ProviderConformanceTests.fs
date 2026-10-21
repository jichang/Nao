namespace Nao.Providers.Tests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Providers

type private ConformanceHttpHandler(send: HttpRequestMessage -> HttpResponseMessage) =
    inherit HttpMessageHandler()
    override _.SendAsync(request, _cancellationToken) = Task.FromResult(send request)

module private ProviderConformance =
    let assertBufferedStreamingEquivalent (provider: LlmProvider) =
        task {
            let correlation = CorrelationContext.root ()
            let conversation = [ { Role = User; Content = "hello" } ]

            let! buffered =
                LlmProvider.completeAsync
                    correlation
                    conversation
                    CompletionOptions.Default
                    CancellationToken.None
                    provider

            let chunks = ResizeArray<CompletionChunk>()

            let! streamed =
                LlmProvider.streamAsync
                    provider
                    correlation
                    conversation
                    CompletionOptions.Default
                    chunks.Add
                    CancellationToken.None

            Assert.AreEqual(buffered, streamed)
            Assert.AreEqual(buffered.Content, chunks |> Seq.map _.Delta |> String.concat "")

            Assert.AreEqual(
                Some(ProviderStreamTermination.Completed buffered.FinishReason),
                chunks |> Seq.last |> _.Termination
            )
        }

    let response (contentType: string) (body: string) =
        let response = new HttpResponseMessage(HttpStatusCode.OK)
        response.Content <- new StringContent(body, Encoding.UTF8, contentType)
        response

[<TestClass>]
type ProviderConformanceTests() =
    let isStreaming (request: HttpRequestMessage) =
        request.Content
        |> Option.ofObj
        |> Option.exists (fun content -> content.ReadAsStringAsync().Result.Contains("\"stream\":true"))

    [<TestMethod>]
    member _.``OpenAI-compatible adapter conforms to common buffered and streaming semantics``() : Task =
        task {
            let handler =
                new ConformanceHttpHandler(fun request ->
                    if isStreaming request then
                        ProviderConformance.response
                            "text/event-stream"
                            "data: {\"id\":\"request-1\",\"model\":\"model\",\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"\"},\"finish_reason\":\"stop\"}],\"usage\":{\"total_tokens\":8}}\n\ndata: [DONE]\n"
                    else
                        ProviderConformance.response
                            "application/json"
                            "{\"id\":\"request-1\",\"model\":\"model\",\"choices\":[{\"message\":{\"content\":\"hello\"},\"finish_reason\":\"stop\"}],\"usage\":{\"total_tokens\":8}}")

            let provider =
                OpenAICompatibleProvider.createWithHandler
                    "test"
                    "https://provider.test/v1/chat/completions"
                    "model"
                    None
                    None
                    (Some handler)

            try
                do! ProviderConformance.assertBufferedStreamingEquivalent provider
            finally
                provider.Dispose()
        }

    [<TestMethod>]
    member _.``Ollama adapter conforms to common buffered and streaming semantics``() : Task =
        task {
            let handler =
                new ConformanceHttpHandler(fun request ->
                    if isStreaming request then
                        ProviderConformance.response
                            "text/event-stream"
                            "data: {\"id\":\"request-1\",\"model\":\"llama3.2\",\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"\"},\"finish_reason\":\"stop\"}],\"usage\":{\"total_tokens\":8}}\n\ndata: [DONE]\n"
                    else
                        ProviderConformance.response
                            "application/json"
                            "{\"id\":\"request-1\",\"model\":\"llama3.2\",\"choices\":[{\"message\":{\"content\":\"hello\"},\"finish_reason\":\"stop\"}],\"usage\":{\"total_tokens\":8}}")

            let provider =
                OllamaProvider.createWithHandler
                    { OllamaConfig.Default with
                        BaseUrl = "https://provider.test" }
                    (Some handler)

            try
                do! ProviderConformance.assertBufferedStreamingEquivalent provider
            finally
                provider.Dispose()
        }

    [<TestMethod>]
    member _.``Anthropic adapter conforms to common buffered and streaming semantics``() : Task =
        task {
            let handler =
                new ConformanceHttpHandler(fun request ->
                    if isStreaming request then
                        ProviderConformance.response
                            "text/event-stream"
                            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"request-1\",\"model\":\"claude\",\"usage\":{\"input_tokens\":5,\"output_tokens\":0}}}\n\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hello\"}}\n\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":3}}\n\ndata: {\"type\":\"message_stop\"}\n"
                    else
                        ProviderConformance.response
                            "application/json"
                            "{\"id\":\"request-1\",\"model\":\"claude\",\"content\":[{\"type\":\"text\",\"text\":\"hello\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":5,\"output_tokens\":3}}")

            let provider =
                AnthropicProvider.createWithHandler
                    { AnthropicConfig.Default with
                        BaseUrl = "https://provider.test" }
                    None
                    (Some handler)

            try
                do! ProviderConformance.assertBufferedStreamingEquivalent provider
            finally
                provider.Dispose()
        }

    [<TestMethod>]
    member _.``Partial stream protocol failure is explicit and never resembles completion``() : Task =
        task {
            let handler =
                new ConformanceHttpHandler(fun _ ->
                    ProviderConformance.response
                        "text/event-stream"
                        "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}\n\ndata: not-json\n")

            let provider =
                OpenAICompatibleProvider.createWithHandler
                    "test"
                    "https://provider.test/v1/chat/completions"
                    "model"
                    None
                    None
                    (Some handler)

            try
                let chunks = ResizeArray<CompletionChunk>()

                let! failure =
                    Assert.ThrowsExactlyAsync<PlatformFailureException>(
                        Func<Task>(fun () ->
                            LlmProvider.streamAsync
                                provider
                                (CorrelationContext.root ())
                                [ { Role = User; Content = "hello" } ]
                                CompletionOptions.Default
                                chunks.Add
                                CancellationToken.None
                            :> Task)
                    )

                match failure :> exn with
                | PlatformFailureException value ->
                    Assert.AreEqual(Some ProviderErrorCategory.Protocol, value.ProviderCategory)
                | _ -> Assert.Fail("Expected a normalized provider failure.")

                Assert.AreEqual(
                    Some(ProviderStreamTermination.Partial ProviderErrorCategory.Protocol),
                    chunks |> Seq.last |> _.Termination
                )
            finally
                provider.Dispose()
        }
