namespace Nao.Eval.Evaluators

open System
open System.Threading.Tasks
open Nao.Eval

/// Evaluator that checks if the output contains expected substrings
type ContainsEvaluator(?caseSensitive: bool) =
    let sensitive = defaultArg caseSensitive false

    let comparison =
        if sensitive then StringComparison.Ordinal
        else StringComparison.OrdinalIgnoreCase

    interface IEvaluator with
        member _.Name = "Contains"
        member _.EvaluateAsync (case: EvalCase) (actual: string) =
            task {
                match case.Expected with
                | Some expected ->
                    if actual.Contains(expected, comparison) then
                        return (EvalVerdict.Pass, sprintf "Output contains '%s'" expected)
                    else
                        return (EvalVerdict.Fail, sprintf "Output does not contain '%s'" expected)
                | None ->
                    return (EvalVerdict.Fail, "Contains evaluator requires an expected value")
            }

/// Evaluator that checks if the output contains ALL of the specified keywords
type ContainsAllEvaluator(keywords: string list, ?caseSensitive: bool) =
    let sensitive = defaultArg caseSensitive false

    let comparison =
        if sensitive then StringComparison.Ordinal
        else StringComparison.OrdinalIgnoreCase

    interface IEvaluator with
        member _.Name = "ContainsAll"
        member _.EvaluateAsync (_case: EvalCase) (actual: string) =
            task {
                let found = keywords |> List.filter (fun kw -> actual.Contains(kw, comparison))
                let missing = keywords |> List.filter (fun kw -> not (actual.Contains(kw, comparison)))
                let score = float found.Length / float keywords.Length
                if missing.IsEmpty then
                    return (EvalVerdict.Pass, sprintf "Output contains all %d keywords" keywords.Length)
                else
                    return (EvalVerdict.Partial score, sprintf "Missing keywords: %s" (String.concat ", " missing))
            }

/// Evaluator that checks if the output contains ANY of the specified keywords
type ContainsAnyEvaluator(keywords: string list, ?caseSensitive: bool) =
    let sensitive = defaultArg caseSensitive false

    let comparison =
        if sensitive then StringComparison.Ordinal
        else StringComparison.OrdinalIgnoreCase

    interface IEvaluator with
        member _.Name = "ContainsAny"
        member _.EvaluateAsync (_case: EvalCase) (actual: string) =
            task {
                let found = keywords |> List.filter (fun kw -> actual.Contains(kw, comparison))
                if found.Length > 0 then
                    return (EvalVerdict.Pass, sprintf "Output contains: %s" (String.concat ", " found))
                else
                    return (EvalVerdict.Fail, sprintf "Output contains none of: %s" (String.concat ", " keywords))
            }

module Contains =

    let evaluator = ContainsEvaluator() :> IEvaluator

    let all keywords = ContainsAllEvaluator(keywords) :> IEvaluator

    let any keywords = ContainsAnyEvaluator(keywords) :> IEvaluator
