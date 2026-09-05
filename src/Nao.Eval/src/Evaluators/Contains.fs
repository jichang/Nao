namespace Nao.Eval.Evaluators

open System
open Nao.Eval

/// Evaluators that check whether output contains expected text or keywords.
module Contains =

    let private comparison caseSensitive =
        if caseSensitive then
            StringComparison.Ordinal
        else
            StringComparison.OrdinalIgnoreCase

    /// Create an expected-substring evaluator with configurable case sensitivity.
    let create caseSensitive =
        let comparison = comparison caseSensitive

        Evaluator.create "Contains" (fun _correlation (case: EvalCase) (actual: string) ->
            task {
                match case.Expected with
                | Some expected ->
                    if actual.Contains(expected, comparison) then
                        return (EvalVerdict.Pass, sprintf "Output contains '%s'" expected)
                    else
                        return (EvalVerdict.Fail, sprintf "Output does not contain '%s'" expected)
                | None -> return (EvalVerdict.Fail, "Contains evaluator requires an expected value")
            })

    /// Create an evaluator requiring all keywords, with configurable case sensitivity.
    let allWithCaseSensitivity caseSensitive (keywords: string list) =
        let comparison = comparison caseSensitive

        Evaluator.create "ContainsAll" (fun _correlation (_case: EvalCase) (actual: string) ->
            task {
                let found = keywords |> List.filter (fun kw -> actual.Contains(kw, comparison))

                let missing =
                    keywords |> List.filter (fun kw -> not (actual.Contains(kw, comparison)))

                let score = float found.Length / float keywords.Length

                if missing.IsEmpty then
                    return (EvalVerdict.Pass, sprintf "Output contains all %d keywords" keywords.Length)
                else
                    return (EvalVerdict.Partial score, sprintf "Missing keywords: %s" (String.concat ", " missing))
            })

    /// Create an evaluator requiring any keyword, with configurable case sensitivity.
    let anyWithCaseSensitivity caseSensitive (keywords: string list) =
        let comparison = comparison caseSensitive

        Evaluator.create "ContainsAny" (fun _correlation (_case: EvalCase) (actual: string) ->
            task {
                let found = keywords |> List.filter (fun kw -> actual.Contains(kw, comparison))

                if found.Length > 0 then
                    return (EvalVerdict.Pass, sprintf "Output contains: %s" (String.concat ", " found))
                else
                    return (EvalVerdict.Fail, sprintf "Output contains none of: %s" (String.concat ", " keywords))
            })

    /// Case-insensitive expected-substring evaluation.
    let evaluator = create false

    /// Require all keywords, ignoring case.
    let all keywords = allWithCaseSensitivity false keywords

    /// Require any keyword, ignoring case.
    let any keywords = anyWithCaseSensitivity false keywords
