namespace Nao.Semantics.Tests

open System
open System.Threading
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Semantics
open Nao.Semantics.Rdf
open VDS.RDF.Parsing

[<TestClass>]
type LocalSparqlEndpointTests() =
    let authorization tenant =
        let principal =
            SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse "user") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    let scope = authorization "tenant"
    let predicate = RdfIri.parse "https://example.test/value"

    let endpoint =
        LocalSparqlEndpoint.create "local-dotnetrdf" "3.5.2" TimeProvider.System

    let statement id subject object' graph =
        { Id = id
          Subject = RdfResource.Iri(RdfIri.parse subject)
          Predicate = predicate
          Object = object'
          Graph = graph
          Origin =
            RdfStatementOrigin.Asserted
                { SourceVersionId = "fixture-v1"
                  EvidenceAddresses = [ $"fixture:{id}" ] } }

    let dataset owner statements =
        RdfDataset.create (AuthorizationScope.identity owner) "dataset" 7L [] statements

    let budget maxDatasetStatements maxNamedGraphs maxTriplePatterns maxResultItems =
        SparqlQueryBudget.create
            (TimeSpan.FromSeconds 5.0)
            2_000
            maxDatasetStatements
            maxNamedGraphs
            maxTriplePatterns
            maxResultItems

    let execute owner data request =
        endpoint.ExecuteAsync owner data request CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.SelectBindsLiteralParametersWithoutQueryInjection() =
        let hostile = "\" . ?injected ?predicate ?object . #"

        let data =
            dataset
                scope
                [ statement "hostile" "https://example.test/hostile" (RdfTerm.Literal(RdfLiteral.plain hostile)) None
                  statement "other" "https://example.test/other" (RdfTerm.Literal(RdfLiteral.plain "other")) None ]

        let template =
            SparqlQueryTemplate.create
                "select-by-value"
                SparqlReadQueryKind.Select
                "SELECT ?subject WHERE { ?subject <https://example.test/value> @value }"
                (Set.singleton "value")

        let request =
            SparqlReadRequest.create
                template
                (Map.ofList [ "value", SparqlParameterValue.Literal(RdfLiteral.plain hostile) ])
                (budget 10 10 10 10)

        let execution = execute scope data request

        match execution.Outcome with
        | SparqlExecutionOutcome.Completed(SparqlReadResult.Select(variables, [ row ])) ->
            Assert.AreEqual([ "subject" ], variables)

            Assert.AreEqual(
                Some(RdfTerm.Resource(RdfResource.Iri(RdfIri.parse "https://example.test/hostile"))),
                row["subject"]
            )
        | outcome -> Assert.Fail $"Expected one safely bound SELECT row, got {outcome}."

        Assert.AreEqual("local-dotnetrdf", execution.Provenance.EndpointId)
        Assert.AreEqual("3.5.2", execution.Provenance.EndpointVersion)
        Assert.AreEqual("dataset", execution.Provenance.DatasetId)
        Assert.AreEqual(7L, execution.Provenance.DatasetRevision)
        Assert.AreEqual("select-by-value", execution.Provenance.QueryId)

    [<TestMethod>]
    member _.AskAndConstructHaveDistinctTypedResults() =
        let data =
            dataset
                scope
                [ statement "source" "https://example.test/subject" (RdfTerm.Literal(RdfLiteral.plain "value")) None ]

        let ask =
            SparqlQueryTemplate.create
                "ask"
                SparqlReadQueryKind.Ask
                "ASK WHERE { <https://example.test/subject> <https://example.test/value> \"value\" }"
                Set.empty

        let askResult =
            SparqlReadRequest.create ask Map.empty (budget 10 10 10 10)
            |> execute scope data

        Assert.AreEqual(SparqlExecutionOutcome.Completed(SparqlReadResult.Ask true), askResult.Outcome)

        let construct =
            SparqlQueryTemplate.create
                "construct"
                SparqlReadQueryKind.Construct
                "CONSTRUCT { ?subject <https://example.test/copy> ?value } WHERE { ?subject <https://example.test/value> ?value }"
                Set.empty

        let constructResult =
            SparqlReadRequest.create construct Map.empty (budget 10 10 10 10)
            |> execute scope data

        match constructResult.Outcome with
        | SparqlExecutionOutcome.Completed(SparqlReadResult.Construct [ result ]) ->
            Assert.AreEqual(RdfIri.parse "https://example.test/copy", result.Predicate)

            Assert.AreEqual(
                RdfTerm.Literal(RdfLiteral.typed "value" (RdfIri.parse "http://www.w3.org/2001/XMLSchema#string")),
                result.Object
            )
        | outcome -> Assert.Fail $"Expected one constructed statement, got {outcome}."

    [<TestMethod>]
    member _.ResultDatasetGraphAndComplexityLimitsAreStructured() =
        let graph name =
            Some(RdfGraphName.Iri(RdfIri.parse $"https://example.test/graphs/{name}"))

        let data =
            dataset
                scope
                [ statement "one" "https://example.test/one" (RdfTerm.Literal(RdfLiteral.plain "1")) (graph "one")
                  statement "two" "https://example.test/two" (RdfTerm.Literal(RdfLiteral.plain "2")) (graph "two") ]

        let select text =
            SparqlQueryTemplate.create "bounded" SparqlReadQueryKind.Select text Set.empty
            |> fun template -> SparqlReadRequest.create template Map.empty

        let datasetLimited =
            select "SELECT * WHERE { GRAPH ?graph { ?subject ?predicate ?object } }" (budget 1 10 10 10)
            |> execute scope data

        Assert.AreEqual(SparqlExecutionOutcome.DatasetLimitExceeded(1, 2), datasetLimited.Outcome)

        let graphLimited =
            select "SELECT * WHERE { GRAPH ?graph { ?subject ?predicate ?object } }" (budget 10 1 10 10)
            |> execute scope data

        Assert.AreEqual(SparqlExecutionOutcome.NamedGraphLimitExceeded(1, 2), graphLimited.Outcome)

        let complexityLimited =
            select
                "SELECT * WHERE { GRAPH ?graph { ?subject ?predicate ?object . ?subject ?predicate2 ?object2 } }"
                (budget 10 10 1 10)
            |> execute scope data

        Assert.AreEqual(SparqlExecutionOutcome.QueryComplexityLimitExceeded(1, 2), complexityLimited.Outcome)

        let resultLimited =
            select "SELECT * WHERE { GRAPH ?graph { ?subject ?predicate ?object } }" (budget 10 10 10 1)
            |> execute scope data

        Assert.AreEqual(SparqlExecutionOutcome.ResultLimitExceeded(1, 2), resultLimited.Outcome)

    [<TestMethod>]
    member _.ReadEndpointRejectsUpdatesServicesKindMismatchAndForeignDatasets() =
        let data = dataset scope []

        let query kind text =
            SparqlQueryTemplate.create "invalid" kind text Set.empty
            |> fun template -> SparqlReadRequest.create template Map.empty (budget 10 10 10 10)

        Assert.ThrowsExactly<RdfParseException>(fun () ->
            query SparqlReadQueryKind.Select "DELETE WHERE { ?subject ?predicate ?object }"
            |> execute scope data
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            query SparqlReadQueryKind.Ask "SELECT * WHERE { ?subject ?predicate ?object }"
            |> execute scope data
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            query
                SparqlReadQueryKind.Select
                "SELECT * WHERE { SERVICE <https://example.test/sparql> { ?subject ?predicate ?object } }"
            |> execute scope data
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            query SparqlReadQueryKind.Select "SELECT * WHERE { ?subject ?predicate ?object }"
            |> execute (authorization "other") data
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.BindingsAndBudgetsFailClosedBeforeExecution() =
        let template =
            SparqlQueryTemplate.create
                "parameterized"
                SparqlReadQueryKind.Select
                "SELECT * WHERE { ?subject <https://example.test/value> @value }"
                (Set.singleton "value")

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            SparqlReadRequest.create template Map.empty (budget 10 10 10 10) |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            SparqlQueryBudget.create (TimeSpan.FromTicks 1L) 100 100 10 10 10 |> ignore)
        |> ignore

    [<TestMethod>]
    member _.EngineTimeoutIsStructuredAndAuditable() =
        let statements =
            [ 1..200 ]
            |> List.map (fun index ->
                statement
                    $"statement-{index}"
                    $"https://example.test/subjects/{index}"
                    (RdfTerm.Literal(RdfLiteral.plain (string index)))
                    None)

        let data = dataset scope statements

        let template =
            SparqlQueryTemplate.create
                "timeout"
                SparqlReadQueryKind.Select
                "SELECT (COUNT(*) AS ?count) WHERE { ?a <https://example.test/value> ?x . ?b <https://example.test/value> ?y . ?c <https://example.test/value> ?z }"
                Set.empty

        let timeoutBudget =
            SparqlQueryBudget.create (TimeSpan.FromMilliseconds 1.0) 2_000 500 10 10 10

        let execution =
            SparqlReadRequest.create template Map.empty timeoutBudget |> execute scope data

        Assert.AreEqual(SparqlExecutionOutcome.TimedOut, execution.Outcome)
        Assert.AreEqual("timeout", execution.Provenance.QueryId)
        Assert.IsTrue(execution.Provenance.Duration > TimeSpan.Zero)
