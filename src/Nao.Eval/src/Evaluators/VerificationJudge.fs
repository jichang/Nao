namespace Nao.Eval.Evaluators

open System
open Nao.Agents
open Nao.Eval

/// Functions that adapt verification judges to evaluators.
module VerificationJudge =

    /// Create an evaluator from a verification judge.
    let fromJudge (judge: Judge) (agentId: string) =
        Evaluator.create (sprintf "judge:%s" judge.Name) (fun (case: EvalCase) (actual: string) ->
            task {
                // Create an execution trace from the eval case output
                let trace =
                    Verification.startTrace agentId case.Input
                    |> Verification.addStep (TraceAction.LlmCall "unknown") case.Input actual 0L
                    |> Verification.complete actual

                let! judgement = Judge.judgeAsync trace judge

                let verdict =
                    match judgement.Verdict with
                    | JudgementVerdict.Pass -> EvalVerdict.Pass
                    | JudgementVerdict.Fail -> EvalVerdict.Fail
                    | JudgementVerdict.Partial score -> EvalVerdict.Partial score
                    | JudgementVerdict.Inconclusive _ -> EvalVerdict.Partial 0.5

                let criteriaStr =
                    judgement.CriteriaScores
                    |> Map.fold (fun acc k v -> acc + sprintf "%s=%.2f " k v) ""

                let reason =
                    sprintf "%s [Criteria: %s]" judgement.Explanation (criteriaStr.TrimEnd())

                return (verdict, reason)
            })
