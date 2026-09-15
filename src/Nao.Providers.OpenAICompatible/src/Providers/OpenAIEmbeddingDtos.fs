namespace Nao.Providers

open System
open System.Text.Json
open System.Text.Json.Serialization

[<AllowNullLiteral>]
type OpenAIEmbeddingRequestDto() =
    [<JsonPropertyName("model")>]
    member val Model: string = null with get, set

    [<JsonPropertyName("input")>]
    member val Input: string array = null with get, set

    [<JsonPropertyName("encoding_format")>]
    member val EncodingFormat: string = null with get, set

[<AllowNullLiteral>]
type OpenAIEmbeddingDataDto() =
    [<JsonPropertyName("index")>]
    member val Index = -1 with get, set

    [<JsonPropertyName("embedding")>]
    member val Embedding: float array = null with get, set

[<AllowNullLiteral>]
type OpenAIEmbeddingUsageDto() =
    [<JsonPropertyName("prompt_tokens")>]
    member val PromptTokens = Nullable<int>() with get, set

[<AllowNullLiteral>]
type OpenAIEmbeddingResponseDto() =
    [<JsonPropertyName("model")>]
    member val Model: string = null with get, set

    [<JsonPropertyName("data")>]
    member val Data: OpenAIEmbeddingDataDto array = null with get, set

    [<JsonPropertyName("usage")>]
    member val Usage: OpenAIEmbeddingUsageDto = null with get, set

[<RequireQualifiedAccess>]
module OpenAIEmbeddingDto =
    let private options = JsonSerializerOptions()

    let serializeRequest model inputs =
        let request = OpenAIEmbeddingRequestDto()
        request.Model <- model
        request.Input <- inputs
        request.EncodingFormat <- "float"
        JsonSerializer.Serialize(request, options)

    let deserializeResponse (json: string) =
        JsonSerializer.Deserialize<OpenAIEmbeddingResponseDto>(json, options)
