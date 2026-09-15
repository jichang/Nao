module Nao.Providers.OllamaEmbeddingProvider

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open Nao.Agents
open Nao.Knowledge

[<AllowNullLiteral>]
type OllamaEmbeddingRequestDto() =
    [<JsonPropertyName("model")>]
    member val Model: string = null with get, set

    [<JsonPropertyName("input")>]
    member val Input: string array = null with get, set

    [<JsonPropertyName("truncate")>]
    member val Truncate = false with get, set

[<AllowNullLiteral>]
type OllamaEmbeddingResponseDto() =
    [<JsonPropertyName("model")>]
    member val Model: string = null with get, set

    [<JsonPropertyName("embeddings")>]
    member val Embeddings: float array array = null with get, set

    [<JsonPropertyName("prompt_eval_count")>]
    member val PromptEvalCount = Nullable<int>() with get, set

let private validateConfig (config: OllamaEmbeddingConfig) =
    if isNull (box config) then
        nullArg (nameof config)

    let baseUri =
        match Uri.TryCreate(config.BaseUrl, UriKind.Absolute) with
        | true, uri when
            (uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps)
            && (uri.AbsolutePath = "/" || String.IsNullOrEmpty uri.AbsolutePath)
            ->
            uri
        | _ -> invalidArg (nameof config.BaseUrl) "The Ollama base URL must be an absolute HTTP(S) origin."

    if String.IsNullOrWhiteSpace config.Model || config.Model <> config.Model.Trim() then
        invalidArg (nameof config.Model) "The embedding model must be non-blank and have no surrounding whitespace."

    let descriptor = EmbeddingCapabilityDescriptor.validate config.Descriptor

    if descriptor.Model.Version <> config.Model then
        invalidArg (nameof config.Model) "The requested model must match the embedding profile model version."

    if isNull (box config.CountTokens) then
        nullArg (nameof config.CountTokens)

    config.TimeoutSeconds
    |> Option.iter (fun seconds ->
        if seconds <= 0 then
            invalidArg (nameof config.TimeoutSeconds) "Embedding provider timeout must be positive.")

    baseUri, descriptor

let createWithHandler
    (config: OllamaEmbeddingConfig)
    (httpHandler: HttpMessageHandler option)
    (clock: unit -> DateTimeOffset)
    : KnowledgeEmbeddingProvider =
    let baseUri, descriptor = validateConfig config

    if isNull (box clock) then
        nullArg (nameof clock)

    let endpoint = Uri(baseUri, "/api/embed")
    let client = new HttpClient(defaultArg httpHandler (new HttpClientHandler()))

    config.TimeoutSeconds
    |> Option.iter (fun seconds -> client.Timeout <- TimeSpan.FromSeconds(float seconds))

    let parseResponse (batch: EmbeddingProviderBatch) (responseBody: string) : EmbeddingProviderResponse =
        let response = JsonSerializer.Deserialize<OllamaEmbeddingResponseDto>(responseBody)

        if isNull response || isNull response.Embeddings then
            raise (JsonException "Ollama embedding response embeddings must be an array.")

        if response.Model <> config.Model then
            raise (JsonException "Ollama embedding response model does not match the requested profile.")

        if response.Embeddings.Length <> batch.Inputs.Length then
            raise (JsonException "Ollama embedding response count does not match the request.")

        if not response.PromptEvalCount.HasValue || response.PromptEvalCount.Value < 0 then
            raise (JsonException "Ollama embedding response prompt_eval_count is required and non-negative.")

        { Embeddings =
            List.map2
                (fun (input: EmbeddingInput) values ->
                    if isNull values then
                        raise (JsonException "Ollama embedding response contains a null vector.")

                    { ChunkId = input.ChunkId
                      ContentHash = input.ContentHash
                      ProfileId = descriptor.ProfileId
                      Values = values
                      GeneratedAt = clock () })
                batch.Inputs
                (List.ofArray response.Embeddings)
          Usage =
            { InputTokens = response.PromptEvalCount.Value
              EstimatedCostUsd = 0m } }

    let embedBatchAsync
        (_authorization: AuthorizationScope)
        (batch: EmbeddingProviderBatch)
        (cancellationToken: CancellationToken)
        =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            try
                let requestDto = OllamaEmbeddingRequestDto()
                requestDto.Model <- config.Model
                requestDto.Input <- batch.Inputs |> List.map _.Text |> List.toArray
                requestDto.Truncate <- false
                let body = JsonSerializer.Serialize requestDto
                use request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)

                if not response.IsSuccessStatusCode then
                    return
                        PlatformFailure.fromHttpStatus
                            None
                            (int response.StatusCode)
                            $"Ollama embedding provider returned HTTP {int response.StatusCode}."
                        |> PlatformFailure.raiseException
                else
                    let! responseBody = response.Content.ReadAsStringAsync(cancellationToken)
                    return parseResponse batch responseBody
            with
            | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                return raise (OperationCanceledException(cancellationToken))
            | error ->
                return
                    PlatformFailure.fromException PlatformFailureBoundary.Provider None error
                    |> PlatformFailure.raiseException
        }

    { Descriptor = descriptor
      CountTokens = config.CountTokens
      EmbedBatchAsync = embedBatchAsync
      Dispose = client.Dispose }

let create config clock = createWithHandler config None clock
