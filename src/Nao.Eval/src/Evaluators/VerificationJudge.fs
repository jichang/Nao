namespace Nao.Eval.Evaluators

open System
open System.Threading.Tasks
open Nao.Agents
open Nao.Eval

/// Adapter: Wraps an IJudge (Verification layer) as an IEvaluator (Eval layer).
/// This bridges the richer judgement system (criteria scores, suggestions) into
/// the evaluation framework for dataset-level analysis and regression detection.
type VerificationJudgeAdapter(judge: IJudge, agentId: AgentId) =

    interface IEvaluator with
        member _.Name = sprintf "judge:%s" judge.Name

        member _.EvaluateAsync (case: EvalCase) (actual: string) =
            task {
                // Create an execution trace from the eval case output
                let trace =
                    Verification.startTrace agentId case.Input
                    |> Verification.addStep (TraceAction.LlmCall "unknown") case.Input actual 0L
                    |> Verification.complete actual

                let! judgement = judge.JudgeAsync trace

                let verdict =
                    match judgement.Verdict with
                    | JudgementVerdict.Pass -> EvalVerdict.Pass
                    | JudgementVerdict.Fail -> EvalVerdict.Fail
                    | JudgementVerdict.Partial score -> EvalVerdict.Partial score
                    | JudgementVerdict.Inconclusive _ -> EvalVerdict.Partial 0.5

                let criteriaStr =
                    judgement.CriteriaScores
                    |> Map.fold (fun acc k v -> acc + sprintf "%s=%.2f " k v) ""

                let reason = sprintf "%s [Criteria: %s]" judgement.Explanation (criteriaStr.TrimEnd())

                return (verdict, reason)
            }

/// Module for creating VerificationJudge evaluators
module VerificationJudge =

    /// Create an IEvaluator from an IJudge
    let fromJudge (judge: IJudge) (agentId: AgentId) : IEvaluator =
        VerificationJudgeAdapter(judge, agentId) :> IEvaluator
