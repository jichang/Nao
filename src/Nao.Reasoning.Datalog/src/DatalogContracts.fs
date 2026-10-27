namespace Nao.Reasoning.Datalog

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents
open Nao.Reasoning

[<RequireQualifiedAccess>]
type DatalogType =
    | Symbol
    | Signed
    | Unsigned
    | Float

[<RequireQualifiedAccess>]
type DatalogValue =
    | Symbol of string
    | Signed of int64
    | Unsigned of uint64
    | Float of double

[<RequireQualifiedAccess>]
module DatalogValue =
    let valueType =
        function
        | DatalogValue.Symbol _ -> DatalogType.Symbol
        | DatalogValue.Signed _ -> DatalogType.Signed
        | DatalogValue.Unsigned _ -> DatalogType.Unsigned
        | DatalogValue.Float _ -> DatalogType.Float

    let validate =
        function
        | DatalogValue.Symbol value when isNull value -> nullArg (nameof value)
        | DatalogValue.Float value when Double.IsNaN value || Double.IsInfinity value ->
            invalidArg (nameof value) "Datalog floating-point values must be finite."
        | _ -> ()

type DatalogColumn = { Name: string; Type: DatalogType }

[<RequireQualifiedAccess>]
type DatalogRelationKind =
    | Extensional
    | Derived
    | HostMaterialized

type DatalogRelation =
    { Name: string
      Columns: DatalogColumn list
      Kind: DatalogRelationKind }

[<RequireQualifiedAccess>]
type DatalogTerm =
    | Variable of string
    | Constant of DatalogValue

type DatalogAtom =
    { Relation: string
      Terms: DatalogTerm list }

[<RequireQualifiedAccess>]
type DatalogLiteral =
    | Positive of DatalogAtom
    | Negative of DatalogAtom

type DatalogFact =
    { Id: string
      Relation: string
      Values: DatalogValue list
      Citations: ReasoningCitation list }

type DatalogRule =
    { Id: string
      Head: DatalogAtom
      Body: DatalogLiteral list
      Citations: ReasoningCitation list }

type DatalogQuery =
    { Atom: DatalogAtom
      OutputVariables: string list }

type DatalogRuleSetIdentity =
    { Id: string
      Version: string
      Digest: string }

type DatalogProgram =
    private
        { Owner: AuthorizationIdentity
          Identity: DatalogRuleSetIdentity
          Relations: DatalogRelation list
          Facts: DatalogFact list
          Rules: DatalogRule list
          Query: DatalogQuery }

[<RequireQualifiedAccess>]
module DatalogProgram =
    let internal validated authorization identity relations facts rules query =
        { Owner = AuthorizationScope.identity authorization
          Identity = identity
          Relations = relations
          Facts = facts
          Rules = rules
          Query = query }

    let requireOwner authorization program =
        if program.Owner <> AuthorizationScope.identity authorization then
            raise (UnauthorizedAccessException "Datalog program cannot cross authorization boundaries.")

        program

    let owner value = value.Owner
    let identity value = value.Identity
    let relations value = value.Relations
    let facts value = value.Facts
    let rules value = value.Rules
    let query value = value.Query

type DatalogHostRelationRequest =
    { Relation: DatalogRelation
      MaxFacts: int }

type DatalogHostRelationProvider =
    { RelationName: string
      ResolveAsync: AuthorizationScope -> DatalogHostRelationRequest -> CancellationToken -> Task<DatalogFact list> }

type DatalogBinding = Map<string, DatalogValue>

type DatalogDerivation =
    { RuleId: string
      PremiseFactIds: string list }

type DatalogQueryRow =
    { Binding: DatalogBinding
      Derivations: DatalogDerivation list }

[<RequireQualifiedAccess>]
type DatalogExecutionOutcome =
    | Completed of DatalogQueryRow list
    | TimedOut
    | ResourceLimitExceeded of ReasoningResourceLimit * limit: int64 * observedAtLeast: int64
    | RunnerFailure of code: string * detail: string

type DatalogExecutionProvenance =
    { ExecutionId: Guid
      EngineId: string
      EngineVersion: string
      RuleSet: DatalogRuleSetIdentity
      StartedAt: DateTimeOffset
      Duration: TimeSpan }

type DatalogExecution =
    { Provenance: DatalogExecutionProvenance
      Outcome: DatalogExecutionOutcome }
