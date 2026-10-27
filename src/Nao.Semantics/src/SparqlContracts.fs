namespace Nao.Semantics

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
type SparqlReadQueryKind =
    | Select
    | Ask
    | Construct

type SparqlQueryTemplate =
    private
        { Id: string
          Kind: SparqlReadQueryKind
          Text: string
          ParameterNames: Set<string> }

[<RequireQualifiedAccess>]
module SparqlQueryTemplate =
    let private canonical name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name $"{name} must be canonical and non-blank."

    let create id kind text parameterNames =
        canonical (nameof id) id

        if String.IsNullOrWhiteSpace text then
            invalidArg (nameof text) "SPARQL query text cannot be blank."

        parameterNames
        |> Set.iter (fun name ->
            if String.IsNullOrWhiteSpace name || name <> name.Trim() then
                invalidArg (nameof parameterNames) "SPARQL parameter names must be canonical and non-blank.")

        { Id = id
          Kind = kind
          Text = text
          ParameterNames = parameterNames }

    let id value = value.Id
    let kind value = value.Kind
    let text value = value.Text
    let parameterNames value = value.ParameterNames

[<RequireQualifiedAccess>]
type SparqlParameterValue =
    | Iri of RdfIri
    | Literal of RdfLiteral

type SparqlQueryBudget =
    private
        { Timeout: TimeSpan
          MaxQueryTextLength: int
          MaxDatasetStatements: int
          MaxNamedGraphs: int
          MaxTriplePatterns: int
          MaxResultItems: int }

[<RequireQualifiedAccess>]
module SparqlQueryBudget =
    let create timeout maxQueryTextLength maxDatasetStatements maxNamedGraphs maxTriplePatterns maxResultItems =
        if timeout < TimeSpan.FromMilliseconds 1.0 then
            invalidArg (nameof timeout) "SPARQL timeout must be at least one millisecond."

        if maxQueryTextLength <= 0 then
            invalidArg (nameof maxQueryTextLength) "SPARQL query-text limit must be positive."

        if maxDatasetStatements <= 0 then
            invalidArg (nameof maxDatasetStatements) "SPARQL dataset-statement limit must be positive."

        if maxNamedGraphs <= 0 then
            invalidArg (nameof maxNamedGraphs) "SPARQL named-graph limit must be positive."

        if maxTriplePatterns <= 0 then
            invalidArg (nameof maxTriplePatterns) "SPARQL triple-pattern limit must be positive."

        if maxResultItems <= 0 then
            invalidArg (nameof maxResultItems) "SPARQL result limit must be positive."

        { Timeout = timeout
          MaxQueryTextLength = maxQueryTextLength
          MaxDatasetStatements = maxDatasetStatements
          MaxNamedGraphs = maxNamedGraphs
          MaxTriplePatterns = maxTriplePatterns
          MaxResultItems = maxResultItems }

    let timeout value = value.Timeout
    let maxQueryTextLength value = value.MaxQueryTextLength
    let maxDatasetStatements value = value.MaxDatasetStatements
    let maxNamedGraphs value = value.MaxNamedGraphs
    let maxTriplePatterns value = value.MaxTriplePatterns
    let maxResultItems value = value.MaxResultItems

type SparqlReadRequest =
    private
        { Template: SparqlQueryTemplate
          Parameters: Map<string, SparqlParameterValue>
          Budget: SparqlQueryBudget }

[<RequireQualifiedAccess>]
module SparqlReadRequest =
    let create template parameters budget =
        let expected = SparqlQueryTemplate.parameterNames template
        let supplied = parameters |> Map.keys |> Set.ofSeq

        if supplied <> expected then
            let missing = Set.difference expected supplied |> String.concat ", "
            let unexpected = Set.difference supplied expected |> String.concat ", "

            invalidArg
                (nameof parameters)
                $"SPARQL parameter bindings must match the template exactly. Missing: [{missing}]. Unexpected: [{unexpected}]."

        { Template = template
          Parameters = parameters
          Budget = budget }

    let template value = value.Template
    let parameters value = value.Parameters
    let budget value = value.Budget

type SparqlBindingRow = Map<string, RdfTerm option>

type SparqlConstructedStatement =
    { Subject: RdfResource
      Predicate: RdfIri
      Object: RdfTerm }

[<RequireQualifiedAccess>]
type SparqlReadResult =
    | Select of variables: string list * rows: SparqlBindingRow list
    | Ask of bool
    | Construct of SparqlConstructedStatement list

type SparqlExecutionProvenance =
    { EndpointId: string
      EndpointVersion: string
      DatasetId: string
      DatasetRevision: int64
      QueryId: string
      StartedAt: DateTimeOffset
      Duration: TimeSpan }

[<RequireQualifiedAccess>]
type SparqlExecutionOutcome =
    | Completed of SparqlReadResult
    | Denied
    | TimedOut
    | QueryTextLimitExceeded of limit: int * observed: int
    | QueryComplexityLimitExceeded of limit: int * observed: int
    | ResultLimitExceeded of limit: int * observedAtLeast: int
    | DatasetLimitExceeded of limit: int * observed: int
    | NamedGraphLimitExceeded of limit: int * observed: int
    | ResponseSizeLimitExceeded of limitBytes: int * observedBytes: int
    | RemoteFailure of statusCode: int

type SparqlExecution =
    { Provenance: SparqlExecutionProvenance
      Permission: PermissionDecisionEvidence option
      Outcome: SparqlExecutionOutcome }

type SparqlReadEndpoint =
    { ExecuteAsync: AuthorizationScope -> RdfDataset -> SparqlReadRequest -> CancellationToken -> Task<SparqlExecution> }
