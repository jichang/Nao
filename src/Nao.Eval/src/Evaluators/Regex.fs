namespace Nao.Eval.Evaluators

open System.Text.RegularExpressions
open Nao.Eval

/// Regular-expression evaluators.
module RegexEval =

    /// Create a regular-expression evaluator with the supplied options.
    let create options pattern =
        let regex = Regex(pattern, options ||| RegexOptions.Compiled)

        let configuration = sprintf "options=%d|pattern=%s" (int options) pattern

        Evaluator.deterministic
            "Regex"
            "1"
            configuration
            true
            "Fails when the configured regular expression does not match."
            (fun _correlation (_case: EvalCase) (actual: string) ->
                task {
                    if regex.IsMatch(actual) then
                        return (EvalVerdict.Pass, sprintf "Output matches pattern '%s'" pattern)
                    else
                        return (EvalVerdict.Fail, sprintf "Output does not match pattern '%s'" pattern)
                })

    /// Match a pattern while ignoring case.
    let matches pattern = create RegexOptions.IgnoreCase pattern

    /// Match a pattern case-sensitively.
    let matchesCaseSensitive pattern = create RegexOptions.None pattern
