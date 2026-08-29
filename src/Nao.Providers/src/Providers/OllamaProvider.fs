namespace Nao.Providers

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Nao.Agents

/// LLM provider that connects to an Ollama server via its OpenAI-compatible API.
/// Ollama exposes /v1/chat/completions for chat-style completions.
type OllamaProvider(config: OllamaConfig, ?httpHandler: HttpMessageHandler) =
    let client =
        match httpHandler with
        | Some handler -> new HttpClient(handler, true)
        | None -> new HttpClient()

    do
        client.BaseAddress <- Uri(config.BaseUrl)
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
            config.Model messages options.Temperature streaming options.MaxTokens
            (List.toArray options.StopSequences) (Option.toObj config.ReasoningEffort)

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
        if isNull response then
            raise (JsonException("The response body must be a JSON object."))
        let delta, finish =
            if isNull response.Choices || response.Choices.Length = 0 then
                "", None
            else
                let choice = response.Choices.[0]
                let deltaText =
                    if isNull choice.Delta || isNull choice.Delta.Content then ""
                    else choice.Delta.Content
                let finishReason =
                    if isNull choice.FinishReason then None
                    elif String.IsNullOrWhiteSpace choice.FinishReason then
                        raise (JsonException("choices[0].finish_reason must be a non-empty string or null."))
                    else Some choice.FinishReason
                deltaText, finishReason
        let tokens =
            if isNull response.Usage then None
            elif response.Usage.TotalTokens.HasValue then Some response.Usage.TotalTokens.Value
            else raise (JsonException("usage.total_tokens is required when usage is present."))
        delta, finish, tokens, tokenUsage response.Usage

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
                if isNull response.Usage then None
                elif response.Usage.TotalTokens.HasValue then Some response.Usage.TotalTokens.Value
                else raise (JsonException("usage.total_tokens is required when usage is present."))
            CompletionResult.create choice.Message.Content choice.FinishReason totalTokens (tokenUsage response.Usage)
        with ex ->
            CompletionResult.create (sprintf "Parse error: %s" ex.Message) "error" None None

    interface ILlmProvider with
        member _.Name = sprintf "Ollama(%s)" config.Model

        member _.CompleteAsync (conversation: Conversation) (options: CompletionOptions) : Task<CompletionResult> =
            task {
                let body = buildRequestBody conversation options false
                let content = new StringContent(body, Encoding.UTF8, "application/json")

                let! response = client.PostAsync("/v1/chat/completions", content)
                let! responseBody = response.Content.ReadAsStringAsync()

                if not response.IsSuccessStatusCode then
                    return CompletionResult.create (sprintf "Error: %d - %s" (int response.StatusCode) responseBody) "error" None None
                else
                    return parseResponse responseBody
            }

    interface IStreamingLlmProvider with
        member _.StreamAsync (conversation: Conversation) (options: CompletionOptions) (onChunk: CompletionChunk -> unit) : Task<CompletionResult> =
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
