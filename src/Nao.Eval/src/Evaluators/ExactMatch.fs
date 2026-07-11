namespace Nao.Eval.Evaluators

open System
open System.Threading.Tasks
open Nao.Eval

/// Evaluator that checks for exact string match (case-insensitive by default)
type ExactMatchEvaluator(?caseSensitive: bool) =
    let sensitive = defaultArg caseSensitive false

    interface IEvaluator with
        member _.Name = "ExactMatch"
        member _.EvaluateAsync (case: EvalCase) (actual: string) =
            task {
                match case.Expected with
                | Some expected ->
                    let matches =
                        if sensitive then actual.Trim() = expected.Trim()
                        else String.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase)
                    if matches then
                        return (EvalVerdict.Pass, "Output exactly matches expected")
                    else
                        return (EvalVerdict.Fail, sprintf "Expected '%s' but got '%s'" expected actual)
                | None ->
                    return (EvalVerdict.Fail, "ExactMatch requires an expected value")
            }

module ExactMatch =

    let evaluator = ExactMatchEvaluator() :> IEvaluator

    let caseSensitive = ExactMatchEvaluator(caseSensitive = true) :> IEvaluator
