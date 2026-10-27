namespace Nao.Semantics

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

type SparqlRemoteDataset =
    private
        { Owner: AuthorizationIdentity
          Id: string
          Revision: int64
          StatementCount: int
          DefaultGraphs: Set<RdfIri>
          NamedGraphs: Set<RdfIri> }

[<RequireQualifiedAccess>]
module SparqlRemoteDataset =
    let create owner id revision statementCount defaultGraphs namedGraphs =
        if String.IsNullOrWhiteSpace id || id <> id.Trim() then
            invalidArg (nameof id) "Remote SPARQL dataset identity must be canonical and non-blank."

        if revision <= 0L then
            invalidArg (nameof revision) "Remote SPARQL dataset revision must be positive."

        if statementCount < 0 then
            invalidArg (nameof statementCount) "Remote SPARQL dataset statement count cannot be negative."

        if Set.isEmpty defaultGraphs && Set.isEmpty namedGraphs then
            invalidArg
                (nameof defaultGraphs)
                "Remote SPARQL access requires an explicit default or named graph selection."

        { Owner = owner
          Id = id
          Revision = revision
          StatementCount = statementCount
          DefaultGraphs = defaultGraphs
          NamedGraphs = namedGraphs }

    let requireOwner authorization dataset =
        if dataset.Owner <> AuthorizationScope.identity authorization then
            raise (UnauthorizedAccessException "Remote SPARQL dataset access cannot cross authorization boundaries.")

        dataset

    let id value = value.Id
    let revision value = value.Revision
    let statementCount value = value.StatementCount
    let defaultGraphs value = value.DefaultGraphs
    let namedGraphs value = value.NamedGraphs

type SparqlRemoteTransportRequest =
    { Endpoint: Uri
      ContentType: string
      Accept: string
      Body: string
      Timeout: TimeSpan }

type SparqlRemoteTransportResponse =
    { StatusCode: int
      ContentType: string
      Body: string
      FinalEndpoint: Uri }

type SparqlRemoteTransport =
    private
        { SendAsync: SparqlRemoteTransportRequest -> CancellationToken -> Task<SparqlRemoteTransportResponse> }

[<RequireQualifiedAccess>]
module SparqlRemoteTransport =
    let create sendAsync = { SendAsync = sendAsync }

    let sendAsync request cancellationToken transport =
        transport.SendAsync request cancellationToken

type SparqlRemoteAuthorizer =
    private
        { AuthorizeAsync: AuthorizationScope -> ResourceAccess -> CancellationToken -> Task<PermissionDecisionEvidence> }

[<RequireQualifiedAccess>]
module SparqlRemoteAuthorizer =
    let create authorizeAsync = { AuthorizeAsync = authorizeAsync }

    let authorizeAsync authorization access cancellationToken value =
        value.AuthorizeAsync authorization access cancellationToken

type SparqlRemoteReadEndpoint =
    { ExecuteAsync:
        AuthorizationScope -> SparqlRemoteDataset -> SparqlReadRequest -> CancellationToken -> Task<SparqlExecution> }
