namespace Nao.Semantics.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Reasoning
open Nao.Semantics

[<TestClass>]
type CommonReasoningTests() =
    let digest character = String(character, 64)

    let authorization tenant =
        let principal =
            SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse "user") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    let scope = authorization "tenant"

    let citation =
        ReasoningCitation.create "fixture:assertion-1" "fixture-v1" (Some "line-1")

    let assertion =
        ReasoningAssertion.create
            "assertion-1"
            (ReasoningArtifact.create "application/n-triples" "<urn:s> <urn:p> <urn:o> .")
            [ citation ]

    let limits conclusions proof externalCalls =
        ReasoningLimits.create
            (TimeSpan.FromSeconds 2.0)
            100_000_000L
            10_000L
            100
            conclusions
            proof
            100_000
            externalCalls

    let provenance operation =
        { ExecutionId = Guid.Parse "11111111-1111-1111-1111-111111111111"
          EngineId = "elk"
          EngineVersion = "0.6.0"
          Profile = OwlProfile.Owl2EL
          Assumptions = OwlSemanticAssumptions.Standard
          Operation = operation
          ConfigurationId = "elk-config"
          ConfigurationVersion = "1"
          ConfigurationDigest = digest 'b'
          OntologyDatasetId = "dataset"
          OntologyDatasetRevision = 7L
          OntologyVersionIri = RdfIri.parse "https://example.test/ontology/7"
          OntologyContentDigest = digest 'a'
          Imports = Map.empty
          StartedAt = DateTimeOffset(2026, 10, 27, 5, 0, 0, TimeSpan.Zero)
          Duration = TimeSpan.FromMilliseconds 12.0
          CacheHit = false }

    let pinnedInputs =
        [ ReasoningInputIdentity.create "owl-ontology" "dataset" "7" (digest 'a')
          ReasoningInputIdentity.create "reasoner-configuration" "elk-config" "1" (digest 'b') ]

    let request operation requirements reasoningLimits =
        ReasoningRequest.create
            scope
            (Guid.Parse "22222222-2222-2222-2222-222222222222")
            [ assertion ]
            (OwlReasoningBridge.semanticMode OwlProfile.Owl2EL)
            (ReasoningArtifact.create "application/vnd.nao.owl-operation+text" (string operation))
            requirements
            reasoningLimits
            pinnedInputs

    let requirements requireProof requireUnsatCore capabilities =
        ReasoningEngineRequirements.create
            (Some "elk")
            capabilities
            (Set.singleton ReasoningExecutionMode.RemoteService)
            requireProof
            requireUnsatCore

    let execution operation outcome =
        { Provenance = provenance operation
          Outcome = outcome }

    let inferred id =
        { Id = id
          Subject = RdfResource.Iri(RdfIri.parse "https://example.test/Dog")
          Predicate = RdfIri.parse "http://www.w3.org/2000/01/rdf-schema#subClassOf"
          Object = RdfTerm.Resource(RdfResource.Iri(RdfIri.parse "https://example.test/Animal"))
          Graph = None
          Origin =
            RdfStatementOrigin.Inferred
                { EngineId = "elk"
                  EngineVersion = "0.6.0"
                  RuleOrAxiomId = "owl-el"
                  PremiseStatementIds = [ "assertion-1" ] } }

    let adapt operation outcome =
        OwlReasoningBridge.adapt
            scope
            (request operation (requirements false false Set.empty) (limits 10 10 1))
            (execution operation outcome)

    [<TestMethod>]
    member _.ContractsPinArtifactsInputsSemanticsAndAuthorization() =
        let artifact = ReasoningArtifact.create "text/plain" "canonical input"
        Assert.AreEqual(ReasoningArtifact.digest artifact, ReasoningArtifact.digest artifact)
        Assert.AreEqual(64, ReasoningArtifact.digest artifact |> String.length)

        let semanticMode = OwlReasoningBridge.semanticMode OwlProfile.Owl2EL
        Assert.AreEqual(ReasoningWorldAssumption.OpenWorld, ReasoningSemanticMode.world semanticMode)
        Assert.AreEqual(ReasoningNegationSemantics.Classical, ReasoningSemanticMode.negation semanticMode)
        Assert.AreEqual(ReasoningMonotonicity.Monotonic, ReasoningSemanticMode.monotonicity semanticMode)

        let reasoningRequest =
            request OwlReasoningOperationKind.CheckConsistency (requirements false false Set.empty) (limits 10 10 1)

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            ReasoningRequest.requireOwner (authorization "foreign") reasoningRequest
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            ReasoningInputIdentity.create "ontology" "id" "1" "not-a-digest" |> ignore)
        |> ignore

    [<TestMethod>]
    member _.OwlOutcomesRemainProvenDisprovenUnknownInconsistentTimeoutAndUnsupported() =
        let entailment = OwlReasoningOperationKind.CheckEntailment

        let cases =
            [ OwlReasoningOutcome.Completed(OwlReasoningResult.Entailment(OwlEntailmentResult.Entailed None)), "proven"
              OwlReasoningOutcome.Completed(OwlReasoningResult.Entailment(OwlEntailmentResult.Disentailed None)),
              "disproven"
              OwlReasoningOutcome.Completed(OwlReasoningResult.Entailment OwlEntailmentResult.NotEntailed), "unknown"
              OwlReasoningOutcome.Completed(OwlReasoningResult.Entailment(OwlEntailmentResult.Unknown "incomplete")),
              "unknown"
              OwlReasoningOutcome.TimedOut, "timeout"
              OwlReasoningOutcome.Unsupported(entailment, "unsupported"), "unsupported" ]

        for owlOutcome, expected in cases do
            let result = adapt entailment owlOutcome

            let actual =
                match ReasoningResult.outcome result with
                | ReasoningOutcome.Proven _ -> "proven"
                | ReasoningOutcome.Disproven _ -> "disproven"
                | ReasoningOutcome.Unknown(ReasoningUnknownReason.NotProven, _) -> "unknown"
                | ReasoningOutcome.Unknown(ReasoningUnknownReason.EngineReturnedUnknown "incomplete", _) -> "unknown"
                | ReasoningOutcome.TimedOut -> "timeout"
                | ReasoningOutcome.Unsupported _ -> "unsupported"
                | outcome -> $"unexpected:{outcome}"

            Assert.AreEqual(expected, actual)

        let inconsistent =
            adapt
                OwlReasoningOperationKind.CheckConsistency
                (OwlReasoningOutcome.Completed(OwlReasoningResult.Consistency(OwlConsistencyResult.Inconsistent None)))

        match ReasoningResult.outcome inconsistent with
        | ReasoningOutcome.Inconsistent _ -> ()
        | outcome -> Assert.Fail $"Expected inconsistent outcome, got {outcome}."

    [<TestMethod>]
    member _.BridgePreservesProofCitationsIdentityUsageAndSourceAssertions() =
        let statement = inferred "inferred-1"

        let proof =
            { Steps =
                [ { Id = "step-1"
                    RuleOrAxiomId = "owl-el"
                    PremiseIds = [ "assertion-1" ]
                    Conclusion = statement } ]
              RootStepIds = [ "step-1" ] }

        let result =
            adapt
                OwlReasoningOperationKind.CheckEntailment
                (OwlReasoningOutcome.Completed(OwlReasoningResult.Entailment(OwlEntailmentResult.Entailed(Some proof))))

        match ReasoningResult.outcome result with
        | ReasoningOutcome.Proven(_, evidence) ->
            Assert.AreEqual(1, evidence.Proof.Value.Steps.Length)
            Assert.AreEqual("owl-el", evidence.Proof.Value.Steps.Head.RuleOrAxiomId)
        | outcome -> Assert.Fail $"Expected proven result, got {outcome}."

        Assert.AreEqual([ assertion ], ReasoningResult.sourceAssertions result)
        Assert.AreEqual(pinnedInputs, ReasoningResult.pinnedInputs result)
        Assert.AreEqual(1, (ReasoningResult.usage result).ProofSteps)
        Assert.AreEqual("elk", result |> ReasoningResult.engine |> ReasoningEngineIdentity.id)

        Assert.AreEqual(
            (provenance OwlReasoningOperationKind.CheckEntailment).ExecutionId,
            (ReasoningResult.audit result).TraceId
        )

    [<TestMethod>]
    member _.RequirementsAndCommonLimitsFailExplicitly() =
        let classification =
            OwlReasoningOutcome.Completed(OwlReasoningResult.Classification [ inferred "one"; inferred "two" ])

        let limited =
            OwlReasoningBridge.adapt
                scope
                (request OwlReasoningOperationKind.Classify (requirements false false Set.empty) (limits 1 10 1))
                (execution OwlReasoningOperationKind.Classify classification)

        match ReasoningResult.outcome limited with
        | ReasoningOutcome.ResourceLimitExceeded(ReasoningResourceLimit.Conclusions, 1L, 2L) -> ()
        | outcome -> Assert.Fail $"Expected common conclusion limit, got {outcome}."

        let proofRequired =
            OwlReasoningBridge.adapt
                scope
                (request OwlReasoningOperationKind.CheckEntailment (requirements true false Set.empty) (limits 10 10 1))
                (execution
                    OwlReasoningOperationKind.CheckEntailment
                    (OwlReasoningOutcome.Completed(OwlReasoningResult.Entailment(OwlEntailmentResult.Entailed None))))

        match ReasoningResult.outcome proofRequired with
        | ReasoningOutcome.Unsupported("proof", _) -> ()
        | outcome -> Assert.Fail $"Expected missing-proof outcome, got {outcome}."

        let noExternalCalls =
            OwlReasoningBridge.adapt
                scope
                (request
                    OwlReasoningOperationKind.CheckConsistency
                    (requirements false false Set.empty)
                    (limits 10 10 0))
                (execution
                    OwlReasoningOperationKind.CheckConsistency
                    (OwlReasoningOutcome.Completed(OwlReasoningResult.Consistency OwlConsistencyResult.Consistent)))

        match ReasoningResult.outcome noExternalCalls with
        | ReasoningOutcome.ResourceLimitExceeded(ReasoningResourceLimit.ExternalCalls, 0L, 1L) -> ()
        | outcome -> Assert.Fail $"Expected external-call limit, got {outcome}."

    [<TestMethod>]
    member _.AuditedEndpointRecordsPinnedTraceWithoutPayloadContent() =
        let reasoningRequest =
            request OwlReasoningOperationKind.CheckConsistency (requirements false false Set.empty) (limits 10 10 1)

        let result =
            adapt
                OwlReasoningOperationKind.CheckConsistency
                (OwlReasoningOutcome.Completed(OwlReasoningResult.Consistency OwlConsistencyResult.Consistent))

        let mutable recorded = None

        let sink =
            { RecordAsync =
                fun receivedScope record _ ->
                    Assert.AreEqual(AuthorizationScope.identity scope, AuthorizationScope.identity receivedScope)
                    recorded <- Some record
                    Task.CompletedTask }

        let endpoint =
            { Engine = ReasoningResult.engine result
              ExecuteAsync = fun _ _ _ -> Task.FromResult result }
            |> ReasoningEndpoint.withAudit sink

        let returned =
            endpoint.ExecuteAsync scope reasoningRequest CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(result, returned)
        let record = recorded.Value
        Assert.AreEqual(ReasoningResult.requestId result, record.RequestId)
        Assert.AreEqual("proven", record.Outcome)
        Assert.AreEqual(pinnedInputs |> List.map ReasoningInputIdentity.digest, record.InputDigests)
