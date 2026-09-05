namespace Nao.Eval.Evaluators

open Nao.Eval

/// Evaluator that combines multiple evaluators with configurable logic
[<RequireQualifiedAccess>]
type CompositeMode =
    /// All evaluators must pass
    | All
    /// Any evaluator passing is sufficient
    | Any
    /// Average score across all evaluators
    | Average

/// Functions for composing evaluators.
module Composite =

    /// Create a composite evaluator using the requested combination mode.
    let create mode evaluators =
        Evaluator.create (sprintf "Composite(%A)" mode) (fun correlation (case: EvalCase) (actual: string) ->
            task {
                let! results =
                    evaluators
                    |> List.map (fun evaluator -> evaluator.EvaluateAsync correlation case actual)
                    |> fun tasks -> System.Threading.Tasks.Task.WhenAll(tasks)

                let results = results |> Array.toList

                match mode with
                | CompositeMode.All ->
                    let allPass = results |> List.forall (fun (v, _) -> v.Passed)

                    if allPass then
                        return (EvalVerdict.Pass, "All evaluators passed")
                    else
                        let failures =
                            results
                            |> List.filter (fun (v, _) -> not v.Passed)
                            |> List.map snd
                            |> String.concat "; "

                        let avgScore = results |> List.averageBy (fun (v, _) -> v.Score)
                        return (EvalVerdict.Partial avgScore, sprintf "Some evaluators failed: %s" failures)

                | CompositeMode.Any ->
                    let anyPass = results |> List.exists (fun (v, _) -> v.Passed)

                    if anyPass then
                        let passing =
                            results
                            |> List.filter (fun (v, _) -> v.Passed)
                            |> List.map snd
                            |> String.concat "; "

                        return (EvalVerdict.Pass, sprintf "Passed: %s" passing)
                    else
                        let reasons = results |> List.map snd |> String.concat "; "
                        return (EvalVerdict.Fail, sprintf "No evaluator passed: %s" reasons)

                | CompositeMode.Average ->
                    let avgScore = results |> List.averageBy (fun (v, _) -> v.Score)
                    let reasons = results |> List.map snd |> String.concat "; "

                    let verdict =
                        if avgScore >= 0.8 then EvalVerdict.Pass
                        elif avgScore <= 0.2 then EvalVerdict.Fail
                        else EvalVerdict.Partial avgScore

                    return (verdict, sprintf "Average score: %.2f (%s)" avgScore reasons)
            })

    let all evaluators = create CompositeMode.All evaluators

    let any evaluators = create CompositeMode.Any evaluators

    let average evaluators = create CompositeMode.Average evaluators
