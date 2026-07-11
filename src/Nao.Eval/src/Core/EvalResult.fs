namespace Nao.Eval

open System
open Nao.Agents

/// The verdict of a single evaluation
[<RequireQualifiedAccess>]
type EvalVerdict =
    | Pass
    | Fail
    | Partial of score: float

    member this.Score =
        match this with
        | Pass -> 1.0
        | Fail -> 0.0
        | Partial s -> s

    member this.Passed =
        match this with
        | Pass -> true
        | Partial s -> s >= 0.5
        | Fail -> false

/// The result of evaluating a single case
type EvalResult =
    { /// The eval case that was run
      CaseId: string
      /// The agent's actual output
      ActualOutput: string
      /// The evaluation verdict
      Verdict: EvalVerdict
      /// Reason/explanation for the verdict
      Reason: string
      /// Time taken to get the agent's response (ms)
      LatencyMs: int64
      /// Evaluator that produced this result
      EvaluatorName: string
      /// Timestamp of evaluation
      Timestamp: DateTimeOffset
      /// Optional execution trace for deeper analysis and regression detection
      ExecutionTrace: ExecutionTrace option }

module EvalResult =

    let passed result = result.Verdict.Passed

    let score result = result.Verdict.Score
