namespace Nao.Eval.Evaluators

open System.Text.RegularExpressions
open System.Threading.Tasks
open Nao.Eval

/// Evaluator that checks if the output matches a regular expression pattern
type RegexEvaluator(pattern: string, ?options: RegexOptions) =
    let regexOpts = defaultArg options RegexOptions.IgnoreCase
    let regex = Regex(pattern, regexOpts ||| RegexOptions.Compiled)

    interface IEvaluator with
        member _.Name = "Regex"
        member _.EvaluateAsync (_case: EvalCase) (actual: string) =
            task {
                if regex.IsMatch(actual) then
                    return (EvalVerdict.Pass, sprintf "Output matches pattern '%s'" pattern)
                else
                    return (EvalVerdict.Fail, sprintf "Output does not match pattern '%s'" pattern)
            }

module RegexEval =

    let matches pattern = RegexEvaluator(pattern) :> IEvaluator

    let matchesCaseSensitive pattern =
        RegexEvaluator(pattern, RegexOptions.None) :> IEvaluator
