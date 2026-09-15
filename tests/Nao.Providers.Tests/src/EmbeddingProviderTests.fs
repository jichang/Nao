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
open Nao.Knowledge
open Nao.Providers

type private EmbeddingStubHttpMessageHandler(send: HttpRequestMessage -> CancellationToken -> Task<HttpResponseMessage>)
    =
    inherit HttpMessageHandler()
    override _.SendAsync(request, cancellationToken) = send request cancellationToken

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type EmbeddingProviderTests() =
    let now = DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero)

    let authorization =
        SecurityPrincipal.create (TenantId.parse "embedding-provider-tenant") (UserId.parse "provider-user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "provider-workspace") None
            |> Option.get

    let documentVersionId =
        KnowledgeDocumentVersionId.derive
            (KnowledgeDocumentId.parse "embedding-provider-document")
            1
            (ContentHash.computeText "embedding-provider-document")

    let descriptor componentId model dimensions =
        EmbeddingCapabilityDescriptor.create
            (ComponentVersion.create componentId model)
            dimensions
            EmbeddingNormalization.UnitLength
            VectorDistanceMetric.Cosine
            (ComponentVersion.create "provider-tokenizer" "1")
            (EmbeddingCapabilityLimits.create 8 1000 256 1024)

    let input sequence text =
        let hash = ContentHash.computeText text

        { ChunkId = KnowledgeChunkId.derive documentVersionId "provider-fixture" sequence hash
          ContentHash = hash
          Text = text }

    let batch inputs =
        { Id =
            EmbeddingBatchId.derive
                (EmbeddingProfileId.parse "eprof_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
                inputs
          Inputs = inputs }

    let response status body =
        new HttpResponseMessage(status, Content = new StringContent(body, Encoding.UTF8, "application/json"))

    let invoke provider inputs cancellationToken =
        provider.EmbedBatchAsync authorization (batch inputs) cancellationToken
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Hosted provider resolves credentials per operation and restores indexed response order``() =
        let profile = descriptor "openai-compatible" "text-embedding-3-small-2026-01-15" 2
        let secretReference = SecretReference.parse "providers/openai/embedding-key"
        let mutable resolutions = 0
        let mutable requests = 0

        let secretProvider =
            SecretProvider.create "test-vault" "1" (fun actualAuthorization reference _ ->
                Assert.AreEqual(authorization, actualAuthorization)
                Assert.AreEqual(secretReference, reference)
                resolutions <- resolutions + 1

                Task.FromResult(
                    SecretLookup.Available(
                        SecretVersion.parse (string resolutions),
                        SecretValue.create reference $"secret-{resolutions}"
                    )
                ))

        let handler =
            new EmbeddingStubHttpMessageHandler(fun request cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()
                    requests <- requests + 1

                    let requestUri =
                        request.RequestUri
                        |> Option.ofObj
                        |> Option.defaultWith (fun () -> failwith "Request URI is required.")

                    let requestAuthorization =
                        request.Headers.Authorization
                        |> Option.ofObj
                        |> Option.defaultWith (fun () -> failwith "Authorization header is required.")

                    let requestContent =
                        request.Content
                        |> Option.ofObj
                        |> Option.defaultWith (fun () -> failwith "Request content is required.")

                    Assert.AreEqual("https://embedding.example/v1/embeddings", requestUri.AbsoluteUri)
                    Assert.AreEqual($"secret-{requests}", requestAuthorization.Parameter)
                    let! body = requestContent.ReadAsStringAsync(cancellationToken)
                    use document = JsonDocument.Parse body

                    Assert.AreEqual(
                        "text-embedding-3-small-2026-01-15",
                        document.RootElement.GetProperty("model").GetString()
                    )

                    Assert.AreEqual("float", document.RootElement.GetProperty("encoding_format").GetString())
                    Assert.AreEqual(2, document.RootElement.GetProperty("input").GetArrayLength())

                    return
                        response
                            HttpStatusCode.OK
                            """{"model":"text-embedding-3-small-2026-01-15","data":[{"index":1,"embedding":[3.0,4.0]},{"index":0,"embedding":[1.0,2.0]}],"usage":{"prompt_tokens":7}}"""
                })

        let config: OpenAICompatibleEmbeddingConfig =
            { Endpoint = "https://embedding.example/v1/embeddings"
              ApiKeyReference = Some secretReference
              Model = "text-embedding-3-small-2026-01-15"
              Descriptor = profile
              CountTokens = fun text -> text.Length
              CostPerMillionInputTokensUsd = 0.02m
              TimeoutSeconds = Some 30 }

        let provider =
            OpenAICompatibleEmbeddingProvider.createWithHandler config (Some secretProvider) (Some handler) (fun () ->
                now)

        try
            let inputs = [ input 0 "first"; input 1 "second" ]

            for _ in 1..2 do
                let result = invoke provider inputs CancellationToken.None
                CollectionAssert.AreEqual([| 1.0; 2.0 |], result.Embeddings[0].Values)
                CollectionAssert.AreEqual([| 3.0; 4.0 |], result.Embeddings[1].Values)
                Assert.AreEqual(7, result.Usage.InputTokens)
                Assert.AreEqual(0.00000014m, result.Usage.EstimatedCostUsd)

            Assert.AreEqual(2, resolutions)
            Assert.AreEqual(2, requests)
        finally
            provider.Dispose()

    [<TestMethod>]
    member _.``Local Ollama provider uses native batch endpoint without truncation``() =
        let profile = descriptor "ollama" "nomic-embed-text:v1" 3
        let mutable requestBody = ""

        let handler =
            new EmbeddingStubHttpMessageHandler(fun request cancellationToken ->
                task {
                    let requestUri =
                        request.RequestUri
                        |> Option.ofObj
                        |> Option.defaultWith (fun () -> failwith "Request URI is required.")

                    let requestContent =
                        request.Content
                        |> Option.ofObj
                        |> Option.defaultWith (fun () -> failwith "Request content is required.")

                    Assert.AreEqual("http://localhost:11434/api/embed", requestUri.AbsoluteUri)
                    Assert.IsNull(request.Headers.Authorization)
                    let! body = requestContent.ReadAsStringAsync(cancellationToken)
                    requestBody <- body

                    return
                        response
                            HttpStatusCode.OK
                            """{"model":"nomic-embed-text:v1","embeddings":[[1.0,2.0,3.0],[4.0,5.0,6.0]],"prompt_eval_count":5}"""
                })

        let config: OllamaEmbeddingConfig =
            { BaseUrl = "http://localhost:11434"
              Model = "nomic-embed-text:v1"
              Descriptor = profile
              CountTokens = fun text -> text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length
              TimeoutSeconds = None }

        let provider =
            OllamaEmbeddingProvider.createWithHandler config (Some handler) (fun () -> now)

        try
            let result =
                invoke provider [ input 0 "first value"; input 1 "second" ] CancellationToken.None

            use document = JsonDocument.Parse requestBody
            Assert.AreEqual("nomic-embed-text:v1", document.RootElement.GetProperty("model").GetString())
            Assert.IsFalse(document.RootElement.GetProperty("truncate").GetBoolean())
            Assert.AreEqual(2, document.RootElement.GetProperty("input").GetArrayLength())
            Assert.AreEqual(profile.ProfileId, result.Embeddings[0].ProfileId)
            Assert.AreEqual(5, result.Usage.InputTokens)
            Assert.AreEqual(0m, result.Usage.EstimatedCostUsd)
        finally
            provider.Dispose()

    [<TestMethod>]
    member _.``Hosted provider classifies HTTP failures without copying response content``() =
        let profile = descriptor "openai-compatible" "hosted-model-v1" 2
        let canary = "prohibited-raw-content"

        let handler =
            new EmbeddingStubHttpMessageHandler(fun _ _ ->
                response HttpStatusCode.InternalServerError $"provider echoed {canary}"
                |> Task.FromResult)

        let config: OpenAICompatibleEmbeddingConfig =
            { Endpoint = "https://embedding.example/v1/embeddings"
              ApiKeyReference = None
              Model = "hosted-model-v1"
              Descriptor = profile
              CountTokens = fun _ -> 1
              CostPerMillionInputTokensUsd = 0m
              TimeoutSeconds = None }

        let provider =
            OpenAICompatibleEmbeddingProvider.createWithHandler config None (Some handler) (fun () -> now)

        try
            let error =
                Assert.ThrowsExactly<PlatformFailureException>(fun () ->
                    invoke provider [ input 0 "sensitive source" ] CancellationToken.None |> ignore)

            match error :> exn with
            | PlatformFailureException failure ->
                Assert.AreEqual(PlatformErrorCategory.TransientDependency, failure.Category)
                Assert.IsTrue(failure.Retryable)
                Assert.IsFalse(failure.Message.Contains(canary, StringComparison.Ordinal))
                Assert.IsFalse(failure.Message.Contains("sensitive source", StringComparison.Ordinal))
            | _ -> Assert.Fail("Expected a structured platform failure.")
        finally
            provider.Dispose()

    [<TestMethod>]
    member _.``Hosted provider rejects response model drift``() =
        let profile = descriptor "openai-compatible" "pinned-model-v1" 2

        let handler =
            new EmbeddingStubHttpMessageHandler(fun _ _ ->
                response
                    HttpStatusCode.OK
                    """{"model":"different-model","data":[{"index":0,"embedding":[1.0,2.0]}],"usage":{"prompt_tokens":1}}"""
                |> Task.FromResult)

        let config: OpenAICompatibleEmbeddingConfig =
            { Endpoint = "https://embedding.example/v1/embeddings"
              ApiKeyReference = None
              Model = "pinned-model-v1"
              Descriptor = profile
              CountTokens = fun _ -> 1
              CostPerMillionInputTokensUsd = 0m
              TimeoutSeconds = None }

        let provider =
            OpenAICompatibleEmbeddingProvider.createWithHandler config None (Some handler) (fun () -> now)

        try
            let error =
                Assert.ThrowsExactly<PlatformFailureException>(fun () ->
                    invoke provider [ input 0 "model drift" ] CancellationToken.None |> ignore)

            match error :> exn with
            | PlatformFailureException failure ->
                Assert.AreEqual(PlatformErrorCategory.InvalidOutput, failure.Category)
                Assert.IsFalse(failure.Retryable)
            | _ -> Assert.Fail("Expected a structured platform failure.")
        finally
            provider.Dispose()

    [<TestMethod>]
    member _.``Local provider propagates caller cancellation``() =
        let profile = descriptor "ollama" "local-model-v1" 2

        let handler =
            new EmbeddingStubHttpMessageHandler(fun _ cancellationToken ->
                task {
                    do! Task.Delay(Timeout.Infinite, cancellationToken)
                    return response HttpStatusCode.OK "{}"
                })

        let config: OllamaEmbeddingConfig =
            { BaseUrl = "http://localhost:11434"
              Model = "local-model-v1"
              Descriptor = profile
              CountTokens = fun _ -> 1
              TimeoutSeconds = None }

        let provider =
            OllamaEmbeddingProvider.createWithHandler config (Some handler) (fun () -> now)

        try
            use cancellation = new CancellationTokenSource()
            cancellation.CancelAfter(TimeSpan.FromMilliseconds 20.0)

            Assert.ThrowsExactly<OperationCanceledException>(fun () ->
                invoke provider [ input 0 "cancel me" ] cancellation.Token |> ignore)
            |> ignore
        finally
            provider.Dispose()

    [<TestMethod>]
    member _.``Hosted credentials require a secret provider``() =
        let profile = descriptor "openai-compatible" "hosted-model-v1" 2

        let config: OpenAICompatibleEmbeddingConfig =
            { Endpoint = "https://embedding.example/v1/embeddings"
              ApiKeyReference = Some(SecretReference.parse "providers/openai/embedding-key")
              Model = "hosted-model-v1"
              Descriptor = profile
              CountTokens = fun _ -> 1
              CostPerMillionInputTokensUsd = 0m
              TimeoutSeconds = None }

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            OpenAICompatibleEmbeddingProvider.createWithHandler config None None (fun () -> now)
            |> ignore)
        |> ignore
