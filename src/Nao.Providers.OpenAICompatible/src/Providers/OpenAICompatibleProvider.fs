module Nao.Providers.OpenAICompatibleProvider

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Nao.Agents

/// Functional provider factory for any server that speaks the OpenAI-compatible chat completions API.
/// The configured URL must be the complete endpoint and is used without modification.
let createWithHandler
    name
    baseUrl
    model
    (apiKey: SecretValue option)
    timeoutSeconds
    (httpHandler: HttpMessageHandler option)
    : LlmProvider =
    let chatUrl =
        match Uri.TryCreate(baseUrl, UriKind.Absolute) with
        | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps -> uri
        | _ -> invalidArg (nameof baseUrl) "The OpenAI-compatible URL must be an absolute HTTP or HTTPS endpoint."

    let client = new HttpClient(defaultArg httpHandler (new HttpClientHandler()))

    do
        timeoutSeconds
        |> Option.iter (fun seconds -> client.Timeout <- TimeSpan.FromSeconds(float seconds))

    do
        match apiKey with
        | Some key ->
            SecretValue.useValue
                (fun value -> client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", value))
                key
        | _ -> ()

    let roleToString (role: Role) =
        match role with
        | System -> "system"
        | User -> "user"
        | Assistant -> "assistant"

    let buildRequestBody (conversation: Conversation) (options: CompletionOptions) (streaming: bool) =
        let messages =
            conversation
            |> List.map (fun message -> roleToString message.Role, message.Content)
            |> List.toArray

        OpenAIChatDto.serializeRequest
            model
            messages
            options.Temperature
            streaming
            options.MaxTokens
            (List.toArray options.StopSequences)
            null

    // Parse one streamed SSE JSON object into (text delta, finish reason, total tokens).
    // Any of the three may be absent in a given chunk (deltas carry text; the penultimate
    // chunk carries finish_reason; an optional trailing chunk carries usage).
    let extensions (response: OpenAIChatResponseDto) =
        [ "openai.id", response.Id; "openai.model", response.Model ]
        |> List.choose (fun (key, value) ->
            if String.IsNullOrWhiteSpace value then
                None
            else
                Some(key, value))
        |> Map.ofList

    let parseStreamChunk (json: string) : (string * string option * int option * Map<string, string>) =
        let response = OpenAIChatDto.deserializeResponse json

        if isNull (box response) then
            raise (JsonException("The response body must be a JSON object."))

        let delta, finish =
            if isNull response.Choices || response.Choices.Length = 0 then
                "", None
            else
                let choice = response.Choices.[0]

                let deltaText =
                    if isNull (box choice.Delta) || isNull choice.Delta.Content then
                        ""
                    else
                        choice.Delta.Content

                let finishReason =
                    if isNull choice.FinishReason then
                        None
                    elif String.IsNullOrWhiteSpace choice.FinishReason then
                        raise (JsonException("choices[0].finish_reason must be a non-empty string or null."))
                    else
                        Some choice.FinishReason

                deltaText, finishReason

        let tokens =
            if isNull (box response.Usage) then
                None
            elif response.Usage.TotalTokens.HasValue then
                Some response.Usage.TotalTokens.Value
            else
                raise (JsonException("usage.total_tokens is required when usage is present."))

        delta, finish, tokens, extensions response

    let parseResponse (json: string) : CompletionResult =
        try
            let response = OpenAIChatDto.deserializeResponse json

            if isNull (box response) then
                raise (JsonException("The response body must be a JSON object."))

            if isNull response.Choices || response.Choices.Length = 0 then
                raise (JsonException("choices must be a non-empty array."))

            let choice = response.Choices.[0]

            if isNull (box choice.Message) || isNull choice.Message.Content then
                raise (JsonException("choices[0].message.content must be a string."))

            if String.IsNullOrWhiteSpace choice.FinishReason then
                raise (JsonException("choices[0].finish_reason must be a non-empty string."))

            let totalTokens =
                if isNull (box response.Usage) then
                    None
                elif response.Usage.TotalTokens.HasValue then
                    Some response.Usage.TotalTokens.Value
                else
                    raise (JsonException("usage.total_tokens is required when usage is present."))

            { Content = choice.Message.Content
              FinishReason = ProviderFinishReason.parse choice.FinishReason
              Usage =
                totalTokens
                |> Option.map ProviderUsage.aggregate
                |> Option.defaultValue ProviderUsage.None
              ToolCalls = []
              StructuredOutputJson = None
              Extensions = extensions response }
        with ex ->
            PlatformFailure.fromException PlatformFailureBoundary.Provider None ex
            |> PlatformFailure.raiseException

    let providerName () = sprintf "%s(%s)" name model

    let completeAsync
        (_correlation: CorrelationContext)
        (conversation: Conversation)
        (options: CompletionOptions)
        (cancellationToken: CancellationToken)
        : Task<CompletionResult> =
        task {
            try
                let body = buildRequestBody conversation options false
                let content = new StringContent(body, Encoding.UTF8, "application/json")

                let! response = client.PostAsync(chatUrl, content, cancellationToken)

                if not response.IsSuccessStatusCode then
                    return
                        PlatformFailure.fromHttpStatus
                            None
                            (int response.StatusCode)
                            (sprintf "Provider returned HTTP %d." (int response.StatusCode))
                        |> PlatformFailure.raiseException
                else
                    let! responseBody = response.Content.ReadAsStringAsync(cancellationToken)
                    return parseResponse responseBody
            with
            | :? OperationCanceledException as error when cancellationToken.IsCancellationRequested ->
                return raise error
            | ex ->
                return
                    PlatformFailure.fromException PlatformFailureBoundary.Provider None ex
                    |> PlatformFailure.raiseException
        }

    let streamAsync
        (_correlation: CorrelationContext)
        (conversation: Conversation)
        (options: CompletionOptions)
        (onChunk: CompletionChunk -> unit)
        (cancellationToken: CancellationToken)
        : Task<CompletionResult> =
        task {
            let mutable emitted = false

            try
                let body = buildRequestBody conversation options true
                use request = new HttpRequestMessage(HttpMethod.Post, chatUrl)
                request.Content <- new StringContent(body, Encoding.UTF8, "application/json")

                // ResponseHeadersRead so we start reading the body as it streams rather
                // than waiting for the whole response to buffer.
                let! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)

                if not response.IsSuccessStatusCode then
                    return
                        PlatformFailure.fromHttpStatus
                            None
                            (int response.StatusCode)
                            (sprintf "Provider returned HTTP %d." (int response.StatusCode))
                        |> PlatformFailure.raiseException
                else
                    use! responseStream = response.Content.ReadAsStreamAsync(cancellationToken)
                    use reader = new System.IO.StreamReader(responseStream)
                    let builder = StringBuilder()
                    let mutable finishReason = None
                    let mutable tokens = None
                    let mutable reading = true
                    let mutable responseExtensions = Map.empty

                    while reading do
                        let! line = reader.ReadLineAsync(cancellationToken)

                        if isNull line then
                            reading <- false
                        else
                            let trimmed = line.Trim()

                            if trimmed.StartsWith("data:") then
                                let payload = trimmed.Substring(5).Trim()

                                if payload = "[DONE]" then
                                    reading <- false
                                elif payload <> "" then
                                    let delta, finish, tk, chunkExtensions = parseStreamChunk payload

                                    responseExtensions <-
                                        Map.fold
                                            (fun state key value -> Map.add key value state)
                                            responseExtensions
                                            chunkExtensions

                                    match finish with
                                    | Some reason -> finishReason <- Some reason
                                    | None -> ()

                                    match tk with
                                    | Some _ -> tokens <- tk
                                    | None -> ()

                                    if delta <> "" then
                                        emitted <- true
                                        builder.Append(delta) |> ignore

                                        onChunk
                                            { Delta = delta
                                              Termination = None
                                              Usage = ProviderUsage.None
                                              ToolCalls = []
                                              StructuredOutputJson = None
                                              Extensions = chunkExtensions }

                    let finishReason =
                        finishReason
                        |> Option.defaultWith (fun () ->
                            raise (JsonException("The stream ended without a finish_reason.")))
                        |> ProviderFinishReason.parse

                    let usage =
                        tokens
                        |> Option.map ProviderUsage.aggregate
                        |> Option.defaultValue ProviderUsage.None

                    onChunk
                        { Delta = ""
                          Termination = Some(ProviderStreamTermination.Completed finishReason)
                          Usage = usage
                          ToolCalls = []
                          StructuredOutputJson = None
                          Extensions = responseExtensions }

                    return
                        { Content = builder.ToString()
                          FinishReason = finishReason
                          Usage = usage
                          ToolCalls = []
                          StructuredOutputJson = None
                          Extensions = responseExtensions }
            with
            | :? OperationCanceledException as error when cancellationToken.IsCancellationRequested ->
                return raise error
            | ex ->
                let failure = PlatformFailure.fromException PlatformFailureBoundary.Provider None ex

                if emitted then
                    onChunk (CompletionChunk.partialFailure failure)

                return failure |> PlatformFailure.raiseException
        }

    { Name = providerName
      CompleteAsync = completeAsync
      StreamAsync = Some streamAsync
      Dispose = client.Dispose }

let create name baseUrl model apiKey timeoutSeconds =
    createWithHandler name baseUrl model apiKey timeoutSeconds None
