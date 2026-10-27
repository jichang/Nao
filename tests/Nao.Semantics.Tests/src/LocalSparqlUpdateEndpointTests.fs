namespace Nao.Semantics.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Semantics
open Nao.Semantics.Rdf

[<TestClass>]
type LocalSparqlUpdateEndpointTests() =
    let authorization tenant =
        let principal =
            SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse "user") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    let scope = authorization "tenant"
    let permissionId = Guid.Parse "11111111-1111-1111-1111-111111111111"
    let predicate = RdfIri.parse "https://example.test/value"

    let authorizer decision =
        SparqlUpdateAuthorizer.create (fun _ access _ ->
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

    let endpoint decision =
        LocalSparqlUpdateEndpoint.create "local-dotnetrdf-update" "3.5.2" TimeProvider.System (authorizer decision)

    let sourceStatement id =
        { Id = id
          Subject = RdfResource.Iri(RdfIri.parse "https://example.test/existing")
          Predicate = predicate
          Object = RdfTerm.Literal(RdfLiteral.plain "existing")
          Graph = None
          Origin =
            RdfStatementOrigin.Asserted
                { SourceVersionId = "fixture-v1"
                  EvidenceAddresses = [ $"fixture:{id}" ] } }

    let dataset statements =
        RdfDataset.create (AuthorizationScope.identity scope) "dataset" 1L [] statements

    let budget changed =
        SparqlUpdateBudget.create (TimeSpan.FromSeconds 5.0) 2_000 1_000 20 10 changed

    let request text parameters writableGraphs updateBudget =
        SparqlUpdateTemplate.create "operation-1" text (parameters |> Map.keys |> Set.ofSeq)
        |> fun template -> SparqlUpdateRequest.create template parameters 1L writableGraphs updateBudget

    let execute
        (updateEndpoint: SparqlUpdateEndpoint)
        (data: RdfDataset)
        (updateRequest: SparqlUpdateRequest)
        : SparqlUpdateExecution =
        updateEndpoint.ExecuteAsync scope data updateRequest CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.AuthorizedParameterizedInsertReturnsVersionedReplacementWithUpdateProvenance() =
        let hostile =
            "\" . <https://example.test/injected> <https://example.test/value> \"bad"

        let data = dataset [ sourceStatement "source-1"; sourceStatement "source-2" ]

        let updateRequest =
            request
                "INSERT DATA { <https://example.test/inserted> <https://example.test/value> @value }"
                (Map.ofList [ "value", SparqlParameterValue.Literal(RdfLiteral.plain hostile) ])
                (Set.singleton None)
                (budget 10)

        let execution = execute (endpoint PermissionDecision.Allow) data updateRequest

        match execution.Outcome with
        | SparqlUpdateOutcome.Applied(replacement, 1, 0) ->
            Assert.AreEqual(2L, RdfDataset.revision replacement)
            Assert.AreEqual(3, RdfDataset.statements replacement |> List.length)

            let inserted =
                RdfDataset.statements replacement
                |> List.find (fun statement ->
                    statement.Subject = RdfResource.Iri(RdfIri.parse "https://example.test/inserted"))

            Assert.AreEqual(
                RdfTerm.Literal(RdfLiteral.typed hostile (RdfIri.parse "http://www.w3.org/2001/XMLSchema#string")),
                inserted.Object
            )

            match inserted.Origin with
            | RdfStatementOrigin.AuthorizedUpdate provenance ->
                Assert.AreEqual("operation-1", provenance.OperationId)
                Assert.AreEqual("local-dotnetrdf-update", provenance.EndpointId)
                Assert.AreEqual(permissionId, provenance.PermissionDecisionId)
                Assert.AreEqual(1L, provenance.PriorDatasetRevision)
            | origin -> Assert.Fail $"Expected authorized-update provenance, got {origin}."

            CollectionAssert.AreEquivalent(
                [| "source-1"; "source-2" |],
                RdfDataset.statements replacement
                |> List.filter (fun statement ->
                    statement.Subject = RdfResource.Iri(RdfIri.parse "https://example.test/existing"))
                |> List.map _.Id
                |> List.toArray
            )
        | outcome -> Assert.Fail $"Expected an applied update, got {outcome}."

        Assert.AreEqual(PermissionDecision.Allow, execution.Permission.Decision)
        Assert.AreEqual(Some 2L, execution.Provenance.ResultDatasetRevision)

    [<TestMethod>]
    member _.DeniedUpdateDoesNotParseOrMutateInput() =
        let data = dataset [ sourceStatement "source" ]

        let updateRequest =
            request "not valid SPARQL" Map.empty (Set.singleton None) (budget 10)

        let execution = execute (endpoint PermissionDecision.Deny) data updateRequest

        Assert.AreEqual(SparqlUpdateOutcome.Denied, execution.Outcome)
        Assert.AreEqual(1L, RdfDataset.revision data)
        Assert.AreEqual(1, RdfDataset.statements data |> List.length)

    [<TestMethod>]
    member _.GraphScopeRevisionAndMutationLimitsFailAtomically() =
        let data = dataset [ sourceStatement "source" ]

        let allowedGraph =
            RdfGraphName.Iri(RdfIri.parse "https://example.test/graphs/allowed")

        let forbiddenGraph =
            RdfGraphName.Iri(RdfIri.parse "https://example.test/graphs/forbidden")

        let graphRequest =
            request
                "INSERT DATA { GRAPH <https://example.test/graphs/forbidden> { <https://example.test/new> <https://example.test/value> \"new\" } }"
                Map.empty
                (Set.singleton (Some allowedGraph))
                (budget 10)

        let graphExecution = execute (endpoint PermissionDecision.Allow) data graphRequest

        Assert.AreEqual(
            SparqlUpdateOutcome.GraphScopeViolation(Set.singleton (Some forbiddenGraph)),
            graphExecution.Outcome
        )

        let mutationRequest =
            request
                "INSERT DATA { <https://example.test/new-1> <https://example.test/value> \"1\" . <https://example.test/new-2> <https://example.test/value> \"2\" }"
                Map.empty
                (Set.singleton None)
                (budget 1)

        let mutationExecution =
            execute (endpoint PermissionDecision.Allow) data mutationRequest

        Assert.AreEqual(SparqlUpdateOutcome.MutationLimitExceeded(1, 2), mutationExecution.Outcome)

        let conflictTemplate =
            SparqlUpdateTemplate.create
                "conflict"
                "INSERT DATA { <https://example.test/new> <https://example.test/value> \"new\" }"
                Set.empty

        let conflictRequest =
            SparqlUpdateRequest.create conflictTemplate Map.empty 2L (Set.singleton None) (budget 10)

        let conflictExecution =
            execute (endpoint PermissionDecision.Allow) data conflictRequest

        Assert.AreEqual(SparqlUpdateOutcome.RevisionConflict(2L, 1L), conflictExecution.Outcome)

        Assert.AreEqual(1L, RdfDataset.revision data)
        Assert.AreEqual(1, RdfDataset.statements data |> List.length)

    [<TestMethod>]
    member _.RemoteAndUnrepresentableGraphOperationsAreRejected() =
        let data = dataset []

        let assertRejected text =
            let updateRequest = request text Map.empty (Set.singleton None) (budget 10)

            Assert.ThrowsExactly<ArgumentException>(fun () ->
                execute (endpoint PermissionDecision.Allow) data updateRequest |> ignore)
            |> ignore

        assertRejected "LOAD <https://example.test/data>"
        assertRejected "CREATE GRAPH <https://example.test/graph>"

        assertRejected
            "INSERT { ?subject <https://example.test/value> ?object } WHERE { SERVICE <https://example.test/sparql> { ?subject ?predicate ?object } }"

    [<TestMethod>]
    member _.PermissionEvidenceMustMatchRequestedDatasetAccess() =
        let mismatched =
            SparqlUpdateAuthorizer.create (fun _ _ _ ->
                Task.FromResult
                    { Id = permissionId
                      Access = ResourceAccess.knowledge "other" KnowledgeAction.Index
                      Decision = PermissionDecision.Allow
                      Basis = PermissionDecisionBasis.HostApproval PermissionGrantScope.Operation
                      PolicyVersions = Map.empty
                      Grants = []
                      EvaluatedAt = DateTimeOffset.UtcNow })

        let updateEndpoint =
            LocalSparqlUpdateEndpoint.create "local-dotnetrdf-update" "3.5.2" TimeProvider.System mismatched

        let updateRequest =
            request
                "INSERT DATA { <https://example.test/new> <https://example.test/value> \"new\" }"
                Map.empty
                (Set.singleton None)
                (budget 10)

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            execute updateEndpoint (dataset []) updateRequest |> ignore)
        |> ignore

    [<TestMethod>]
    member _.CommandAndTimeoutBudgetsAreStructured() =
        let data = dataset []

        let commandBudget =
            SparqlUpdateBudget.create (TimeSpan.FromSeconds 5.0) 2_000 100 10 1 10

        let commandRequest =
            request
                "INSERT DATA { <https://example.test/one> <https://example.test/value> \"1\" }; INSERT DATA { <https://example.test/two> <https://example.test/value> \"2\" }"
                Map.empty
                (Set.singleton None)
                commandBudget

        let commandExecution =
            execute (endpoint PermissionDecision.Allow) data commandRequest

        Assert.AreEqual(SparqlUpdateOutcome.CommandLimitExceeded(1, 2), commandExecution.Outcome)

        let statements =
            [ 1..150 ]
            |> List.map (fun index ->
                { sourceStatement $"source-{index}" with
                    Subject = RdfResource.Iri(RdfIri.parse $"https://example.test/subjects/{index}") })

        let timeoutBudget =
            SparqlUpdateBudget.create (TimeSpan.FromMilliseconds 1.0) 2_000 500 10 10 50_000

        let timeoutRequest =
            request
                "INSERT { ?a <https://example.test/copy> ?b } WHERE { ?a <https://example.test/value> ?x . ?b <https://example.test/value> ?y . ?c <https://example.test/value> ?z }"
                Map.empty
                (Set.singleton None)
                timeoutBudget

        let timeoutExecution =
            execute (endpoint PermissionDecision.Allow) (dataset statements) timeoutRequest

        Assert.AreEqual(SparqlUpdateOutcome.TimedOut, timeoutExecution.Outcome)
