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
    {
        /// Stable identity for this evaluated result
        Id: Guid
        /// Retention owner inherited explicitly from the evaluation run
        Owner: string
        /// Dataset revision evaluated by this result
        DatasetId: Guid
        /// Evaluation run that produced this result
        RunId: Guid
        /// Agent execution that produced the evaluated output
        ExecutionId: ExecutionId
        /// The eval case that was run
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
        ExecutionTrace: ExecutionTrace option
    }

module EvalResult =

    let passed result = result.Verdict.Passed

    let score result = result.Verdict.Score

/// Stable identity shared by every result and report in one evaluation run.
type EvalRun =
    { Id: Guid
      Owner: string
      DatasetId: Guid
      StartedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module EvalRun =
    let create owner datasetId =
        if String.IsNullOrWhiteSpace owner then
            invalidArg (nameof owner) "Evaluation run owner cannot be blank."

        { Id = Guid.NewGuid()
          Owner = owner
          DatasetId = datasetId
          StartedAt = DateTimeOffset.UtcNow }
