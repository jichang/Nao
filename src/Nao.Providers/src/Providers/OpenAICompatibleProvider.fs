namespace Nao.Providers

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Nao.Agents

/// LLM provider for any server that speaks the OpenAI-compatible chat completions API.
/// The configured URL must be the complete endpoint and is used without modification.
type OpenAICompatibleProvider(name: string, baseUrl: string, model: string, apiKey: string option, ?timeoutSeconds: int, ?httpHandler: HttpMessageHandler) =
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
        | Some key when not (String.IsNullOrWhiteSpace key) ->
            client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", key)
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
            model messages options.Temperature streaming options.MaxTokens
            (List.toArray options.StopSequences) null

    let tokenUsage (usage: OpenAIUsageDto) =
        if isNull usage then None
        elif usage.PromptTokens.HasValue && usage.CompletionTokens.HasValue then
            Some { InputTokens = usage.PromptTokens.Value; OutputTokens = usage.CompletionTokens.Value }
        else None

    // Parse one streamed SSE JSON object into text, finish reason, total tokens, and split usage.
    // Any of the three may be absent in a given chunk (deltas carry text; the penultimate
    // chunk carries finish_reason; an optional trailing chunk carries usage).
    let parseStreamChunk (json: string) : (string * string option * int option * TokenUsage option) =
        let response = OpenAIChatDto.deserializeResponse json
        if isNull (box response) then
            raise (JsonException("The response body must be a JSON object."))
        let delta, finish =
            if isNull response.Choices || response.Choices.Length = 0 then
                "", None
            else
                let choice = response.Choices.[0]
                let deltaText =
                    if isNull (box choice.Delta) || isNull choice.Delta.Content then ""
                    else choice.Delta.Content
                let finishReason =
                    if isNull choice.FinishReason then None
                    elif String.IsNullOrWhiteSpace choice.FinishReason then
                        raise (JsonException("choices[0].finish_reason must be a non-empty string or null."))
                    else Some choice.FinishReason
                deltaText, finishReason
        let tokens =
            if isNull (box response.Usage) then None
            elif response.Usage.TotalTokens.HasValue then Some response.Usage.TotalTokens.Value
            else raise (JsonException("usage.total_tokens is required when usage is present."))
        delta, finish, tokens, tokenUsage response.Usage

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
                if isNull (box response.Usage) then None
                elif response.Usage.TotalTokens.HasValue then Some response.Usage.TotalTokens.Value
                else raise (JsonException("usage.total_tokens is required when usage is present."))

            CompletionResult.create choice.Message.Content choice.FinishReason totalTokens (tokenUsage response.Usage)
        with ex ->
            CompletionResult.create (sprintf "Parse error: %s" ex.Message) "error" None None

    interface ILlmProvider with
        member _.Name = sprintf "%s(%s)" name model

        member _.CompleteAsync (conversation: Conversation) (options: CompletionOptions) : Task<CompletionResult> =
            task {
                try
                    let body = buildRequestBody conversation options false
                    let content = new StringContent(body, Encoding.UTF8, "application/json")

                    let! response = client.PostAsync(chatUrl, content)
                    let! responseBody = response.Content.ReadAsStringAsync()

                    if not response.IsSuccessStatusCode then
                        return CompletionResult.create (sprintf "Error: %d - %s" (int response.StatusCode) responseBody) "error" None None
                    else
                        return parseResponse responseBody
                with ex ->
                    // A connection failure (server down/unreachable) must not crash the
                    // caller — surface it as an error result instead.
                    return CompletionResult.create (sprintf "Error: %s" ex.Message) "error" None None
            }

    interface IStreamingLlmProvider with
        member _.StreamAsync (conversation: Conversation) (options: CompletionOptions) (onChunk: CompletionChunk -> unit) : Task<CompletionResult> =
            task {
                try
                    let body = buildRequestBody conversation options true
                    use request = new HttpRequestMessage(HttpMethod.Post, chatUrl)
                    request.Content <- new StringContent(body, Encoding.UTF8, "application/json")

                    // ResponseHeadersRead so we start reading the body as it streams rather
                    // than waiting for the whole response to buffer.
                    let! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                    if not response.IsSuccessStatusCode then
                        let! errorBody = response.Content.ReadAsStringAsync()
                        return CompletionResult.create (sprintf "Error: %d - %s" (int response.StatusCode) errorBody) "error" None None
                    else
                        use! responseStream = response.Content.ReadAsStreamAsync()
                        use reader = new System.IO.StreamReader(responseStream)
                        let builder = StringBuilder()
                        let mutable finishReason = None
                        let mutable tokens = None
                        let mutable usage = None
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
                                        let delta, finish, tk, reportedUsage = parseStreamChunk payload
                                        match finish with Some reason -> finishReason <- Some reason | None -> ()
                                        match tk with Some _ -> tokens <- tk | None -> ()
                                        match reportedUsage with Some _ -> usage <- reportedUsage | None -> ()
                                        if delta <> "" then
                                            builder.Append(delta) |> ignore
                                            onChunk (CompletionChunk.create delta None None None)
                        let finishReason =
                            finishReason
                            |> Option.defaultWith (fun () -> raise (JsonException("The stream ended without a finish_reason.")))
                        onChunk (CompletionChunk.create "" (Some finishReason) tokens usage)
                        return CompletionResult.create (builder.ToString()) finishReason tokens usage
                with ex ->
                    return CompletionResult.create (sprintf "Error: %s" ex.Message) "error" None None
            }

    interface IDisposable with
        member _.Dispose() = client.Dispose()
