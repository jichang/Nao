module Nao.Providers.OllamaProvider

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Nao.Agents

/// Functional provider factory for an Ollama server's OpenAI-compatible API.
let create (config: OllamaConfig) : LlmProvider =
    let client = new HttpClient(BaseAddress = Uri(config.BaseUrl))

    do
        config.TimeoutSeconds
        |> Option.iter (fun seconds -> client.Timeout <- TimeSpan.FromSeconds(float seconds))

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
            config.Model
            messages
            options.Temperature
            streaming
            options.MaxTokens
            (List.toArray options.StopSequences)
            (Option.toObj config.ReasoningEffort)

    // Parse one streamed SSE JSON object into (text delta, finish reason, total tokens).
    // Any of the three may be absent in a given chunk (deltas carry text; the penultimate
    // chunk carries finish_reason; an optional trailing chunk carries usage).
    let parseStreamChunk (json: string) : (string * string option * int option) =
        let response = OpenAIChatDto.deserializeResponse json

        if isNull response then
            raise (JsonException("The response body must be a JSON object."))

        let delta, finish =
            if isNull response.Choices || response.Choices.Length = 0 then
                "", None
            else
                let choice = response.Choices.[0]

                let deltaText =
                    if isNull choice.Delta || isNull choice.Delta.Content then
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
            if isNull response.Usage then
                None
            elif response.Usage.TotalTokens.HasValue then
                Some response.Usage.TotalTokens.Value
            else
                raise (JsonException("usage.total_tokens is required when usage is present."))

        delta, finish, tokens

    let parseResponse (json: string) : CompletionResult =
        try
            let response = OpenAIChatDto.deserializeResponse json

            if isNull response then
                raise (JsonException("The response body must be a JSON object."))

            if isNull response.Choices || response.Choices.Length = 0 then
                raise (JsonException("choices must be a non-empty array."))

            let choice = response.Choices.[0]

            if isNull choice.Message || isNull choice.Message.Content then
                raise (JsonException("choices[0].message.content must be a string."))

            if String.IsNullOrWhiteSpace choice.FinishReason then
                raise (JsonException("choices[0].finish_reason must be a non-empty string."))

            let totalTokens =
                if isNull response.Usage then
                    None
                elif response.Usage.TotalTokens.HasValue then
                    Some response.Usage.TotalTokens.Value
                else
                    raise (JsonException("usage.total_tokens is required when usage is present."))

            { Content = choice.Message.Content
              FinishReason = choice.FinishReason
              TokensUsed = totalTokens
              Usage = None }
        with ex ->
            PlatformFailure.fromException PlatformFailureBoundary.Provider None ex
            |> PlatformFailure.raiseException

    let providerName () = sprintf "Ollama(%s)" config.Model

    let completeAsync (conversation: Conversation) (options: CompletionOptions) : Task<CompletionResult> =
        task {
            try
                let body = buildRequestBody conversation options false
                let content = new StringContent(body, Encoding.UTF8, "application/json")

                let! response = client.PostAsync("/v1/chat/completions", content)
                let! responseBody = response.Content.ReadAsStringAsync()

                if not response.IsSuccessStatusCode then
                    return
                        PlatformFailure.fromHttpStatus
                            None
                            (int response.StatusCode)
                            (sprintf "Provider returned HTTP %d: %s" (int response.StatusCode) responseBody)
                        |> PlatformFailure.raiseException
                else
                    return parseResponse responseBody
            with ex ->
                return
                    PlatformFailure.fromException PlatformFailureBoundary.Provider None ex
                    |> PlatformFailure.raiseException
        }

    let streamAsync
        (conversation: Conversation)
        (options: CompletionOptions)
        (onChunk: CompletionChunk -> unit)
        : Task<CompletionResult> =
        task {
            try
                let body = buildRequestBody conversation options true
                use request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
                request.Content <- new StringContent(body, Encoding.UTF8, "application/json")

                // ResponseHeadersRead so we start reading the body as it streams rather
                // than waiting for the whole response to buffer.
                let! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)

                if not response.IsSuccessStatusCode then
                    let! errorBody = response.Content.ReadAsStringAsync()

                    return
                        PlatformFailure.fromHttpStatus
                            None
                            (int response.StatusCode)
                            (sprintf "Provider returned HTTP %d: %s" (int response.StatusCode) errorBody)
                        |> PlatformFailure.raiseException
                else
                    use! responseStream = response.Content.ReadAsStreamAsync()
                    use reader = new System.IO.StreamReader(responseStream)
                    let builder = StringBuilder()
                    let mutable finishReason = None
                    let mutable tokens = None
                    let mutable reading = true

                    while reading do
                        let! line = reader.ReadLineAsync()

                        if isNull line then
                            reading <- false
                        else
                            let trimmed = line.Trim()

                            if trimmed.StartsWith("data:") then
                                let payload = trimmed.Substring(5).Trim()

                                if payload = "[DONE]" then
                                    reading <- false
                                elif payload <> "" then
                                    let delta, finish, tk = parseStreamChunk payload

                                    match finish with
                                    | Some reason -> finishReason <- Some reason
                                    | None -> ()

                                    match tk with
                                    | Some _ -> tokens <- tk
                                    | None -> ()

                                    if delta <> "" then
                                        builder.Append(delta) |> ignore

                                        onChunk
                                            { Delta = delta
                                              FinishReason = None
                                              TokensUsed = None
                                              Usage = None }

                    let finishReason =
                        finishReason
                        |> Option.defaultWith (fun () ->
                            raise (JsonException("The stream ended without a finish_reason.")))

                    onChunk
                        { Delta = ""
                          FinishReason = Some finishReason
                          TokensUsed = tokens
                          Usage = None }

                    return
                        { Content = builder.ToString()
                          FinishReason = finishReason
                          TokensUsed = tokens
                          Usage = None }
            with ex ->
                return
                    PlatformFailure.fromException PlatformFailureBoundary.Provider None ex
                    |> PlatformFailure.raiseException
        }

    { Name = providerName
      CompleteAsync = completeAsync
      StreamAsync = Some streamAsync
      Dispose = client.Dispose }
