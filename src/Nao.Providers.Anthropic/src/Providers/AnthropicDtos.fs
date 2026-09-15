namespace Nao.Providers

open System
open System.Text.Json
open System.Text.Json.Serialization

[<AllowNullLiteral>]
type AnthropicMessageDto() =
    [<JsonPropertyName("role")>]
    member val Role: string = null with get, set

    [<JsonPropertyName("content")>]
    member val Content: string = null with get, set

[<AllowNullLiteral>]
type AnthropicRequestDto() =
    [<JsonPropertyName("model")>]
    member val Model: string = null with get, set

    [<JsonPropertyName("max_tokens")>]
    member val MaxTokens = 0 with get, set

    [<JsonPropertyName("temperature")>]
    member val Temperature = 0.0 with get, set

    [<JsonPropertyName("stream")>]
    member val Stream = false with get, set

    [<JsonPropertyName("system"); JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)>]
    member val System: string = null with get, set

    [<JsonPropertyName("messages")>]
    member val Messages: AnthropicMessageDto array = null with get, set

    [<JsonPropertyName("stop_sequences"); JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)>]
    member val StopSequences: string array = null with get, set

[<AllowNullLiteral>]
type AnthropicContentBlockDto() =
    [<JsonPropertyName("type")>]
    member val Type: string = null with get, set

    [<JsonPropertyName("text")>]
    member val Text: string = null with get, set

[<AllowNullLiteral>]
type AnthropicUsageDto() =
    [<JsonPropertyName("input_tokens")>]
    member val InputTokens = Nullable<int>() with get, set

    [<JsonPropertyName("output_tokens")>]
    member val OutputTokens = Nullable<int>() with get, set

[<AllowNullLiteral>]
type AnthropicResponseDto() =
    [<JsonPropertyName("content")>]
    member val Content: AnthropicContentBlockDto array = null with get, set

    [<JsonPropertyName("stop_reason")>]
    member val StopReason: string = null with get, set

    [<JsonPropertyName("usage")>]
    member val Usage: AnthropicUsageDto = null with get, set

[<AllowNullLiteral>]
type AnthropicStreamMessageDto() =
    [<JsonPropertyName("usage")>]
    member val Usage: AnthropicUsageDto = null with get, set

[<AllowNullLiteral>]
type AnthropicStreamDeltaDto() =
    [<JsonPropertyName("type")>]
    member val Type: string = null with get, set

    [<JsonPropertyName("text")>]
    member val Text: string = null with get, set

    [<JsonPropertyName("stop_reason")>]
    member val StopReason: string = null with get, set

[<AllowNullLiteral>]
type AnthropicStreamEventDto() =
    [<JsonPropertyName("type")>]
    member val Type: string = null with get, set

    [<JsonPropertyName("message")>]
    member val Message: AnthropicStreamMessageDto = null with get, set

    [<JsonPropertyName("delta")>]
    member val Delta: AnthropicStreamDeltaDto = null with get, set

    [<JsonPropertyName("usage")>]
    member val Usage: AnthropicUsageDto = null with get, set

[<RequireQualifiedAccess>]
module internal AnthropicDto =
    let options =
        JsonSerializerOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)

    let serializeRequest model maxTokens temperature streaming systemPrompt messages stopSequences =
        let messageDtos =
            messages
            |> Array.map (fun (role, content) ->
                let message = AnthropicMessageDto()
                message.Role <- role
                message.Content <- content
                message)

        let request = AnthropicRequestDto()
        request.Model <- model
        request.MaxTokens <- maxTokens
        request.Temperature <- temperature
        request.Stream <- streaming
        request.System <- systemPrompt
        request.Messages <- messageDtos
        request.StopSequences <- if Array.isEmpty stopSequences then null else stopSequences
        JsonSerializer.Serialize(request, options)

    let deserializeResponse (json: string) =
        JsonSerializer.Deserialize<AnthropicResponseDto>(json, options)

    let deserializeStreamEvent (json: string) =
        JsonSerializer.Deserialize<AnthropicStreamEventDto>(json, options)
