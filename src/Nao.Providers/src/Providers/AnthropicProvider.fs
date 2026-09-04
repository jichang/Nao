module Nao.Providers.AnthropicProvider

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Nao.Agents

/// Functional provider factory for Anthropic's native Messages API.
let createWithHandler (config: AnthropicConfig) (httpHandler: HttpMessageHandler option) : LlmProvider =
    let client = new HttpClient(defaultArg httpHandler (new HttpClientHandler()))

    do
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01")

        if not (String.IsNullOrWhiteSpace config.ApiKey) then
            client.DefaultRequestHeaders.Add("x-api-key", config.ApiKey)

        config.TimeoutSeconds
        |> Option.iter (fun seconds -> client.Timeout <- TimeSpan.FromSeconds(float seconds))

    let messagesUrl = config.BaseUrl.TrimEnd('/') + "/v1/messages"

    let finishReason reason =
        match reason with
        | "max_tokens" -> "length"
        | "tool_use" -> "tool_call"
        | "end_turn"
        | "stop_sequence" -> "stop"
        | value when String.IsNullOrWhiteSpace value -> raise (JsonException("stop_reason must be a non-empty string."))
        | value -> value

    let buildRequestBody (conversation: Conversation) (options: CompletionOptions) streaming =
        let systemPrompt =
            conversation
            |> List.choose (fun message ->
                if message.Role = Role.System then
                    Some message.Content
                else
                    None)
            |> String.concat "\n\n"

        let messages =
            conversation
            |> List.choose (fun message ->
                if message.Role = Role.System then
                    None
                else
                    Some((if message.Role = Role.User then "user" else "assistant"), message.Content))
            |> List.toArray

        AnthropicDto.serializeRequest
            config.Model
            (options.MaxTokens |> Option.defaultValue 4096)
            options.Temperature
            streaming
            (if String.IsNullOrWhiteSpace systemPrompt then
                 null
             else
                 systemPrompt)
            messages
            (List.toArray options.StopSequences)

    let responseTokens (usage: AnthropicUsageDto) =
        if
            isNull usage
            || not usage.InputTokens.HasValue
            || not usage.OutputTokens.HasValue
        then
            raise (JsonException("usage.input_tokens and usage.output_tokens are required."))

        usage.InputTokens.Value + usage.OutputTokens.Value

    let parseResponse (json: string) =
        try
            let response = AnthropicDto.deserializeResponse json

            if isNull response || isNull response.Content then
                raise (JsonException("content must be an array."))

            let content =
                response.Content
                |> Array.choose (fun block ->
                    if isNull block || String.IsNullOrWhiteSpace block.Type then
                        raise (JsonException("content block type is required."))
                    elif block.Type = "text" then
                        if isNull block.Text then
                            raise (JsonException("text content blocks require text."))

                        Some block.Text
                    else
                        None)
                |> String.concat ""

            { Content = content
              FinishReason = finishReason response.StopReason
              TokensUsed = Some(responseTokens response.Usage)
              Usage = None }
        with ex ->
            PlatformFailure.fromException PlatformFailureBoundary.Provider None ex
            |> PlatformFailure.raiseException

    let parseStreamEvent (json: string) =
        let event = AnthropicDto.deserializeStreamEvent json

        if isNull event || String.IsNullOrWhiteSpace event.Type then
            raise (JsonException("Stream event type is required."))

        match event.Type with
        | "message_start" ->
            if
                isNull event.Message
                || isNull event.Message.Usage
                || not event.Message.Usage.InputTokens.HasValue
            then
                raise (JsonException("message_start requires message.usage.input_tokens."))

            "", None, Some event.Message.Usage.InputTokens.Value, None
        | "content_block_delta" ->
            if
                isNull event.Delta
                || event.Delta.Type <> "text_delta"
                || isNull event.Delta.Text
            then
                raise (JsonException("content_block_delta requires a text_delta with text."))

            event.Delta.Text, None, None, None
        | "message_delta" ->
            if
                isNull event.Delta
                || isNull event.Usage
                || not event.Usage.OutputTokens.HasValue
            then
                raise (JsonException("message_delta requires delta.stop_reason and usage.output_tokens."))

            "", Some(finishReason event.Delta.StopReason), None, Some event.Usage.OutputTokens.Value
        | _ -> "", None, None, None

    let providerName () = sprintf "Anthropic(%s)" config.Model

    let completeAsync conversation options : Task<CompletionResult> =
        task {
            try
                use content =
                    new StringContent(buildRequestBody conversation options false, Encoding.UTF8, "application/json")

                let! response = client.PostAsync(messagesUrl, content)
                let! responseBody = response.Content.ReadAsStringAsync()

                if response.IsSuccessStatusCode then
                    return parseResponse responseBody
                else
                    return
                        PlatformFailure.fromHttpStatus
                            None
                            (int response.StatusCode)
                            (sprintf "Provider returned HTTP %d: %s" (int response.StatusCode) responseBody)
                        |> PlatformFailure.raiseException
            with ex ->
                return
                    PlatformFailure.fromException PlatformFailureBoundary.Provider None ex
                    |> PlatformFailure.raiseException
        }

    let streamAsync conversation options onChunk : Task<CompletionResult> =
        task {
            try
                use request = new HttpRequestMessage(HttpMethod.Post, messagesUrl)

                request.Content <-
                    new StringContent(buildRequestBody conversation options true, Encoding.UTF8, "application/json")

                let! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)

                if not response.IsSuccessStatusCode then
                    let! responseBody = response.Content.ReadAsStringAsync()

                    return
                        PlatformFailure.fromHttpStatus
                            None
                            (int response.StatusCode)
                            (sprintf "Provider returned HTTP %d: %s" (int response.StatusCode) responseBody)
                        |> PlatformFailure.raiseException
                else
                    use! responseStream = response.Content.ReadAsStreamAsync()
                    use reader = new System.IO.StreamReader(responseStream)
                    let content = StringBuilder()
                    let mutable reason = "stop"
                    let mutable inputTokens = 0
                    let mutable outputTokens = 0
                    let mutable reading = true

                    while reading do
                        let! line = reader.ReadLineAsync()

                        if isNull line then
                            reading <- false
                        else
                            let trimmed = line.Trim()

                            if trimmed.StartsWith("data:") then
                                let delta, eventReason, eventInputTokens, eventOutputTokens =
                                    parseStreamEvent (trimmed.Substring(5).Trim())

                                eventReason |> Option.iter (fun value -> reason <- value)
                                eventInputTokens |> Option.iter (fun value -> inputTokens <- value)
                                eventOutputTokens |> Option.iter (fun value -> outputTokens <- value)

                                if delta <> "" then
                                    content.Append(delta) |> ignore

                                    onChunk
                                        { Delta = delta
                                          FinishReason = None
                                          TokensUsed = None
                                          Usage = None }

                    let tokens = Some(inputTokens + outputTokens)

                    onChunk
                        { Delta = ""
                          FinishReason = Some reason
                          TokensUsed = tokens
                          Usage = None }

                    return
                        { Content = content.ToString()
                          FinishReason = reason
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

let create config = createWithHandler config None
