namespace Nao.Providers

open System
open System.Text.Json
open System.Text.Json.Serialization

[<AllowNullLiteral>]
type OpenAIChatMessageDto() =
    [<JsonPropertyName("role")>]
    member val Role: string = null with get, set

    [<JsonPropertyName("content")>]
    member val Content: string = null with get, set

[<AllowNullLiteral>]
type OpenAIStreamOptionsDto() =
    [<JsonPropertyName("include_usage")>]
    member val IncludeUsage = false with get, set

[<AllowNullLiteral>]
type OpenAIChatRequestDto() =
    [<JsonPropertyName("model")>]
    member val Model: string = null with get, set

    [<JsonPropertyName("messages")>]
    member val Messages: OpenAIChatMessageDto array = null with get, set

    [<JsonPropertyName("temperature")>]
    member val Temperature = 0.0 with get, set

    [<JsonPropertyName("stream")>]
    member val Stream = false with get, set

    [<JsonPropertyName("stream_options"); JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)>]
    member val StreamOptions: OpenAIStreamOptionsDto = null with get, set

    [<JsonPropertyName("max_tokens"); JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)>]
    member val MaxTokens = Nullable<int>() with get, set

    [<JsonPropertyName("stop"); JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)>]
    member val Stop: string array = null with get, set

    [<JsonPropertyName("reasoning_effort"); JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)>]
    member val ReasoningEffort: string = null with get, set

[<AllowNullLiteral>]
type OpenAIResponseMessageDto() =
    [<JsonPropertyName("content")>]
    member val Content: string = null with get, set

[<AllowNullLiteral>]
type OpenAIResponseDeltaDto() =
    [<JsonPropertyName("content")>]
    member val Content: string = null with get, set

[<AllowNullLiteral>]
type OpenAIChoiceDto() =
    [<JsonPropertyName("message")>]
    member val Message: OpenAIResponseMessageDto = null with get, set

    [<JsonPropertyName("delta")>]
    member val Delta: OpenAIResponseDeltaDto = null with get, set

    [<JsonPropertyName("finish_reason")>]
    member val FinishReason: string = null with get, set

[<AllowNullLiteral>]
type OpenAIUsageDto() =
    [<JsonPropertyName("total_tokens")>]
    member val TotalTokens = Nullable<int>() with get, set

[<AllowNullLiteral>]
type OpenAIChatResponseDto() =
    [<JsonPropertyName("choices")>]
    member val Choices: OpenAIChoiceDto array = null with get, set

    [<JsonPropertyName("usage")>]
    member val Usage: OpenAIUsageDto = null with get, set

[<RequireQualifiedAccess>]
module internal OpenAIChatDto =
    let options = JsonSerializerOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)

    let serializeRequest model messages temperature streaming maxTokens stopSequences reasoningEffort =
        let messageDtos =
            messages
            |> Array.map (fun (role, content) ->
                let message = OpenAIChatMessageDto()
                message.Role <- role
                message.Content <- content
                message)
        let request = OpenAIChatRequestDto()
        request.Model <- model
        request.Messages <- messageDtos
        request.Temperature <- temperature
        request.Stream <- streaming
        if streaming then
            let streamOptions = OpenAIStreamOptionsDto()
            streamOptions.IncludeUsage <- true
            request.StreamOptions <- streamOptions
        request.MaxTokens <- maxTokens |> Option.map Nullable |> Option.defaultValue (Nullable())
        request.Stop <- if Array.isEmpty stopSequences then null else stopSequences
        request.ReasoningEffort <- reasoningEffort
        JsonSerializer.Serialize(request, options)

    let deserializeResponse (json: string) =
        JsonSerializer.Deserialize<OpenAIChatResponseDto>(json, options)