namespace Nao.Eval.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval
open Nao.Knowledge

[<TestClass>]
type KnowledgeDatasetCandidateTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "reviewer") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

    let now = DateTimeOffset(2026, 10, 11, 23, 40, 0, TimeSpan.Zero)
    let relevantChunk = KnowledgeChunkId.parse ("chunk_" + String.replicate 64 "1")

    let dimensions =
        KnowledgeEvaluationDimensions.create
            [ KnowledgeSourceId.parse "production-source" ]
            [ "text/plain" ]
            "en"
            "standard"
            KnowledgeQueryType.FactLookup

    let redactor: KnowledgeProductionQueryRedactor =
        { Descriptor =
            KnowledgeQueryRedactorDescriptor.create
                (ComponentVersion.create "test-redactor" "1")
                (ContentHash.computeText "redactor-config")
          Redact =
            fun query ->
                KnowledgeQueryRedaction.create
                    (query.Replace("person@example.test", "[redacted]", StringComparison.Ordinal))
                    1 }

    let candidate retainUntil =
        let failure =
            KnowledgeProductionRetrievalFailure.create
                "Find records for person@example.test"
                dimensions
                [ relevantChunk ]
                KnowledgeProductionRetrievalFailureKind.MissingRelevantResult
                (CorrelationId.generate ())

        KnowledgeDatasetCandidate.capture
            authorization
            now
            (KnowledgeDatasetCandidateCapturePolicy.create (ContentHash.computeText "consent-record") retainUntil)
            redactor
            failure

    let evidence access decision =
        { Id = Guid.NewGuid()
          Access = access
          Decision = decision
          Basis =
            if decision = PermissionDecision.Allow then
                PermissionDecisionBasis.HostApproval PermissionGrantScope.Operation
            else
                PermissionDecisionBasis.HostDenial
          PolicyVersions = Map [ PermissionPolicy.identity, PermissionPolicy.currentVersion ]
          Grants = []
          EvaluatedAt = now }

    let authorizer decision =
        KnowledgeDatasetCandidateReviewAuthorizer.create (fun _ access _ -> evidence access decision |> Task.FromResult)

    [<TestMethod>]
    member _.``Capture retains redacted query and governed privacy evidence``() =
        let value = candidate (now.AddDays 30.0)

        Assert.AreEqual(KnowledgeDatasetCandidate.currentVersion, KnowledgeDatasetCandidate.version value)
        Assert.AreEqual("Find records for [redacted]", KnowledgeDatasetCandidate.query value)

        Assert.IsFalse(
            KnowledgeDatasetCandidate.query value
            |> _.Contains("person@example.test", StringComparison.Ordinal)
        )

        Assert.AreEqual(1, KnowledgeDatasetCandidate.removedValueCount value)
        Assert.AreEqual(AuthorizationScope.identity authorization, KnowledgeDatasetCandidate.owner value)
        Assert.AreEqual(ContentHash.computeText "consent-record", KnowledgeDatasetCandidate.consentRecordHash value)

    [<TestMethod>]
    member _.``Denied review publishes no judgment``() =
        let result =
            KnowledgeDatasetCandidateReview.reviewAsync
                (authorizer PermissionDecision.Deny)
                authorization
                now
                (candidate (now.AddDays 30.0))
                (KnowledgeDatasetCandidateReviewRequest.Reject "Not reproducible.")
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        match result with
        | KnowledgeDatasetCandidateReviewResult.Denied permission ->
            Assert.AreEqual(PermissionDecision.Deny, permission.Decision)
        | KnowledgeDatasetCandidateReviewResult.Authorized _ -> Assert.Fail "Denied review returned a judgment."

    [<TestMethod>]
    member _.``Approved candidate promotes into a new authenticated dataset revision``() =
        let candidate = candidate (now.AddDays 30.0)

        let judgment =
            KnowledgeDatasetCandidateJudgment.create
                "production-missing-result"
                []
                [ relevantChunk ]
                [ KnowledgeExpectedFact.create "expected" "The reviewed expected fact." [ relevantChunk ] ]
                []
                "Reviewer reproduced the missing-result failure."

        let review =
            KnowledgeDatasetCandidateReview.reviewAsync
                (authorizer PermissionDecision.Allow)
                authorization
                now
                candidate
                (KnowledgeDatasetCandidateReviewRequest.Approve judgment)
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let approved =
            match review with
            | KnowledgeDatasetCandidateReviewResult.Authorized(KnowledgeDatasetCandidateReview.Approved value) -> value
            | value -> invalidOp $"Expected approved candidate, received {value}."

        let promotion =
            KnowledgeDatasetCandidatePromotionWorkflow.promoteAsync
                (authorizer PermissionDecision.Allow)
                authorization
                (now.AddDays 1.0)
                "production-regressions"
                1
                None
                [ KnowledgeDatasetCandidateReview.Approved approved ]
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        match promotion with
        | KnowledgeDatasetCandidatePromotionResult.Promoted value ->
            Assert.AreEqual(
                AuthorizationScope.identity authorization,
                KnowledgeDatasetCandidatePromotion.promotedBy value
            )

            Assert.AreEqual(
                [ KnowledgeDatasetCandidate.id candidate ],
                KnowledgeDatasetCandidatePromotion.candidateIds value
            )

            let dataset = KnowledgeDatasetCandidatePromotion.dataset value
            Assert.AreEqual(1, KnowledgeEvaluationDataset.revision dataset)

            Assert.AreEqual(
                "Find records for [redacted]",
                dataset
                |> KnowledgeEvaluationDataset.cases
                |> List.exactlyOne
                |> KnowledgeEvaluationCase.query
            )
        | KnowledgeDatasetCandidatePromotionResult.Denied _ -> Assert.Fail "Authorized promotion was denied."

    [<TestMethod>]
    member _.``Expired and rejected candidates cannot be promoted``() =
        let expired = candidate (now.AddMinutes 1.0)

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeDatasetCandidateReview.reviewAsync
                (authorizer PermissionDecision.Allow)
                authorization
                (now.AddMinutes 2.0)
                expired
                (KnowledgeDatasetCandidateReviewRequest.Reject "Expired.")
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let active = candidate (now.AddDays 30.0)

        let rejected =
            KnowledgeDatasetCandidateReview.reviewAsync
                (authorizer PermissionDecision.Allow)
                authorization
                now
                active
                (KnowledgeDatasetCandidateReviewRequest.Reject "Not reproducible.")
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let review =
            match rejected with
            | KnowledgeDatasetCandidateReviewResult.Authorized value -> value
            | _ -> invalidOp "Review was unexpectedly denied."

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeDatasetCandidatePromotionWorkflow.promoteAsync
                (authorizer PermissionDecision.Allow)
                authorization
                now
                "production-regressions"
                1
                None
                [ review ]
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore
