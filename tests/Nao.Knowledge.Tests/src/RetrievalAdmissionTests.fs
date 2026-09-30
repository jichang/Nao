namespace Nao.Knowledge.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type RetrievalAdmissionTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

    let request intent transformation budget =
        KnowledgeRetrievalRequest.create
            "query"
            authorization
            (KnowledgeIndexNamespace.parse "primary")
            (KnowledgeIndexFilter.create [ KnowledgeSourceId.parse "source" ] [] Map.empty None None None None)
            intent
            KnowledgeRetrievalStrategy.Hybrid
            transformation
            KnowledgeRetrievalLimits.defaultLimits
            budget
        |> KnowledgeQueryNormalizer.normalize

    let run model normalized =
        KnowledgeQueryUnderstanding.planAsync model normalized CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Explicit non-knowledge intent skips before model transformation``() =
        let normalized =
            request
                KnowledgeTaskIntent.NonKnowledge
                KnowledgeQueryTransformationStrategy.Rewrite
                KnowledgeRetrievalBudget.deterministic

        let mutable calls = 0

        let model =
            { Descriptor =
                { Model = ComponentVersion.create "must-not-run" "1"
                  ConfigurationHash = ContentHash.computeText "must-not-run"
                  Deterministic = true }
              TransformAsync =
                fun _ _ _ ->
                    calls <- calls + 1

                    Task.FromResult
                        { Variants = [ "unexpected" ]
                          Usage =
                            { ModelCalls = 1
                              InputTokens = 1
                              OutputTokens = 1
                              EstimatedCostUsd = 0m } } }

        match run (Some model) normalized with
        | KnowledgeQueryUnderstandingResult.RetrievalSkipped admission ->
            Assert.AreEqual(0, calls)
            Assert.AreSame(normalized, KnowledgeRetrievalAdmission.normalizedQuery admission)
            Assert.AreSame(KnowledgeNormalizedQuery.request normalized, KnowledgeRetrievalAdmission.request admission)
            Assert.AreEqual(KnowledgeRetrievalAdmissionPolicy.descriptor, KnowledgeRetrievalAdmission.policy admission)
            Assert.AreEqual(KnowledgeRetrievalAdmissionDecision.Skip, KnowledgeRetrievalAdmission.decision admission)

            Assert.AreEqual(
                KnowledgeRetrievalAdmissionReason.ExplicitNonKnowledgeIntent,
                KnowledgeRetrievalAdmission.reason admission
            )
        | KnowledgeQueryUnderstandingResult.RetrievalPlanned _ ->
            Assert.Fail "Explicit non-knowledge intent must skip retrieval."

    [<TestMethod>]
    member _.``Knowledge and unspecified intent retrieve with the same immutable request``() =
        for intent, expectedReason in
            [ KnowledgeTaskIntent.KnowledgeSeeking, KnowledgeRetrievalAdmissionReason.ExplicitKnowledgeIntent
              KnowledgeTaskIntent.Unspecified, KnowledgeRetrievalAdmissionReason.UnspecifiedIntent ] do
            let normalized =
                request intent KnowledgeQueryTransformationStrategy.None KnowledgeRetrievalBudget.deterministic

            match run None normalized with
            | KnowledgeQueryUnderstandingResult.RetrievalPlanned(admission, plan) ->
                Assert.AreEqual(
                    KnowledgeRetrievalAdmissionDecision.Retrieve,
                    KnowledgeRetrievalAdmission.decision admission
                )

                Assert.AreEqual(expectedReason, KnowledgeRetrievalAdmission.reason admission)

                Assert.AreSame(
                    KnowledgeNormalizedQuery.request normalized,
                    KnowledgeRetrievalAdmission.request admission
                )

                Assert.AreSame(
                    KnowledgeNormalizedQuery.request normalized,
                    KnowledgeQueryTransformationPlan.request plan
                )
            | KnowledgeQueryUnderstandingResult.RetrievalSkipped _ ->
                Assert.Fail "Knowledge and unspecified intent must remain retrieval-eligible."

    [<TestMethod>]
    member _.``Cancelled query understanding produces no admission decision``() =
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        let normalized =
            request
                KnowledgeTaskIntent.NonKnowledge
                KnowledgeQueryTransformationStrategy.None
                KnowledgeRetrievalBudget.deterministic

        Assert.ThrowsExactly<OperationCanceledException>(fun () ->
            KnowledgeQueryUnderstanding.planAsync None normalized cancellation.Token
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore
