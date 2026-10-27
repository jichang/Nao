namespace Nao.Semantics

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

type SparqlUpdateTemplate =
    private
        { Id: string
          Text: string
          ParameterNames: Set<string> }

[<RequireQualifiedAccess>]
module SparqlUpdateTemplate =
    let create id text parameterNames =
        if String.IsNullOrWhiteSpace id || id <> id.Trim() then
            invalidArg (nameof id) "SPARQL update identity must be canonical and non-blank."

        if String.IsNullOrWhiteSpace text then
            invalidArg (nameof text) "SPARQL update text cannot be blank."

        parameterNames
        |> Set.iter (fun name ->
            if String.IsNullOrWhiteSpace name || name <> name.Trim() then
                invalidArg (nameof parameterNames) "SPARQL parameter names must be canonical and non-blank.")

        { Id = id
          Text = text
          ParameterNames = parameterNames }

    let id value = value.Id
    let text value = value.Text
    let parameterNames value = value.ParameterNames

type SparqlUpdateBudget =
    private
        { Timeout: TimeSpan
          MaxUpdateTextLength: int
          MaxDatasetStatements: int
          MaxNamedGraphs: int
          MaxCommands: int
          MaxChangedStatements: int }

[<RequireQualifiedAccess>]
module SparqlUpdateBudget =
    let create timeout maxUpdateTextLength maxDatasetStatements maxNamedGraphs maxCommands maxChangedStatements =
        if timeout < TimeSpan.FromMilliseconds 1.0 then
            invalidArg (nameof timeout) "SPARQL update timeout must be at least one millisecond."

        [ nameof maxUpdateTextLength, maxUpdateTextLength
          nameof maxDatasetStatements, maxDatasetStatements
          nameof maxNamedGraphs, maxNamedGraphs
          nameof maxCommands, maxCommands
          nameof maxChangedStatements, maxChangedStatements ]
        |> List.iter (fun (name, value) ->
            if value <= 0 then
                invalidArg name $"{name} must be positive.")

        { Timeout = timeout
          MaxUpdateTextLength = maxUpdateTextLength
          MaxDatasetStatements = maxDatasetStatements
          MaxNamedGraphs = maxNamedGraphs
          MaxCommands = maxCommands
          MaxChangedStatements = maxChangedStatements }

    let timeout value = value.Timeout
    let maxUpdateTextLength value = value.MaxUpdateTextLength
    let maxDatasetStatements value = value.MaxDatasetStatements
    let maxNamedGraphs value = value.MaxNamedGraphs
    let maxCommands value = value.MaxCommands
    let maxChangedStatements value = value.MaxChangedStatements

type SparqlUpdateRequest =
    private
        { Template: SparqlUpdateTemplate
          Parameters: Map<string, SparqlParameterValue>
          ExpectedDatasetRevision: int64
          WritableGraphs: Set<RdfGraphName option>
          Budget: SparqlUpdateBudget }

[<RequireQualifiedAccess>]
module SparqlUpdateRequest =
    let create template parameters expectedDatasetRevision writableGraphs budget =
        let expected = SparqlUpdateTemplate.parameterNames template
        let supplied = parameters |> Map.keys |> Set.ofSeq

        if supplied <> expected then
            invalidArg (nameof parameters) "SPARQL update parameter bindings must match the template exactly."

        if expectedDatasetRevision <= 0L then
            invalidArg (nameof expectedDatasetRevision) "SPARQL update requires a positive expected dataset revision."

        if Set.isEmpty writableGraphs then
            invalidArg (nameof writableGraphs) "SPARQL update requires at least one writable graph."

        { Template = template
          Parameters = parameters
          ExpectedDatasetRevision = expectedDatasetRevision
          WritableGraphs = writableGraphs
          Budget = budget }

    let template value = value.Template
    let parameters value = value.Parameters
    let expectedDatasetRevision value = value.ExpectedDatasetRevision
    let writableGraphs value = value.WritableGraphs
    let budget value = value.Budget

type SparqlUpdateAuthorizer =
    private
        { AuthorizeAsync: AuthorizationScope -> ResourceAccess -> CancellationToken -> Task<PermissionDecisionEvidence> }

[<RequireQualifiedAccess>]
module SparqlUpdateAuthorizer =
    let create authorizeAsync = { AuthorizeAsync = authorizeAsync }

    let authorizeAsync authorization access cancellationToken value =
        value.AuthorizeAsync authorization access cancellationToken

type SparqlUpdateProvenance =
    { EndpointId: string
      EndpointVersion: string
      DatasetId: string
      PriorDatasetRevision: int64
      ResultDatasetRevision: int64 option
      OperationId: string
      StartedAt: DateTimeOffset
      Duration: TimeSpan }

[<RequireQualifiedAccess>]
type SparqlUpdateOutcome =
    | Applied of dataset: RdfDataset * insertedStatements: int * deletedStatements: int
    | Denied
    | RevisionConflict of expected: int64 * actual: int64
    | TimedOut
    | UpdateTextLimitExceeded of limit: int * observed: int
    | DatasetLimitExceeded of limit: int * observed: int
    | NamedGraphLimitExceeded of limit: int * observed: int
    | CommandLimitExceeded of limit: int * observed: int
    | MutationLimitExceeded of limit: int * observed: int
    | GraphScopeViolation of graphs: Set<RdfGraphName option>

type SparqlUpdateExecution =
    { Provenance: SparqlUpdateProvenance
      Permission: PermissionDecisionEvidence
      Outcome: SparqlUpdateOutcome }

type SparqlUpdateEndpoint =
    { ExecuteAsync:
        AuthorizationScope -> RdfDataset -> SparqlUpdateRequest -> CancellationToken -> Task<SparqlUpdateExecution> }
