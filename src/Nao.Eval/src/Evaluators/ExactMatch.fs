namespace Nao.Eval.Evaluators

open System
open Nao.Eval

/// Evaluators that check for an exact string match.
module ExactMatch =

    /// Create an exact-match evaluator with configurable case sensitivity.
    let create caseSensitive =
        Evaluator.create "ExactMatch" (fun (case: EvalCase) (actual: string) ->
            task {
                match case.Expected with
                | Some expected ->
                    let matches =
                        if caseSensitive then actual.Trim() = expected.Trim()
                        else String.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase)
                    if matches then
                        return (EvalVerdict.Pass, "Output exactly matches expected")
                    else
                        return (EvalVerdict.Fail, sprintf "Expected '%s' but got '%s'" expected actual)
                | None ->
                    return (EvalVerdict.Fail, "ExactMatch requires an expected value")
            })

    /// Case-insensitive exact matching.
    let evaluator = create false

    /// Case-sensitive exact matching.
    let caseSensitive = create true
