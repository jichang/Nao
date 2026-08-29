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
    { /// The LLM provider used as judge
      Provider: ILlmProvider
      /// Completion options for the judge
      Options: CompletionOptions
      /// Custom grading criteria (injected into the judge prompt)
      Criteria: string
      /// Score scale description (e.g. "1-5" or "pass/fail")
      ScaleDescription: string }

    static member Default provider =
        { Provider = provider
          Options = { CompletionOptions.Default with Temperature = 0.0 }
          Criteria = "correctness, completeness, and relevance"
          ScaleDescription = "1-5 where 1=completely wrong, 3=partially correct, 5=perfect" }

/// Evaluator that uses an LLM to judge agent output quality
type LlmJudgeEvaluator(config: LlmJudgeConfig) =

    let buildPrompt (case: EvalCase) (actual: string) =
        let expectedPart =
            match case.Expected with
            | Some exp -> sprintf "\n\nReference Answer:\n%s" exp
            | None -> ""

        sprintf """You are an evaluation judge. Grade the following agent output based on: %s

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

    let parseScore (response: string) =
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

    interface IEvaluator with
        member _.Name = "LlmJudge"
        member _.EvaluateAsync (case: EvalCase) (actual: string) =
            task {
                let prompt = buildPrompt case actual
                let system =
                    Prompt.render
                        { Prompt.Empty with
                            Role = "You are a precise evaluation judge."
                            OutputFormat = OutputFormat.Schema "JSON" }
                let conversation = [
                    { Role = System; Content = system }
                    { Role = User; Content = prompt }
                ]
                let! result = config.Provider.CompleteAsync conversation config.Options
                let (score, reason) = parseScore result.Content

                let verdict =
                    if score >= 0.8 then EvalVerdict.Pass
                    elif score <= 0.2 then EvalVerdict.Fail
                    else EvalVerdict.Partial score

                return (verdict, reason)
            }

module LlmJudge =

    let create provider = LlmJudgeEvaluator(LlmJudgeConfig.Default provider) :> IEvaluator

    let withCriteria criteria provider =
        LlmJudgeEvaluator({ LlmJudgeConfig.Default provider with Criteria = criteria }) :> IEvaluator

    let withConfig config = LlmJudgeEvaluator(config) :> IEvaluator
