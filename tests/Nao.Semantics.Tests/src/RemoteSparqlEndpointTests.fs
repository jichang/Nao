namespace Nao.Semantics.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Semantics
open Nao.Semantics.Rdf
open VDS.RDF.Parsing
open VDS.RDF.Query

[<TestClass>]
type RemoteSparqlEndpointTests() =
    let authorization tenant =
        let principal =
            SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse "user") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    let scope = authorization "tenant"
    let endpointUri = Uri "https://sparql.example.test/query"
    let permissionId = Guid.Parse "22222222-2222-2222-2222-222222222222"

    let dataset =
        SparqlRemoteDataset.create
            (AuthorizationScope.identity scope)
            "remote-dataset"
            9L
            20
            (Set.singleton (RdfIri.parse "https://example.test/graphs/default"))
            (Set.singleton (RdfIri.parse "https://example.test/graphs/named"))

    let budget resultLimit =
        SparqlQueryBudget.create (TimeSpan.FromSeconds 5.0) 2_000 100 10 10 resultLimit

    let authorizer decision =
        SparqlRemoteAuthorizer.create (fun _ access _ ->
            Task.FromResult
                { Id = permissionId
                  Access = access
                  Decision = decision
                  Basis =
                    if decision = PermissionDecision.Allow then
                        PermissionDecisionBasis.HostApproval PermissionGrantScope.Operation
                    else
                        PermissionDecisionBasis.HostDenial
                  PolicyVersions = Map.ofList [ PermissionPolicy.identity, PermissionPolicy.currentVersion ]
                  Grants = []
                  EvaluatedAt = DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero) })

    let createEndpoint decision maxResponseBytes sendAsync =
        RemoteSparqlEndpoint.create
            "remote"
            "protocol-1"
            endpointUri
            maxResponseBytes
            TimeProvider.System
            (authorizer decision)
            (SparqlRemoteTransport.create sendAsync)

    let execute
        (endpoint: SparqlRemoteReadEndpoint)
        (remoteDataset: SparqlRemoteDataset)
        (request: SparqlReadRequest)
        : SparqlExecution =
        endpoint.ExecuteAsync scope remoteDataset request CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.SelectUsesSafeBindingsAuthorizedGraphsAndExecutionProvenance() =
        let mutable sent = None

        let transport request _ =
            sent <- Some request

            Task.FromResult
                { StatusCode = 200
                  ContentType = "application/sparql-results+json; charset=utf-8"
                  Body =
                    """{"head":{"vars":["subject"]},"results":{"bindings":[{"subject":{"type":"uri","value":"https://example.test/match"}}]}}"""
                  FinalEndpoint = request.Endpoint }

        let hostile = "\" . ?injected ?predicate ?object . #"

        let template =
            SparqlQueryTemplate.create
                "remote-select"
                SparqlReadQueryKind.Select
                "SELECT ?subject WHERE { ?subject <https://example.test/value> @value }"
                (Set.singleton "value")

        let request =
            SparqlReadRequest.create
                template
                (Map.ofList [ "value", SparqlParameterValue.Literal(RdfLiteral.plain hostile) ])
                (budget 10)

        let execution =
            execute (createEndpoint PermissionDecision.Allow 10_000 transport) dataset request

        match execution.Outcome with
        | SparqlExecutionOutcome.Completed(SparqlReadResult.Select([ "subject" ], [ row ])) ->
            Assert.AreEqual(
                Some(RdfTerm.Resource(RdfResource.Iri(RdfIri.parse "https://example.test/match"))),
                row["subject"]
            )
        | outcome -> Assert.Fail $"Expected one remote SELECT result, got {outcome}."

        let request = sent |> Option.get
        Assert.AreEqual("application/sparql-query; charset=utf-8", request.ContentType)
        Assert.AreEqual("application/sparql-results+json", request.Accept)
        Assert.IsTrue(request.Endpoint.Query.Contains("default-graph-uri=", StringComparison.Ordinal))
        Assert.IsTrue(request.Endpoint.Query.Contains("named-graph-uri=", StringComparison.Ordinal))
        Assert.AreEqual(SparqlQueryType.Select, SparqlQueryParser().ParseFromString(request.Body).QueryType)
        Assert.AreEqual(Some permissionId, execution.Permission |> Option.map _.Id)
        Assert.AreEqual("remote-dataset", execution.Provenance.DatasetId)
        Assert.AreEqual(9L, execution.Provenance.DatasetRevision)
        Assert.AreEqual("remote-select", execution.Provenance.QueryId)

    [<TestMethod>]
    member _.DeniedRequestNeverReachesTransport() =
        let mutable called = false

        let transport _ _ =
            called <- true
            Task.FromException<SparqlRemoteTransportResponse>(InvalidOperationException "must not send")

        let template =
            SparqlQueryTemplate.create "denied" SparqlReadQueryKind.Ask "ASK { ?s ?p ?o }" Set.empty

        let execution =
            SparqlReadRequest.create template Map.empty (budget 1)
            |> execute (createEndpoint PermissionDecision.Deny 10_000 transport) dataset

        Assert.AreEqual(SparqlExecutionOutcome.Denied, execution.Outcome)
        Assert.IsFalse called

    [<TestMethod>]
    member _.FromServiceForeignOwnerAndRedirectEscapeAreRejected() =
        let mutable called = false

        let transport request _ =
            called <- true

            Task.FromResult
                { StatusCode = 200
                  ContentType = "application/sparql-results+json"
                  Body = """{"head":{},"boolean":true}"""
                  FinalEndpoint = Uri "https://other.example.test/query" }

        let endpoint = createEndpoint PermissionDecision.Allow 10_000 transport

        let query text =
            SparqlQueryTemplate.create "restricted" SparqlReadQueryKind.Ask text Set.empty
            |> fun template -> SparqlReadRequest.create template Map.empty (budget 1)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            query "ASK FROM <https://example.test/graphs/other> { ?s ?p ?o }"
            |> execute endpoint dataset
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            query "ASK { SERVICE <https://other.example.test/query> { ?s ?p ?o } }"
            |> execute endpoint dataset
            |> ignore)
        |> ignore

        Assert.IsFalse called

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            query "ASK { ?s ?p ?o }"
            |> execute
                endpoint
                (SparqlRemoteDataset.create
                    (AuthorizationScope.identity (authorization "other"))
                    "foreign"
                    1L
                    0
                    (Set.singleton (RdfIri.parse "https://example.test/graph"))
                    Set.empty)
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            query "ASK { ?s ?p ?o }" |> execute endpoint dataset |> ignore)
        |> ignore

    [<TestMethod>]
    member _.RemoteFailuresResponseLimitsAndContentTypesAreExplicit() =
        let template =
            SparqlQueryTemplate.create "ask" SparqlReadQueryKind.Ask "ASK { ?s ?p ?o }" Set.empty

        let request = SparqlReadRequest.create template Map.empty (budget 1)

        let response status contentType body request =
            Task.FromResult
                { StatusCode = status
                  ContentType = contentType
                  Body = body
                  FinalEndpoint = request.Endpoint }

        let failed =
            execute
                (createEndpoint PermissionDecision.Allow 10_000 (fun request _ ->
                    response 503 "text/plain" "unavailable" request))
                dataset
                request

        Assert.AreEqual(SparqlExecutionOutcome.RemoteFailure 503, failed.Outcome)

        let oversized =
            execute
                (createEndpoint PermissionDecision.Allow 10 (fun request _ ->
                    response 200 "application/sparql-results+json" """{"head":{},"boolean":true}""" request))
                dataset
                request

        match oversized.Outcome with
        | SparqlExecutionOutcome.ResponseSizeLimitExceeded(10, observed) -> Assert.IsTrue(observed > 10)
        | outcome -> Assert.Fail $"Expected a response-size failure, got {outcome}."

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            execute
                (createEndpoint PermissionDecision.Allow 10_000 (fun request _ ->
                    response 200 "text/html" "<html />" request))
                dataset
                request
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.AskAndConstructResponsesRemainTyped() =
        let ask =
            SparqlQueryTemplate.create "ask" SparqlReadQueryKind.Ask "ASK { ?s ?p ?o }" Set.empty
            |> fun template -> SparqlReadRequest.create template Map.empty (budget 1)

        let askExecution =
            execute
                (createEndpoint PermissionDecision.Allow 10_000 (fun request _ ->
                    Task.FromResult
                        { StatusCode = 200
                          ContentType = "application/sparql-results+json"
                          Body = """{"head":{},"boolean":true}"""
                          FinalEndpoint = request.Endpoint }))
                dataset
                ask

        Assert.AreEqual(SparqlExecutionOutcome.Completed(SparqlReadResult.Ask true), askExecution.Outcome)

        let construct =
            SparqlQueryTemplate.create
                "construct"
                SparqlReadQueryKind.Construct
                "CONSTRUCT { ?s <https://example.test/copy> ?o } WHERE { ?s ?p ?o }"
                Set.empty
            |> fun template -> SparqlReadRequest.create template Map.empty (budget 10)

        let constructExecution =
            execute
                (createEndpoint PermissionDecision.Allow 10_000 (fun request _ ->
                    Task.FromResult
                        { StatusCode = 200
                          ContentType = "application/n-triples"
                          Body = "<https://example.test/s> <https://example.test/copy> \"value\" ."
                          FinalEndpoint = request.Endpoint }))
                dataset
                construct

        match constructExecution.Outcome with
        | SparqlExecutionOutcome.Completed(SparqlReadResult.Construct [ statement ]) ->
            Assert.AreEqual(RdfIri.parse "https://example.test/copy", statement.Predicate)
        | outcome -> Assert.Fail $"Expected one remote CONSTRUCT statement, got {outcome}."

    [<TestMethod>]
    member _.RemoteResultAndTransportTimeoutLimitsAreStructured() =
        let template =
            SparqlQueryTemplate.create
                "bounded-select"
                SparqlReadQueryKind.Select
                "SELECT ?subject WHERE { ?subject ?predicate ?object }"
                Set.empty

        let request = SparqlReadRequest.create template Map.empty (budget 1)

        let resultLimited =
            execute
                (createEndpoint PermissionDecision.Allow 10_000 (fun transportRequest _ ->
                    Task.FromResult
                        { StatusCode = 200
                          ContentType = "application/sparql-results+json"
                          Body =
                            """{"head":{"vars":["subject"]},"results":{"bindings":[{"subject":{"type":"uri","value":"https://example.test/one"}},{"subject":{"type":"uri","value":"https://example.test/two"}}]}}"""
                          FinalEndpoint = transportRequest.Endpoint }))
                dataset
                request

        Assert.AreEqual(SparqlExecutionOutcome.ResultLimitExceeded(1, 2), resultLimited.Outcome)

        let timeoutRequest =
            SparqlReadRequest.create
                template
                Map.empty
                (SparqlQueryBudget.create (TimeSpan.FromMilliseconds 1.0) 2_000 100 10 10 10)

        let timeout =
            execute
                (createEndpoint PermissionDecision.Allow 10_000 (fun _ cancellationToken ->
                    task {
                        do! Task.Delay(TimeSpan.FromSeconds 5.0, cancellationToken)
                        return raise (InvalidOperationException "Transport should have timed out.")
                    }))
                dataset
                timeoutRequest

        Assert.AreEqual(SparqlExecutionOutcome.TimedOut, timeout.Outcome)
