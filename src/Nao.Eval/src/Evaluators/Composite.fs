namespace Nao.Eval.Evaluators

open System.Threading.Tasks
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

type CompositeEvaluator(evaluators: IEvaluator list, mode: CompositeMode) =

    interface IEvaluator with
        member _.Name = sprintf "Composite(%A)" mode
        member _.EvaluateAsync (case: EvalCase) (actual: string) =
            task {
                let! results =
                    evaluators
                    |> List.map (fun e -> e.EvaluateAsync case actual)
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
                        let passing = results |> List.filter (fun (v, _) -> v.Passed) |> List.map snd |> String.concat "; "
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
            }

module Composite =

    let all evaluators = CompositeEvaluator(evaluators, CompositeMode.All) :> IEvaluator

    let any evaluators = CompositeEvaluator(evaluators, CompositeMode.Any) :> IEvaluator

    let average evaluators = CompositeEvaluator(evaluators, CompositeMode.Average) :> IEvaluator
