namespace Nao.Eval

open System.Threading.Tasks

/// Functional evaluator for scoring agent outputs against expectations.
type Evaluator =
    {
        /// A name identifying this evaluator.
        Name: string
        /// Evaluate the agent's output for a given case.
        EvaluateAsync: EvalCase -> string -> Task<EvalVerdict * string>
    }

/// Functions for constructing and invoking evaluators.
[<RequireQualifiedAccess>]
module Evaluator =

    /// Create an evaluator from a name and asynchronous evaluation function.
    let create name evaluateAsync : Evaluator =
        { Name = name
          EvaluateAsync = evaluateAsync }

    /// Evaluate an output with an evaluator.
    let evaluateAsync case actual evaluator = evaluator.EvaluateAsync case actual
