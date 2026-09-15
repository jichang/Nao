module Nao.Providers.OpenAICompatibleEmbeddingProvider

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open Nao.Agents
open Nao.Knowledge

let private endpoint value =
    match Uri.TryCreate(value, UriKind.Absolute) with
    | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps -> uri
    | _ -> invalidArg (nameof value) "The embedding endpoint must be an absolute HTTP or HTTPS URL."

let private validateConfig (config: OpenAICompatibleEmbeddingConfig) =
    if isNull (box config) then
        nullArg (nameof config)

    if String.IsNullOrWhiteSpace config.Model || config.Model <> config.Model.Trim() then
        invalidArg (nameof config.Model) "The embedding model must be non-blank and have no surrounding whitespace."

    let descriptor = EmbeddingCapabilityDescriptor.validate config.Descriptor

    if descriptor.Model.Version <> config.Model then
        invalidArg (nameof config.Model) "The requested model must match the embedding profile model version."

    if isNull (box config.CountTokens) then
        nullArg (nameof config.CountTokens)

    if config.CostPerMillionInputTokensUsd < 0m then
        invalidArg (nameof config.CostPerMillionInputTokensUsd) "Embedding input-token cost cannot be negative."

    config.TimeoutSeconds
    |> Option.iter (fun seconds ->
        if seconds <= 0 then
            invalidArg (nameof config.TimeoutSeconds) "Embedding provider timeout must be positive.")

    endpoint config.Endpoint, descriptor

let createWithHandler
    (config: OpenAICompatibleEmbeddingConfig)
    (secretProvider: SecretProvider option)
    (httpHandler: HttpMessageHandler option)
    (clock: unit -> DateTimeOffset)
    : KnowledgeEmbeddingProvider =
    let endpoint, descriptor = validateConfig config

    if isNull (box clock) then
        nullArg (nameof clock)

    match config.ApiKeyReference, secretProvider with
    | Some _, None -> invalidArg (nameof secretProvider) "A secret provider is required for the configured API key."
    | _ -> ()

    let client = new HttpClient(defaultArg httpHandler (new HttpClientHandler()))

    config.TimeoutSeconds
    |> Option.iter (fun seconds -> client.Timeout <- TimeSpan.FromSeconds(float seconds))

    let parseResponse (batch: EmbeddingProviderBatch) (responseBody: string) : EmbeddingProviderResponse =
        let response: OpenAIEmbeddingResponseDto =
            OpenAIEmbeddingDto.deserializeResponse responseBody

        if isNull response || isNull response.Data then
            raise (JsonException "Embedding response data must be an array.")

        if response.Model <> config.Model then
            raise (JsonException "Embedding response model does not match the requested profile.")

        if response.Data.Length <> batch.Inputs.Length then
            raise (JsonException "Embedding response data count does not match the request.")

        let byIndex =
            response.Data
            |> Array.map (fun (item: OpenAIEmbeddingDataDto) ->
                if
                    isNull item
                    || item.Index < 0
                    || item.Index >= batch.Inputs.Length
                    || isNull item.Embedding
                then
                    raise (JsonException "Embedding response contains an invalid indexed vector.")

                item.Index, item.Embedding)
            |> Map.ofArray

        if byIndex.Count <> response.Data.Length then
            raise (JsonException "Embedding response contains duplicate indexes.")

        if isNull response.Usage || not response.Usage.PromptTokens.HasValue then
            raise (JsonException "Embedding response usage.prompt_tokens is required.")

        let inputTokens = response.Usage.PromptTokens.Value

        if inputTokens < 0 then
            raise (JsonException "Embedding response input-token usage cannot be negative.")

        { Embeddings =
            batch.Inputs
            |> List.mapi (fun index input ->
                { ChunkId = input.ChunkId
                  ContentHash = input.ContentHash
                  ProfileId = descriptor.ProfileId
                  Values = byIndex[index]
                  GeneratedAt = clock () })
          Usage =
            { InputTokens = inputTokens
              EstimatedCostUsd = decimal inputTokens * config.CostPerMillionInputTokensUsd / 1_000_000m } }

    let embedBatchAsync
        (authorization: AuthorizationScope)
        (batch: EmbeddingProviderBatch)
        (cancellationToken: CancellationToken)
        =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            try
                use request = new HttpRequestMessage(HttpMethod.Post, endpoint)

                match config.ApiKeyReference, secretProvider with
                | Some reference, Some provider ->
                    let! resolution = SecretProvider.resolveAsync authorization reference cancellationToken provider

                    SecretResolution.value resolution
                    |> SecretValue.useValue (fun value ->
                        request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", value))
                | None, _ -> ()
                | Some _, None -> invalidOp "The configured embedding credential provider is unavailable."

                let body =
                    batch.Inputs
                    |> List.map _.Text
                    |> List.toArray
                    |> OpenAIEmbeddingDto.serializeRequest config.Model

                request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)

                if not response.IsSuccessStatusCode then
                    return
                        PlatformFailure.fromHttpStatus
                            None
                            (int response.StatusCode)
                            $"Embedding provider returned HTTP {int response.StatusCode}."
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

let create config secretProvider clock =
    createWithHandler config secretProvider None clock
