namespace Nao.Eval

open System.Threading.Tasks
open System.Threading
open Nao.Agents

/// Host authorization and execution state, kept separate from model-visible evaluation data.
type EvaluationContext =
    { Authorization: AuthorizationScope
      Correlation: CorrelationContext
      CancellationToken: CancellationToken }

/// Functional evaluator for scoring agent outputs against expectations.
type Evaluator =
    {
        /// A name identifying this evaluator.
        Name: string
        /// Evaluate the agent's output for a given case.
        EvaluateAsync: EvaluationContext -> EvalCase -> string -> Task<EvalVerdict * string>
    }

/// Functions for constructing and invoking evaluators.
[<RequireQualifiedAccess>]
module Evaluator =

    /// Create an evaluator from a name and asynchronous evaluation function.
    let create name (evaluateAsync: EvaluationContext -> EvalCase -> string -> Task<EvalVerdict * string>) : Evaluator =
        { Name = name
          EvaluateAsync =
            fun context case actual ->
                task {
                    context.CancellationToken.ThrowIfCancellationRequested()
                    return! evaluateAsync context case actual |> _.WaitAsync(context.CancellationToken)
                } }

    /// Evaluate an output with an evaluator.
    let evaluateAsync context case actual evaluator =
        evaluator.EvaluateAsync context case actual
