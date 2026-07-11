namespace Nao.Providers

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Nao.Agents

/// LLM provider that connects to an Ollama server via its OpenAI-compatible API.
/// Ollama exposes /v1/chat/completions for chat-style completions.
type OllamaProvider(config: OllamaConfig) =
    let client = new HttpClient(BaseAddress = Uri(config.BaseUrl))

    let roleToString (role: Role) =
        match role with
        | System -> "system"
        | User -> "user"
        | Assistant -> "assistant"

    let buildRequestBody (conversation: Conversation) (options: CompletionOptions) (streaming: bool) =
        use stream = new System.IO.MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteString("model", config.Model)

        writer.WriteStartArray("messages")
        for m in conversation do
            writer.WriteStartObject()
            writer.WriteString("role", roleToString m.Role)
            writer.WriteString("content", m.Content)
            writer.WriteEndObject()
        writer.WriteEndArray()

        writer.WriteNumber("temperature", options.Temperature)
        writer.WriteBoolean("stream", streaming)
        // Ask for a trailing usage chunk so streamed completions can still report token
        // counts (ignored by backends that don't support it).
        if streaming then
            writer.WriteStartObject("stream_options")
            writer.WriteBoolean("include_usage", true)
            writer.WriteEndObject()

        match options.MaxTokens with
        | Some t -> writer.WriteNumber("max_tokens", t)
        | None -> ()

        match options.StopSequences with
        | [] -> ()
        | seqs ->
            writer.WriteStartArray("stop")
            for s in seqs do
                writer.WriteStringValue(s)
            writer.WriteEndArray()

        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    // Parse one streamed SSE JSON object into (text delta, finish reason, total tokens).
    // Any of the three may be absent in a given chunk (deltas carry text; the penultimate
    // chunk carries finish_reason; an optional trailing chunk carries usage).
    let parseStreamChunk (json: string) : (string * string option * int option) =
        try
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            let delta, finish =
                match root.TryGetProperty("choices") with
                | true, choices when choices.GetArrayLength() > 0 ->
                    let choice = choices.[0]
                    let d =
                        match choice.TryGetProperty("delta") with
                        | true, deltaObj ->
                            match deltaObj.TryGetProperty("content") with
                            | true, c when c.ValueKind = JsonValueKind.String -> c.GetString()
                            | _ -> ""
                        | _ -> ""
                    let fr =
                        match choice.TryGetProperty("finish_reason") with
                        | true, fr when fr.ValueKind = JsonValueKind.String && not (String.IsNullOrEmpty(fr.GetString())) ->
                            Some (fr.GetString())
                        | _ -> None
                    d, fr
                | _ -> "", None
            let tokens =
                match root.TryGetProperty("usage") with
                | true, usage when usage.ValueKind = JsonValueKind.Object ->
                    match usage.TryGetProperty("total_tokens") with
                    | true, t when t.ValueKind = JsonValueKind.Number -> Some (t.GetInt32())
                    | _ -> None
                | _ -> None
            delta, finish, tokens
        with _ -> "", None, None

    let parseResponse (json: string) : CompletionResult =
        try
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement

            let content =
                match root.TryGetProperty("choices") with
                | true, choices when choices.GetArrayLength() > 0 ->
                    let firstChoice = choices.[0]
                    match firstChoice.TryGetProperty("message") with
                    | true, message ->
                        match message.TryGetProperty("content") with
                        | true, c -> c.GetString()
                        | _ -> ""
                    | _ -> ""
                | _ -> ""

            let finishReason =
                match root.TryGetProperty("choices") with
                | true, choices when choices.GetArrayLength() > 0 ->
                    match choices.[0].TryGetProperty("finish_reason") with
                    | true, fr when fr.ValueKind = JsonValueKind.String ->
                        let r = fr.GetString()
                        if String.IsNullOrEmpty(r) then "stop" else r
                    | _ -> "stop"
                | _ -> "stop"

            let totalTokens =
                match root.TryGetProperty("usage") with
                | true, usage ->
                    match usage.TryGetProperty("total_tokens") with
                    | true, t when t.ValueKind = JsonValueKind.Number -> Some (t.GetInt32())
                    | _ -> None
                | _ -> None

            { Content = content
              FinishReason = finishReason
              TokensUsed = totalTokens }
        with ex ->
            { Content = sprintf "Parse error: %s" ex.Message
              FinishReason = "error"
              TokensUsed = None }

    interface ILlmProvider with
        member _.Name = sprintf "Ollama(%s)" config.Model

        member _.CompleteAsync (conversation: Conversation) (options: CompletionOptions) : Task<CompletionResult> =
            task {
                let body = buildRequestBody conversation options false
                let content = new StringContent(body, Encoding.UTF8, "application/json")

                let! response = client.PostAsync("/v1/chat/completions", content)
                let! responseBody = response.Content.ReadAsStringAsync()

                if not response.IsSuccessStatusCode then
                    return
                        { Content = sprintf "Error: %d - %s" (int response.StatusCode) responseBody
                          FinishReason = "error"
                          TokensUsed = None }
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
                        return
                            { Content = sprintf "Error: %d - %s" (int response.StatusCode) errorBody
                              FinishReason = "error"
                              TokensUsed = None }
                    else
                        use! responseStream = response.Content.ReadAsStreamAsync()
                        use reader = new System.IO.StreamReader(responseStream)
                        let builder = StringBuilder()
                        let mutable finishReason = "stop"
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
                                        match finish with Some r -> finishReason <- r | None -> ()
                                        match tk with Some _ -> tokens <- tk | None -> ()
                                        if delta <> "" then
                                            builder.Append(delta) |> ignore
                                            onChunk { Delta = delta; FinishReason = None; TokensUsed = None }
                        // Terminal chunk: report how generation finished and any token usage.
                        onChunk { Delta = ""; FinishReason = Some finishReason; TokensUsed = tokens }
                        return
                            { Content = builder.ToString()
                              FinishReason = finishReason
                              TokensUsed = tokens }
                with ex ->
                    return
                        { Content = sprintf "Error: %s" ex.Message
                          FinishReason = "error"
                          TokensUsed = None }
            }

    interface IDisposable with
        member _.Dispose() = client.Dispose()
