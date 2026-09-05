namespace Nao.Eval.Evaluators

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Nao.Agents
open Nao.Eval

[<AllowNullLiteral>]
type LlmJudgeResponseDto() =
    [<JsonPropertyName("score")>]
    member val Score = Nullable<float>() with get, set

    [<JsonPropertyName("reason")>]
    member val Reason: string = null with get, set

[<RequireQualifiedAccess>]
module internal LlmJudgeResponse =
    let example () =
        let response = LlmJudgeResponseDto()
        response.Score <- Nullable 5.0
        response.Reason <- "brief explanation"
        JsonSerializer.Serialize(response)

    let deserialize (json: string) =
        JsonSerializer.Deserialize<LlmJudgeResponseDto>(json.Trim())

/// Configuration for the LLM-as-judge evaluator
type LlmJudgeConfig =
    { Provider: LlmProvider
      Options: CompletionOptions
      Criteria: string
      ScaleDescription: string }

    static member Default provider =
        { Provider = provider
          Options =
            { CompletionOptions.Default with
                Temperature = 0.0 }
          Criteria = "correctness, completeness, and relevance"
          ScaleDescription = "1-5 where 1=completely wrong, 3=partially correct, 5=perfect" }

/// Evaluators that use an LLM to judge agent output quality.
module LlmJudge =

    let private buildPrompt config (case: EvalCase) (actual: string) =
        let expectedPart =
            match case.Expected with
            | Some exp -> sprintf "\n\nReference Answer:\n%s" exp
            | None -> ""

        sprintf
            """You are an evaluation judge. Grade the following agent output based on: %s

Scale: %s

User Input:
%s%s

Agent Output:
%s

Respond with ONLY a JSON object in this exact format:
%s

Where score is a number on the scale described above."""
            config.Criteria
            config.ScaleDescription
            case.Input
            expectedPart
            actual
            (LlmJudgeResponse.example ())

    let private parseScore (response: string) =
        try
            let parsed = LlmJudgeResponse.deserialize response

            if isNull parsed || not parsed.Score.HasValue then
                raise (JsonException("score is required and must be a number."))

            if String.IsNullOrWhiteSpace parsed.Reason then
                raise (JsonException("reason is required and must be a non-empty string."))

            // Normalize to 0-1 scale (assuming 1-5 scale by default)
            let normalized = (parsed.Score.Value - 1.0) / 4.0 |> max 0.0 |> min 1.0
            (normalized, parsed.Reason)
        with ex ->
            (0.0, sprintf "Parse error: %s" ex.Message)

    /// Create an evaluator from the complete judge configuration.
    let withConfig config =
        Evaluator.create "LlmJudge" (fun correlation (case: EvalCase) (actual: string) ->
            task {
                let prompt = buildPrompt config case actual

                let system =
                    Prompt.render
                        { Prompt.Empty with
                            Role = "You are a precise evaluation judge."
                            OutputFormat = OutputFormat.Schema "JSON" }

                let conversation =
                    [ { Role = System; Content = system }; { Role = User; Content = prompt } ]

                let! result = config.Provider.CompleteAsync correlation conversation config.Options
                let (score, reason) = parseScore result.Content

                let verdict =
                    if score >= 0.8 then EvalVerdict.Pass
                    elif score <= 0.2 then EvalVerdict.Fail
                    else EvalVerdict.Partial score

                return (verdict, reason)
            })

    let create provider =
        LlmJudgeConfig.Default provider |> withConfig

    let withCriteria criteria provider =
        { LlmJudgeConfig.Default provider with
            Criteria = criteria }
        |> withConfig
