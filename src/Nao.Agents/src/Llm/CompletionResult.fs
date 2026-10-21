namespace Nao.Agents

open System

[<RequireQualifiedAccess>]
type ProviderFinishReason =
    | Stop
    | Length
    | ToolCalls
    | ContentFiltered
    | Cancelled
    | Other of providerValue: string

[<RequireQualifiedAccess>]
module ProviderFinishReason =
    let parse value =
        match value with
        | "stop"
        | "end_turn"
        | "stop_sequence" -> ProviderFinishReason.Stop
        | "length"
        | "max_tokens" -> ProviderFinishReason.Length
        | "tool_call"
        | "tool_calls"
        | "tool_use" -> ProviderFinishReason.ToolCalls
        | "content_filter"
        | "content_filtered"
        | "safety" -> ProviderFinishReason.ContentFiltered
        | "cancelled"
        | "canceled" -> ProviderFinishReason.Cancelled
        | value when String.IsNullOrWhiteSpace value ->
            invalidArg (nameof value) "Provider finish reason must be non-blank."
        | value -> ProviderFinishReason.Other value

[<RequireQualifiedAccess>]
type ProviderUsage =
    | None
    | Aggregate of totalTokens: int
    | Partial of inputTokens: int option * outputTokens: int option
    | Full of inputTokens: int * outputTokens: int

[<RequireQualifiedAccess>]
module ProviderUsage =
    let private requireNonNegative argumentName value =
        if value < 0 then
            invalidArg argumentName "Provider token usage cannot be negative."

    let aggregate totalTokens =
        requireNonNegative (nameof totalTokens) totalTokens
        ProviderUsage.Aggregate totalTokens

    let partial (inputTokens: int option) (outputTokens: int option) =
        if inputTokens.IsNone && outputTokens.IsNone then
            invalidArg (nameof inputTokens) "Partial usage must report at least one token count."

        inputTokens |> Option.iter (requireNonNegative (nameof inputTokens))
        outputTokens |> Option.iter (requireNonNegative (nameof outputTokens))
        ProviderUsage.Partial(inputTokens, outputTokens)

    let full inputTokens outputTokens =
        requireNonNegative (nameof inputTokens) inputTokens
        requireNonNegative (nameof outputTokens) outputTokens
        ProviderUsage.Full(inputTokens, outputTokens)

    let total =
        function
        | ProviderUsage.None -> None
        | ProviderUsage.Aggregate totalTokens -> Some totalTokens
        | ProviderUsage.Partial(Some inputTokens, Some outputTokens)
        | ProviderUsage.Full(inputTokens, outputTokens) -> Some(inputTokens + outputTokens)
        | ProviderUsage.Partial _ -> None

type ProviderToolCall =
    { Id: string
      Name: string
      ArgumentsJson: string }

type CompletionResult =
    { Content: string
      FinishReason: ProviderFinishReason
      Usage: ProviderUsage
      ToolCalls: ProviderToolCall list
      StructuredOutputJson: string option
      Extensions: Map<string, string> }

[<RequireQualifiedAccess>]
module CompletionResult =
    let create content finishReason usage toolCalls structuredOutputJson extensions : CompletionResult =
        { Content = content
          FinishReason = finishReason
          Usage = usage
          ToolCalls = toolCalls
          StructuredOutputJson = structuredOutputJson
          Extensions = extensions }

    let text content finishReason usage =
        create content finishReason usage [] None Map.empty

[<RequireQualifiedAccess>]
type ProviderStreamTermination =
    | Completed of ProviderFinishReason
    | Partial of ProviderErrorCategory

type CompletionChunk =
    { Delta: string
      Termination: ProviderStreamTermination option
      Usage: ProviderUsage
      ToolCalls: ProviderToolCall list
      StructuredOutputJson: string option
      Extensions: Map<string, string> }

[<RequireQualifiedAccess>]
module CompletionChunk =
    let create delta termination usage toolCalls structuredOutputJson extensions : CompletionChunk =
        { Delta = delta
          Termination = termination
          Usage = usage
          ToolCalls = toolCalls
          StructuredOutputJson = structuredOutputJson
          Extensions = extensions }

    let text delta termination usage =
        create delta termination usage [] None Map.empty

    let partialFailure (failure: PlatformFailure) =
        let category =
            failure.ProviderCategory
            |> Option.defaultValue ProviderErrorCategory.ServerFailure

        text "" (Some(ProviderStreamTermination.Partial category)) ProviderUsage.None
