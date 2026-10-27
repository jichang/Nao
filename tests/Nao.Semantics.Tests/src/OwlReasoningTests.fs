namespace Nao.Semantics.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Semantics
open Nao.Semantics.Rdf

[<TestClass>]
type OwlReasoningTests() =
    let digest character = String(character, 64)

    let authorization tenant =
        let principal =
            SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse "user") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    let scope = authorization "tenant"
    let ontologyIri = RdfIri.parse "https://example.test/ontology"
    let versionIri = RdfIri.parse "https://example.test/ontology/1"

    let descriptor =
        OntologyDescriptor.create ontologyIri versionIri Set.empty Map.empty

    let ontology contentDigest =
        OwlOntologyIdentity.create descriptor "dataset" 1L contentDigest Map.empty

    let configuration configDigest =
        OwlReasonerConfiguration.create "elk-config" "1" configDigest (Map.ofList [ "workers", "2" ])

    let budget input output proof response timeout =
        OwlReasoningBudget.create timeout input output proof response

    let source id subject predicate objectValue =
        { Id = id
          Subject = RdfResource.Iri(RdfIri.parse subject)
          Predicate = RdfIri.parse predicate
          Object = RdfTerm.Resource(RdfResource.Iri(RdfIri.parse objectValue))
          Graph = None
          Origin =
            RdfStatementOrigin.Asserted
                { SourceVersionId = "fixture-v1"
                  EvidenceAddresses = [ $"fixture:{id}" ] } }

    let conclusion id subject predicate objectValue =
        { Id = id
          Subject = RdfResource.Iri(RdfIri.parse subject)
          Predicate = RdfIri.parse predicate
          Object = RdfTerm.Resource(RdfResource.Iri(RdfIri.parse objectValue))
          Graph = None
          Origin =
            RdfStatementOrigin.Inferred
                { EngineId = "elk"
                  EngineVersion = "0.6.0"
                  RuleOrAxiomId = "owl-el"
                  PremiseStatementIds = [ "source-1" ] } }

    let sourceStatement =
        source
            "source-1"
            "https://example.test/Dog"
            "http://www.w3.org/2000/01/rdf-schema#subClassOf"
            "https://example.test/Animal"

    let data owner statements =
        RdfDataset.create (AuthorizationScope.identity owner) "dataset" 1L [] statements

    let request profile operation reasoningBudget contentDigest configDigest =
        OwlReasoningRequest.create
            profile
            (ontology contentDigest)
            (configuration configDigest)
            operation
            reasoningBudget

    let response contentDigest configDigest bytes outcome =
        { EngineId = "elk"
          EngineVersion = "0.6.0"
          ConfigurationDigest = configDigest
          OntologyContentDigest = contentDigest
          ResponseBytes = bytes
          Outcome = outcome }

    let endpoint cache sendAsync =
        ElkReasonerEndpoint.create
            "0.6.0"
            OwlProofCapability.DerivationGraph
            TimeProvider.System
            cache
            (ElkReasonerTransport.create sendAsync)

    let execute (reasoner: OwlReasoningEndpoint) owner dataset reasoningRequest =
        reasoner.ExecuteAsync owner dataset reasoningRequest CancellationToken.None
        |> _.GetAwaiter().GetResult()

    let standardBudget = budget 100 100 100 10_000 (TimeSpan.FromSeconds 2.0)

    [<TestMethod>]
    member _.ContractsRequireExactProfilesImportsAndImmutableDigests() =
        let capabilities =
            OwlReasonerCapabilities.create
                "elk"
                "0.6.0"
                (Set.singleton OwlProfile.Owl2EL)
                (Set.singleton OwlReasoningOperationKind.Classify)
                OwlProofCapability.None
                OwlReasonerExecutionMode.RemoteService

        Assert.AreEqual(Set.singleton OwlProfile.Owl2EL, OwlReasonerCapabilities.profiles capabilities)
        Assert.AreEqual(OwlSemanticAssumptions.Standard, OwlSemanticAssumptions.Standard)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            OwlOntologyIdentity.create descriptor "dataset" 1L "ABC" Map.empty |> ignore)
        |> ignore

        let imported = RdfIri.parse "https://example.test/imported"

        let importing =
            OntologyDescriptor.create ontologyIri versionIri (Set.singleton imported) Map.empty

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            OwlOntologyIdentity.create importing "dataset" 1L (digest 'a') Map.empty
            |> ignore)
        |> ignore

        let importIdentity =
            OwlImportIdentity.create (RdfIri.parse "https://example.test/imported/3") (digest 'c')

        let versioned =
            OwlOntologyIdentity.create importing "dataset" 1L (digest 'a') (Map.ofList [ imported, importIdentity ])

        Assert.AreEqual(
            digest 'c',
            versioned
            |> OwlOntologyIdentity.imports
            |> Map.find imported
            |> OwlImportIdentity.contentDigest
        )

    [<TestMethod>]
    member _.UnsupportedProfileAndInputLimitFailBeforeTransport() =
        let mutable calls = 0

        let reasoner =
            endpoint OwlReasoningCache.disabled (fun _ _ ->
                calls <- calls + 1
                Task.FromResult(response (digest 'a') (digest 'b') 1 OwlReasoningOutcome.TimedOut))

        let dataset = data scope [ sourceStatement ]

        let unsupported =
            request OwlProfile.Owl2DL OwlReasoningOperation.Classify standardBudget (digest 'a') (digest 'b')
            |> execute reasoner scope dataset

        match unsupported.Outcome with
        | OwlReasoningOutcome.Unsupported(OwlReasoningOperationKind.Classify, _) -> ()
        | outcome -> Assert.Fail $"Expected unsupported profile, got {outcome}."

        let limited =
            request
                OwlProfile.Owl2EL
                OwlReasoningOperation.Classify
                (budget 1 100 100 10_000 (TimeSpan.FromSeconds 2.0))
                (digest 'a')
                (digest 'b')
            |> execute reasoner scope (data scope [ sourceStatement; { sourceStatement with Id = "source-2" } ])

        match limited.Outcome with
        | OwlReasoningOutcome.InputLimitExceeded(1, 2) -> ()
        | outcome -> Assert.Fail $"Expected input limit, got {outcome}."

        Assert.AreEqual(0, calls)

    [<TestMethod>]
    member _.OwnerAndOntologyRevisionBoundariesCannotBeCrossed() =
        let mutable calls = 0

        let reasoner =
            endpoint OwlReasoningCache.disabled (fun _ _ ->
                calls <- calls + 1
                Task.FromResult(response (digest 'a') (digest 'b') 1 OwlReasoningOutcome.TimedOut))

        let reasoningRequest =
            request OwlProfile.Owl2EL OwlReasoningOperation.CheckConsistency standardBudget (digest 'a') (digest 'b')

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            execute reasoner (authorization "foreign") (data scope [ sourceStatement ]) reasoningRequest
            |> ignore)
        |> ignore

        let wrongRevision =
            RdfDataset.create (AuthorizationScope.identity scope) "dataset" 2L [] [ sourceStatement ]

        match execute reasoner scope wrongRevision reasoningRequest with
        | { Outcome = OwlReasoningOutcome.Failed("ontology_identity_mismatch", _) } -> ()
        | execution -> Assert.Fail $"Expected ontology identity mismatch, got {execution.Outcome}."

        Assert.AreEqual(0, calls)

    [<TestMethod>]
    member _.ClassificationUsesAssertedInputAndCachesOnlyByImmutableIdentity() =
        let entries = ResizeArray<OwlReasoningCacheKey * OwlReasoningResult>()
        let mutable calls = 0

        let cache =
            { TryGetAsync =
                fun key _ ->
                    entries
                    |> Seq.tryPick (fun (candidate, result) -> if candidate = key then Some result else None)
                    |> Task.FromResult
              StoreAsync =
                fun key result _ ->
                    entries.Add(key, result)
                    Task.CompletedTask }

        let inferred =
            conclusion
                "inferred-1"
                "https://example.test/Dog"
                "http://www.w3.org/2000/01/rdf-schema#subClassOf"
                "https://example.test/LivingThing"

        let reasoner =
            endpoint cache (fun serviceRequest _ ->
                calls <- calls + 1
                Assert.AreEqual(1, serviceRequest.Statements.Length)
                Assert.AreEqual("source-1", serviceRequest.Statements.Head.Id)

                Task.FromResult(
                    response
                        (digest 'a')
                        (digest 'b')
                        200
                        (OwlReasoningOutcome.Completed(OwlReasoningResult.Classification [ inferred ]))
                ))

        let dataset =
            data
                scope
                [ sourceStatement
                  { inferred with
                      Id = "old-inference"
                      Origin =
                          RdfStatementOrigin.Inferred
                              { EngineId = "old"
                                EngineVersion = "1"
                                RuleOrAxiomId = "old-rule"
                                PremiseStatementIds = [ "source-1" ] } } ]

        let reasoningRequest =
            request OwlProfile.Owl2EL OwlReasoningOperation.Classify standardBudget (digest 'a') (digest 'b')

        let first = execute reasoner scope dataset reasoningRequest
        let second = execute reasoner scope dataset reasoningRequest

        Assert.AreEqual(1, calls)
        Assert.IsFalse(first.Provenance.CacheHit)
        Assert.IsTrue(second.Provenance.CacheHit)
        Assert.AreEqual(OwlProfile.Owl2EL, second.Provenance.Profile)
        Assert.AreEqual(OwlSemanticAssumptions.Standard, second.Provenance.Assumptions)
        Assert.AreEqual(OwlReasoningOperationKind.Classify, second.Provenance.Operation)
        Assert.AreEqual(digest 'a', second.Provenance.OntologyContentDigest)
        Assert.AreEqual(digest 'b', second.Provenance.ConfigurationDigest)

        let changedConfiguration =
            request OwlProfile.Owl2EL OwlReasoningOperation.Classify standardBudget (digest 'a') (digest 'c')

        let changedKey =
            OwlReasoningCacheKey.create reasoner.Capabilities changedConfiguration

        let originalKey = OwlReasoningCacheKey.create reasoner.Capabilities reasoningRequest
        Assert.AreNotEqual(originalKey, changedKey)

    [<TestMethod>]
    member _.ResponseIdentityShapeAndInferenceProvenanceAreValidated() =
        let assertedConclusion =
            { sourceStatement with
                Id = "not-inferred" }

        let cases =
            [ response
                  (digest 'c')
                  (digest 'b')
                  10
                  (OwlReasoningOutcome.Completed(OwlReasoningResult.Classification []))
              response
                  (digest 'a')
                  (digest 'b')
                  10
                  (OwlReasoningOutcome.Completed(OwlReasoningResult.Consistency OwlConsistencyResult.Consistent))
              response
                  (digest 'a')
                  (digest 'b')
                  10
                  (OwlReasoningOutcome.Completed(OwlReasoningResult.Classification [ assertedConclusion ])) ]

        for serviceResponse in cases do
            let reasoner =
                endpoint OwlReasoningCache.disabled (fun _ _ -> Task.FromResult serviceResponse)

            let reasoningRequest =
                request OwlProfile.Owl2EL OwlReasoningOperation.Classify standardBudget (digest 'a') (digest 'b')

            match execute reasoner scope (data scope [ sourceStatement ]) reasoningRequest with
            | { Outcome = OwlReasoningOutcome.Failed("identity_mismatch", _) }
            | { Outcome = OwlReasoningOutcome.Failed("invalid_response", _) } -> ()
            | execution -> Assert.Fail $"Expected invalid reasoner response, got {execution.Outcome}."

    [<TestMethod>]
    member _.ConsistencyEntailmentUnknownAndInconsistencyRemainDistinct() =
        let outcomes =
            [ OwlReasoningOperation.CheckConsistency, OwlReasoningResult.Consistency OwlConsistencyResult.Consistent
              OwlReasoningOperation.CheckConsistency,
              OwlReasoningResult.Consistency(OwlConsistencyResult.Inconsistent None)
              OwlReasoningOperation.CheckEntailment(
                  sourceStatement.Subject,
                  sourceStatement.Predicate,
                  sourceStatement.Object
              ),
              OwlReasoningResult.Entailment(OwlEntailmentResult.Disentailed None)
              OwlReasoningOperation.CheckEntailment(
                  sourceStatement.Subject,
                  sourceStatement.Predicate,
                  sourceStatement.Object
              ),
              OwlReasoningResult.Entailment OwlEntailmentResult.NotEntailed
              OwlReasoningOperation.CheckEntailment(
                  sourceStatement.Subject,
                  sourceStatement.Predicate,
                  sourceStatement.Object
              ),
              OwlReasoningResult.Entailment(OwlEntailmentResult.Unknown "incomplete") ]

        for operation, expected in outcomes do
            let reasoner =
                endpoint OwlReasoningCache.disabled (fun _ _ ->
                    Task.FromResult(response (digest 'a') (digest 'b') 10 (OwlReasoningOutcome.Completed expected)))

            let reasoningRequest =
                request OwlProfile.Owl2EL operation standardBudget (digest 'a') (digest 'b')

            match execute reasoner scope (data scope [ sourceStatement ]) reasoningRequest with
            | { Outcome = OwlReasoningOutcome.Completed actual } -> Assert.AreEqual(expected, actual)
            | execution -> Assert.Fail $"Expected completed result, got {execution.Outcome}."

    [<TestMethod>]
    member _.TimeoutAndResponseOutputProofLimitsAreStructured() =
        let timeoutReasoner =
            endpoint OwlReasoningCache.disabled (fun _ cancellationToken ->
                task {
                    do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    return response (digest 'a') (digest 'b') 0 OwlReasoningOutcome.TimedOut
                })

        let timeoutRequest =
            request
                OwlProfile.Owl2EL
                OwlReasoningOperation.CheckConsistency
                (budget 100 100 100 10_000 (TimeSpan.FromMilliseconds 1.0))
                (digest 'a')
                (digest 'b')

        match execute timeoutReasoner scope (data scope [ sourceStatement ]) timeoutRequest with
        | { Outcome = OwlReasoningOutcome.TimedOut } -> ()
        | execution -> Assert.Fail $"Expected timeout, got {execution.Outcome}."

        let inferred =
            conclusion
                "inferred-1"
                "https://example.test/Dog"
                "http://www.w3.org/2000/01/rdf-schema#subClassOf"
                "https://example.test/LivingThing"

        let limitedRequest =
            request
                OwlProfile.Owl2EL
                OwlReasoningOperation.Classify
                (budget 100 1 1 10 (TimeSpan.FromSeconds 2.0))
                (digest 'a')
                (digest 'b')

        let outputReasoner =
            endpoint OwlReasoningCache.disabled (fun _ _ ->
                Task.FromResult(
                    response
                        (digest 'a')
                        (digest 'b')
                        10
                        (OwlReasoningOutcome.Completed(
                            OwlReasoningResult.Classification [ inferred; { inferred with Id = "inferred-2" } ]
                        ))
                ))

        match execute outputReasoner scope (data scope [ sourceStatement ]) limitedRequest with
        | { Outcome = OwlReasoningOutcome.OutputLimitExceeded(1, 2) } -> ()
        | execution -> Assert.Fail $"Expected output limit, got {execution.Outcome}."

        let proof =
            { Steps =
                [ { Id = "step-1"
                    RuleOrAxiomId = "owl-el-1"
                    PremiseIds = [ "source-1" ]
                    Conclusion = inferred }
                  { Id = "step-2"
                    RuleOrAxiomId = "owl-el-2"
                    PremiseIds = [ "step-1" ]
                    Conclusion = { inferred with Id = "inferred-2" } } ]
              RootStepIds = [ "step-2" ] }

        let proofRequest =
            request
                OwlProfile.Owl2EL
                OwlReasoningOperation.CheckConsistency
                (budget 100 100 1 10_000 (TimeSpan.FromSeconds 2.0))
                (digest 'a')
                (digest 'b')

        let proofReasoner =
            endpoint OwlReasoningCache.disabled (fun _ _ ->
                Task.FromResult(
                    response
                        (digest 'a')
                        (digest 'b')
                        10
                        (OwlReasoningOutcome.Completed(
                            OwlReasoningResult.Consistency(OwlConsistencyResult.Inconsistent(Some proof))
                        ))
                ))

        match execute proofReasoner scope (data scope [ sourceStatement ]) proofRequest with
        | { Outcome = OwlReasoningOutcome.ProofLimitExceeded(1, 2) } -> ()
        | execution -> Assert.Fail $"Expected proof limit, got {execution.Outcome}."

        let responseReasoner =
            endpoint OwlReasoningCache.disabled (fun _ _ ->
                Task.FromResult(
                    response
                        (digest 'a')
                        (digest 'b')
                        11
                        (OwlReasoningOutcome.Completed(OwlReasoningResult.Classification []))
                ))

        match execute responseReasoner scope (data scope [ sourceStatement ]) limitedRequest with
        | { Outcome = OwlReasoningOutcome.ResponseSizeLimitExceeded(10, 11) } -> ()
        | execution -> Assert.Fail $"Expected response-size limit, got {execution.Outcome}."
