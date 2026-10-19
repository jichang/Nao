namespace Nao.Eval.Evaluators

open System
open Nao.Agents
open Nao.Eval

/// Functions that adapt verification judges to evaluators.
module VerificationJudge =

    /// Create an evaluator from a verification judge.
    let fromJudge (judge: Judge) (agentId: string) =
        let name = sprintf "judge:%s" judge.Name
        let configuration = sprintf "agent=%s|judge=%s" agentId judge.Name

        Evaluator.probabilistic
            name
            "1"
            configuration
            false
            "Judge verdicts are untrusted evidence and inconclusive results produce a partial score."
            (fun context (case: EvalCase) (actual: string) ->
                task {
                    let input = EvalCase.input case

                    let trace =
                        Verification.startTrace context.Correlation agentId input
                        |> Verification.addStep (TraceAction.LlmCall "unknown") input actual 0L
                        |> Verification.complete actual

                    let! judgement = Judge.judgeAsync trace judge |> _.WaitAsync(context.CancellationToken)

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
