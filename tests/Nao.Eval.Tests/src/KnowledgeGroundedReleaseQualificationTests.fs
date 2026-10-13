namespace Nao.Eval.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval
open Nao.Knowledge

[<TestClass>]
type KnowledgeGroundedReleaseQualificationTests() =
    let dataset =
        KnowledgeAdversarialDatasets.adversarialV1
        |> KnowledgeAdversarialDataset.evaluationDataset

    let supportDescriptor version =
        KnowledgeClaimSupportEvaluatorDescriptor.create
            (ComponentVersion.create "nao-reference-support-evaluator" version)
            (Some(ComponentVersion.create "nao-reference-judge" "1"))
            (ContentHash.computeText "reference-support-configuration")
            true

    let adapter =
        KnowledgeGenerationAdapterDescriptor.create
            (ComponentVersion.create "nao-reference-grounded-generation-adapter" "1")
            (ContentHash.computeText "reference-generation-configuration")

    let qualifiedAt = DateTimeOffset(2026, 10, 13, 3, 30, 0, TimeSpan.Zero)

    let observation descriptor outcome claims =
        KnowledgeGenerationObservation.create
            outcome
            claims
            descriptor
            (KnowledgeClaimSupportUsage.create 0 0 0m)
            TimeSpan.Zero

    let perfectResult descriptor case =
        let caseId = KnowledgeEvaluationCase.id case
        let relevant = KnowledgeEvaluationCase.relevantChunkIds case |> Set.toList

        if case |> KnowledgeEvaluationCase.expectedFacts |> List.isEmpty then
            KnowledgeGenerationCaseResult.create
                caseId
                relevant
                (observation descriptor KnowledgeGenerationOutcome.Abstained [])
        else
            let referenceId = KnowledgeGroundedCitationReferenceId.parse $"reference-{caseId}"

            let citation =
                KnowledgeGenerationCitationObservation.create referenceId relevant.Head

            let claim =
                KnowledgeGenerationClaimObservation.create
                    KnowledgeClaimSupportVerdict.Supported
                    [ citation ]
                    [ referenceId ]

            KnowledgeGenerationCaseResult.create
                caseId
                relevant
                (observation descriptor KnowledgeGenerationOutcome.Accepted [ claim ])

    let qualityWith change =
        let descriptor = supportDescriptor "1"

        let results =
            dataset
            |> KnowledgeEvaluationDataset.cases
            |> List.map (perfectResult descriptor)
            |> change

        KnowledgeGroundedGenerationEvaluator.measure dataset results

    [<TestMethod>]
    member _.``Adversarial reference evidence meets grounded release thresholds``() =
        let quality = qualityWith id

        match
            KnowledgeGroundedReleaseQualification.qualify
                KnowledgeGroundedReleaseProfile.adversarialV1
                adapter
                qualifiedAt
                quality
        with
        | KnowledgeGroundedReleaseQualificationResult.Qualified evidence ->
            Assert.AreEqual(adapter, KnowledgeGroundedReleaseEvidence.generationAdapter evidence)
            Assert.AreEqual(supportDescriptor "1", KnowledgeGroundedReleaseEvidence.supportEvaluator evidence)

            Assert.IsTrue(
                evidence
                |> KnowledgeGroundedReleaseEvidence.gateResult
                |> KnowledgeQualityGateResult.passed
            )

            let metrics =
                evidence
                |> KnowledgeGroundedReleaseEvidence.gateResult
                |> KnowledgeQualityGateResult.candidate
                |> KnowledgeQualitySnapshot.metrics

            for metric in
                [ KnowledgeQualityMetric.ContextRelevance
                  KnowledgeQualityMetric.AnswerFaithfulness
                  KnowledgeQualityMetric.CitationCorrectness
                  KnowledgeQualityMetric.AbstentionAccuracy ] do
                Assert.AreEqual(1m, metrics[KnowledgeQualityMetricKey.overall metric])
        | result -> Assert.Fail $"Expected qualified grounded release evidence, received {result}."

    [<TestMethod>]
    member _.``Faithfulness and citation regressions fail the absolute release gate``() =
        let degradeFirstAnswer results =
            results
            |> List.mapi (fun index result ->
                if index <> 0 then
                    result
                else
                    let caseId = KnowledgeGenerationCaseResult.caseId result
                    let context = KnowledgeGenerationCaseResult.contextChunkIds result
                    let referenceId = KnowledgeGroundedCitationReferenceId.parse "unsupported-reference"

                    let unsupported =
                        KnowledgeGenerationClaimObservation.create
                            KnowledgeClaimSupportVerdict.Unsupported
                            [ KnowledgeGenerationCitationObservation.create referenceId context.Head ]
                            []

                    KnowledgeGenerationCaseResult.create
                        caseId
                        context
                        (observation (supportDescriptor "1") KnowledgeGenerationOutcome.Accepted [ unsupported ]))

        let result =
            qualityWith degradeFirstAnswer
            |> KnowledgeGroundedReleaseQualification.qualify
                KnowledgeGroundedReleaseProfile.adversarialV1
                adapter
                qualifiedAt

        Assert.IsFalse(KnowledgeGroundedReleaseQualificationResult.passed result)

        Assert.IsTrue(
            result
            |> KnowledgeGroundedReleaseQualificationResult.failures
            |> List.contains KnowledgeGroundedReleaseFailure.QualityGateFailed
        )

        let failedMetrics =
            result
            |> KnowledgeGroundedReleaseQualificationResult.gateResult
            |> KnowledgeQualityGateResult.checks
            |> List.filter (KnowledgeMetricGateCheck.passed >> not)
            |> List.map KnowledgeMetricGateCheck.metric
            |> Set.ofList

        Assert.IsTrue(failedMetrics.Contains KnowledgeQualityMetric.AnswerFaithfulness)
        Assert.IsTrue(failedMetrics.Contains KnowledgeQualityMetric.CitationCorrectness)

    [<TestMethod>]
    member _.``Unresolved responses fail release qualification independently of aggregate metrics``() =
        let rejectFirstAnswer results =
            results
            |> List.mapi (fun index result ->
                if index <> 0 then
                    result
                else
                    KnowledgeGenerationCaseResult.create
                        (KnowledgeGenerationCaseResult.caseId result)
                        (KnowledgeGenerationCaseResult.contextChunkIds result)
                        (observation (supportDescriptor "1") KnowledgeGenerationOutcome.Rejected []))

        let result =
            qualityWith rejectFirstAnswer
            |> KnowledgeGroundedReleaseQualification.qualify
                KnowledgeGroundedReleaseProfile.adversarialV1
                adapter
                qualifiedAt

        Assert.IsTrue(
            result
            |> KnowledgeGroundedReleaseQualificationResult.failures
            |> List.contains KnowledgeGroundedReleaseFailure.UnresolvedResponses
        )

    [<TestMethod>]
    member _.``Dataset and support-evaluator identity are part of release evidence``() =
        let cases = KnowledgeEvaluationDataset.cases dataset

        let changedDataset =
            KnowledgeEvaluationDataset.create "different-grounded-dataset" 1 cases

        let descriptor = supportDescriptor "1"

        let changedQuality =
            changedDataset
            |> KnowledgeEvaluationDataset.cases
            |> List.map (perfectResult descriptor)
            |> KnowledgeGroundedGenerationEvaluator.measure changedDataset

        let datasetResult =
            KnowledgeGroundedReleaseQualification.qualify
                KnowledgeGroundedReleaseProfile.adversarialV1
                adapter
                qualifiedAt
                changedQuality

        Assert.IsTrue(
            datasetResult
            |> KnowledgeGroundedReleaseQualificationResult.failures
            |> List.contains KnowledgeGroundedReleaseFailure.DatasetMismatch
        )

        let inconsistentResults =
            cases
            |> List.mapi (fun index case -> perfectResult (supportDescriptor (if index = 0 then "2" else "1")) case)

        let inconsistentQuality =
            KnowledgeGroundedGenerationEvaluator.measure dataset inconsistentResults

        let supportResult =
            KnowledgeGroundedReleaseQualification.qualify
                KnowledgeGroundedReleaseProfile.adversarialV1
                adapter
                qualifiedAt
                inconsistentQuality

        Assert.IsTrue(
            supportResult
            |> KnowledgeGroundedReleaseQualificationResult.failures
            |> List.contains KnowledgeGroundedReleaseFailure.InconsistentSupportEvaluator
        )
