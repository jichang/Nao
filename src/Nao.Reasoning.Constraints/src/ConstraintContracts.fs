namespace Nao.Reasoning.Constraints

open System
open Nao.Agents
open Nao.Reasoning

[<RequireQualifiedAccess>]
type ConstraintSort =
    | Boolean
    | Integer
    | Real

[<StructuralEquality; StructuralComparison>]
type ConstraintRational = private ConstraintRational of numerator: bigint * denominator: bigint

[<RequireQualifiedAccess>]
module ConstraintRational =
    let create numerator denominator =
        if denominator = 0I then
            invalidArg (nameof denominator) "A rational denominator cannot be zero."

        let divisor = bigint.GreatestCommonDivisor(abs numerator, abs denominator)
        let sign = if denominator < 0I then -1I else 1I
        ConstraintRational(numerator / divisor * sign, abs denominator / divisor)

    let numerator (ConstraintRational(numerator, _)) = numerator
    let denominator (ConstraintRational(_, denominator)) = denominator

    let compare left right =
        compare (numerator left * denominator right) (numerator right * denominator left)

    let toString value =
        if denominator value = 1I then
            string (numerator value)
        else
            $"{numerator value}/{denominator value}"

[<RequireQualifiedAccess>]
type ConstraintValue =
    | Boolean of bool
    | Integer of bigint
    | Real of ConstraintRational

[<RequireQualifiedAccess>]
module ConstraintValue =
    let sort =
        function
        | ConstraintValue.Boolean _ -> ConstraintSort.Boolean
        | ConstraintValue.Integer _ -> ConstraintSort.Integer
        | ConstraintValue.Real _ -> ConstraintSort.Real

[<RequireQualifiedAccess>]
type ConstraintDomain =
    | Boolean
    | IntegerRange of minimum: bigint * maximum: bigint
    | RealRange of minimum: ConstraintRational * maximum: ConstraintRational
    | UnboundedInteger
    | UnboundedReal

type ConstraintVariable =
    { Name: string
      Domain: ConstraintDomain }

[<RequireQualifiedAccess>]
type ConstraintExpression =
    | Value of ConstraintValue
    | Variable of string
    | Not of ConstraintExpression
    | And of ConstraintExpression list
    | Or of ConstraintExpression list
    | Implies of ConstraintExpression * ConstraintExpression
    | Equal of ConstraintExpression * ConstraintExpression
    | Distinct of ConstraintExpression list
    | LessThan of ConstraintExpression * ConstraintExpression
    | LessThanOrEqual of ConstraintExpression * ConstraintExpression
    | GreaterThan of ConstraintExpression * ConstraintExpression
    | GreaterThanOrEqual of ConstraintExpression * ConstraintExpression
    | Add of ConstraintExpression list
    | Subtract of ConstraintExpression * ConstraintExpression
    | Multiply of ConstraintExpression list
    | Negate of ConstraintExpression

type ConstraintAssertion =
    { Id: string
      Expression: ConstraintExpression
      TrackForUnsatCore: bool
      Citations: ReasoningCitation list }

[<RequireQualifiedAccess>]
type ConstraintObjectiveKind =
    | Minimize
    | Maximize

type ConstraintObjective =
    { Id: string
      Kind: ConstraintObjectiveKind
      Expression: ConstraintExpression }

type ConstraintProblemIdentity =
    { Id: string
      Version: string
      Digest: string }

type ConstraintProblem =
    private
        { Owner: AuthorizationIdentity
          Identity: ConstraintProblemIdentity
          Variables: ConstraintVariable list
          Assertions: ConstraintAssertion list
          Objectives: ConstraintObjective list }

[<RequireQualifiedAccess>]
module ConstraintProblem =
    let internal validated authorization identity variables assertions objectives =
        { Owner = AuthorizationScope.identity authorization
          Identity = identity
          Variables = variables
          Assertions = assertions
          Objectives = objectives }

    let requireOwner authorization problem =
        if problem.Owner <> AuthorizationScope.identity authorization then
            raise (UnauthorizedAccessException "Constraint problem cannot cross authorization boundaries.")

        problem

    let owner value = value.Owner
    let identity value = value.Identity
    let variables value = value.Variables
    let assertions value = value.Assertions
    let objectives value = value.Objectives

type ConstraintModel = Map<string, ConstraintValue>

type ConstraintObjectiveValue =
    { ObjectiveId: string
      Value: ConstraintValue }

[<RequireQualifiedAccess>]
type ConstraintSolverOutcome =
    | Satisfiable of model: ConstraintModel
    | Unsatisfiable of coreAssertionIds: string list
    | Unknown of reason: string
    | TimedOut
    | Optimal of model: ConstraintModel * objectives: ConstraintObjectiveValue list
    | Unbounded of objectiveId: string
    | ResourceLimitExceeded of ReasoningResourceLimit * limit: int64 * observedAtLeast: int64
    | Failed of code: string * detail: string

type ConstraintSolverStatistics =
    { Values: Map<string, string>
      PeakMemoryBytes: int64 option
      Steps: int64 option
      ResponseBytes: int option }

type ConstraintSolverExecution =
    { ExecutionId: Guid
      EngineId: string
      EngineVersion: string
      Problem: ConstraintProblemIdentity
      NormalizedProblem: ReasoningArtifact
      Options: Map<string, string>
      StartedAt: DateTimeOffset
      Duration: TimeSpan
      Outcome: ConstraintSolverOutcome
      Statistics: ConstraintSolverStatistics }
