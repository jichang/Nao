namespace Nao.Eval

open System.Threading.Tasks
open System.Threading
open Nao.Agents

/// Host authorization and execution state, kept separate from model-visible evaluation data.
type EvaluationContext =
    { Authorization: AuthorizationScope
      Correlation: CorrelationContext
      Execution: ExecutionResult
      CancellationToken: CancellationToken }

/// Functional evaluator for scoring agent outputs against expectations.
type Evaluator =
    {
        /// Versioned evaluator implementation and configuration identity.
        Identity: EvalComponentIdentity
        /// Whether the verdict is proof-like deterministic validation or probabilistic evidence.
        Authority: EvaluatorAuthority
        /// Documented release role and fail-closed behavior.
        Reliability: EvaluatorReliability
        /// Evaluate the agent's output for a given case.
        EvaluateAsync: EvaluationContext -> EvalCase -> string -> Task<EvalVerdict * string>
    }

/// Functions for constructing and invoking evaluators.
[<RequireQualifiedAccess>]
module Evaluator =

    /// Create an evaluator from its versioned implementation and configuration.
    let private create
        authority
        name
        version
        configuration
        reliability
        (evaluateAsync: EvaluationContext -> EvalCase -> string -> Task<EvalVerdict * string>)
        : Evaluator =
        { Identity = EvalComponentIdentity.create name version configuration
          Authority = authority
          Reliability = reliability
          EvaluateAsync =
            fun context case actual ->
                task {
                    context.CancellationToken.ThrowIfCancellationRequested()
                    return! evaluateAsync context case actual |> _.WaitAsync(context.CancellationToken)
                } }

    let deterministic name version configuration releaseBlocking failureSemantics evaluateAsync =
        create
            EvaluatorAuthority.Deterministic
            name
            version
            configuration
            { ReleaseBlocking = releaseBlocking
              FailureSemantics = failureSemantics }
            evaluateAsync

    let probabilistic name version configuration releaseBlocking failureSemantics evaluateAsync =
        create
            EvaluatorAuthority.Probabilistic
            name
            version
            configuration
            { ReleaseBlocking = releaseBlocking
              FailureSemantics = failureSemantics }
            evaluateAsync

    /// Evaluate an output with an evaluator.
    let evaluateAsync context case actual evaluator =
        evaluator.EvaluateAsync context case actual

    let name evaluator =
        EvalComponentIdentity.name evaluator.Identity
