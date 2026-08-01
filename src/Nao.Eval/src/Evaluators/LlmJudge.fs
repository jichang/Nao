namespace Nao.Eval.Evaluators

open System.Text.Json
open System.Threading.Tasks
open Nao.Agents
open Nao.Eval

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
{"score": <number>, "reason": "<brief explanation>"}

Where score is a number on the scale described above."""
            config.Criteria
            config.ScaleDescription
            case.Input
            expectedPart
            actual

    let parseScore (response: string) =
        try
            use doc = JsonDocument.Parse(response.Trim())
            let root = doc.RootElement

            let score =
                match root.TryGetProperty("score") with
                | true, elem when elem.ValueKind = JsonValueKind.Number -> elem.GetDouble()
                | _ -> 2.5 // midpoint fallback

            let reason =
                match root.TryGetProperty("reason") with
                | true, elem when elem.ValueKind = JsonValueKind.String -> elem.GetString()
                | _ -> "No reason provided"

            // Normalize to 0-1 scale (assuming 1-5 scale by default)
            let normalized = (score - 1.0) / 4.0 |> max 0.0 |> min 1.0
            (normalized, reason)
        with ex ->
            (0.5, sprintf "Parse error: %s" ex.Message)

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
