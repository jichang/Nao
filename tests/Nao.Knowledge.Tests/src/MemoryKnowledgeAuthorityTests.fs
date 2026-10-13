namespace Nao.Knowledge.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass>]
type MemoryKnowledgeAuthorityTests() =
    let scope user session =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse user) []

        AuthorizationScope.tryCreate
            principal
            None
            (WorkspaceId.parse "workspace")
            (session |> Option.map SessionId.parse)
        |> Option.get

    let sessionAuthorization = scope "user" (Some "session")
    let durableAuthorization = scope "user" None
    let reviewerAuthorization = scope "reviewer" None
    let now = DateTimeOffset(2026, 10, 13, 3, 0, 0, TimeSpan.Zero)

    let activeMemory () =
        let content = "The support escalation policy requires manager approval."

        let event =
            SessionMemoryEvent.capture
                sessionAuthorization
                (CorrelationContext.root ())
                SessionMemoryEventKind.UserFact
                content
                SessionMemoryContentClass.Ordinary
                SessionMemoryPersistenceIntent.Durable
                now

        let candidate =
            match
                SessionMemoryEligibility.evaluate
                    SessionMemoryEligibilityPolicy.defaultPolicy
                    sessionAuthorization
                    now
                    event
            with
            | SessionMemoryEligibilityResult.Eligible value -> value
            | result -> failwith $"Expected eligible candidate, received {result}."

        let sourceOwner = SessionMemoryEvent.owner event
        let owner = { sourceOwner with SessionId = None }

        let consent =
            CrossSessionMemoryConsent.grant
                (CrossSessionMemorySubject.ofIdentity sourceOwner)
                CrossSessionMemoryConsentScope.User
                now
                None

        let policyDecision =
            CrossSessionMemoryPolicyDecision.create
                "nao.cross-session-memory"
                "1"
                (SessionMemoryEvent.id event)
                owner
                true
                now
                (now.AddMinutes 5.0)

        let approved =
            match
                CrossSessionMemoryApproval.evaluate sessionAuthorization now (Some consent) policyDecision candidate
            with
            | CrossSessionMemoryApprovalResult.Approved value -> value
            | result -> failwith $"Expected approved candidate, received {result}."

        let proposal =
            MemoryExtractionProposal.create
                "policy.support-escalation"
                "manager approval required"
                "support escalation policy requires manager approval"
                (MemoryExtractionConfidence.create 0.98)

        let extractor =
            { Descriptor =
                MemoryExtractorDescriptor.create "test.memory-extractor" "1" None None "configuration-hash" true
              ExtractAsync =
                fun _ _ _ ->
                    Task.FromResult(MemoryExtractionOutput.create [ proposal ] (MemoryExtractionUsage.create 0 0 0m)) }

        let batch =
            BackgroundMemoryExtraction.extractAsync
                extractor
                durableAuthorization
                BackgroundMemoryExtractionPolicy.defaultPolicy
                now
                approved
                CancellationToken.None
            |> _.Result

        DurableMemoryLifecycle.introduce
            durableAuthorization
            (DurableMemoryLifecyclePolicy.create (TimeSpan.FromDays 30.0) 8)
            now
            batch
            (BackgroundMemoryExtractionBatch.accepted batch |> List.exactlyOne)
        |> DurableMemoryLifecycleResult.record

    let verification record trust verifiedAt validUntil =
        let memoryValue =
            match DurableMemoryRecord.state record with
            | DurableMemoryState.Active value -> value
            | state -> failwith $"Expected active memory, received {state}."

        MemoryKnowledgeVerification.create
            (DurableMemoryRecord.id record)
            (DurableMemoryRecord.version record)
            (DurableMemoryRecord.owner record)
            (DurableMemoryValue.valueHash memoryValue |> ContentHash.parse)
            trust
            [ MemoryKnowledgeReference.create
                  (Uri "https://knowledge.example/policies/support")
                  (ContentHash.computeText "published support policy")
                  "Published support policy" ]
            (MemoryKnowledgeVerifierDescriptor.create
                (ComponentVersion.create "test.memory-verifier" "1")
                (ContentHash.computeText "verification configuration"))
            "Matched the current published support policy."
            verifiedAt
            validUntil

    let evidence access decision =
        { Id = Guid.NewGuid()
          Access = access
          Decision = decision
          Basis = PermissionDecisionBasis.HostApproval PermissionGrantScope.Operation
          PolicyVersions = Map.ofList [ PermissionPolicy.identity, PermissionPolicy.currentVersion ]
          Grants = []
          EvaluatedAt = now }

    [<TestMethod>]
    member _.``Verified active memory becomes an explicitly non-source-backed promotion candidate``() =
        let record = activeMemory ()

        let verification =
            verification record KnowledgeTrust.OrganizationVerified now (now.AddDays 1.0)

        let permission =
            evidence (MemoryKnowledgeAuthority.promotionAccess record) PermissionDecision.Allow

        match MemoryKnowledgeAuthority.promote reviewerAuthorization now permission verification record with
        | MemoryKnowledgePromotionResult.Promoted candidate ->
            Assert.AreEqual(record, VerifiedMemoryKnowledgeCandidate.memory candidate)
            Assert.AreEqual("policy.support-escalation", VerifiedMemoryKnowledgeCandidate.key candidate)
            Assert.AreEqual("manager approval required", VerifiedMemoryKnowledgeCandidate.value candidate)

            Assert.AreEqual(
                KnowledgeTrust.OrganizationVerified,
                (VerifiedMemoryKnowledgeCandidate.trust candidate).Level
            )

            Assert.AreEqual(
                AuthorizationScope.identity reviewerAuthorization,
                VerifiedMemoryKnowledgeCandidate.promotedBy candidate
            )
        | result -> Assert.Fail $"Expected promoted candidate, received {result}."

    [<TestMethod>]
    member _.``Unverified and source-asserted trust cannot create verification evidence``() =
        let record = activeMemory ()

        for trust in [ KnowledgeTrust.Unverified; KnowledgeTrust.SourceAsserted ] do
            Assert.ThrowsExactly<ArgumentException>(fun () -> verification record trust now (now.AddDays 1.0) |> ignore)
            |> ignore

    [<TestMethod>]
    member _.``Verification is bound to exact memory version owner value and validity``() =
        let record = activeMemory ()

        let permission =
            evidence (MemoryKnowledgeAuthority.promotionAccess record) PermissionDecision.Allow

        let current = verification record KnowledgeTrust.Authoritative now (now.AddDays 1.0)

        let wrongOwner =
            let other = scope "other-user" None |> AuthorizationScope.identity

            MemoryKnowledgeVerification.create
                (MemoryKnowledgeVerification.memoryId current)
                (MemoryKnowledgeVerification.memoryVersion current)
                other
                (MemoryKnowledgeVerification.valueHash current)
                (MemoryKnowledgeVerification.trust current)
                (MemoryKnowledgeVerification.references current)
                (MemoryKnowledgeVerification.verifier current)
                (MemoryKnowledgeVerification.rationale current)
                now
                (now.AddDays 1.0)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            MemoryKnowledgeAuthority.promote reviewerAuthorization now permission wrongOwner record
            |> ignore)
        |> ignore

        let wrongVersion =
            MemoryKnowledgeVerification.create
                (MemoryKnowledgeVerification.memoryId current)
                (MemoryKnowledgeVerification.memoryVersion current + 1)
                (MemoryKnowledgeVerification.owner current)
                (MemoryKnowledgeVerification.valueHash current)
                (MemoryKnowledgeVerification.trust current)
                (MemoryKnowledgeVerification.references current)
                (MemoryKnowledgeVerification.verifier current)
                (MemoryKnowledgeVerification.rationale current)
                now
                (now.AddDays 1.0)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            MemoryKnowledgeAuthority.promote reviewerAuthorization now permission wrongVersion record
            |> ignore)
        |> ignore

        let wrongValue =
            MemoryKnowledgeVerification.create
                (MemoryKnowledgeVerification.memoryId current)
                (MemoryKnowledgeVerification.memoryVersion current)
                (MemoryKnowledgeVerification.owner current)
                (ContentHash.computeText "different value")
                (MemoryKnowledgeVerification.trust current)
                (MemoryKnowledgeVerification.references current)
                (MemoryKnowledgeVerification.verifier current)
                (MemoryKnowledgeVerification.rationale current)
                now
                (now.AddDays 1.0)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            MemoryKnowledgeAuthority.promote reviewerAuthorization now permission wrongValue record
            |> ignore)
        |> ignore

        let expired =
            verification record KnowledgeTrust.Authoritative (now.AddDays -2.0) now

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            MemoryKnowledgeAuthority.promote reviewerAuthorization now permission expired record
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Denied and wrong-resource permission evidence cannot promote memory``() =
        let record = activeMemory ()

        let verification =
            verification record KnowledgeTrust.Authoritative now (now.AddDays 1.0)

        let denied =
            evidence (MemoryKnowledgeAuthority.promotionAccess record) PermissionDecision.Deny

        match MemoryKnowledgeAuthority.promote reviewerAuthorization now denied verification record with
        | MemoryKnowledgePromotionResult.Denied actual -> Assert.AreEqual(denied, actual)
        | result -> Assert.Fail $"Expected denied promotion, received {result}."

        let wrongResource =
            evidence
                (ResourceAccess.administration "memory-knowledge-promotion/other" AdministrationAction.Manage)
                PermissionDecision.Allow

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            MemoryKnowledgeAuthority.promote reviewerAuthorization now wrongResource verification record
            |> ignore)
        |> ignore
