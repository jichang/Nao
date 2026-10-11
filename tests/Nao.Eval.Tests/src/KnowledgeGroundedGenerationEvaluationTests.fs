namespace Nao.Eval.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval
open Nao.Knowledge

[<TestClass>]
type KnowledgeGroundedGenerationEvaluationTests() =
    let chunk suffix =
        KnowledgeChunkId.parse ("chunk_" + String.replicate 63 "0" + suffix)

    let descriptor =
        KnowledgeClaimSupportEvaluatorDescriptor.create
            (ComponentVersion.create "support-evaluator" "1")
            (Some(ComponentVersion.create "judge-model" "1"))
            (ContentHash.computeText "support-config")
            false

    let usage calls tokens cost =
        KnowledgeClaimSupportUsage.create calls tokens cost

    let citation id chunkId =
        KnowledgeGenerationCitationObservation.create (KnowledgeGroundedCitationReferenceId.parse id) chunkId

    let claim verdict citations supportingIds =
        KnowledgeGenerationClaimObservation.create
            verdict
            citations
            (supportingIds |> List.map KnowledgeGroundedCitationReferenceId.parse)

    let observation outcome claims calls tokens cost duration =
        KnowledgeGenerationObservation.create outcome claims descriptor (usage calls tokens cost) duration

    let dimensions source format tenantPolicy queryType =
        KnowledgeEvaluationDimensions.create [ KnowledgeSourceId.parse source ] [ format ] "en" tenantPolicy queryType

    let answerCase =
        let relevant = chunk "1"

        KnowledgeEvaluationCase.create
            "answer"
            "What is supported?"
            (dimensions "handbook" "text/markdown" "standard" KnowledgeQueryType.FactLookup)
            []
            [ relevant ]
            [ KnowledgeExpectedFact.create "fact" "Supported fact." [ relevant ] ]
            []

    let abstentionCase =
        KnowledgeEvaluationCase.create
            "abstain"
            "What is not established?"
            (dimensions "policy" "application/pdf" "restricted" KnowledgeQueryType.Exploratory)
            []
            [ chunk "3" ]
            []
            []

    [<TestMethod>]
    member _.``Generation evaluation measures grounded answer and abstention quality with provenance``() =
        let relevantCitation = citation "relevant" (chunk "1")
        let irrelevantCitation = citation "irrelevant" (chunk "2")

        let answered =
            observation
                KnowledgeGenerationOutcome.Warned
                [ claim KnowledgeClaimSupportVerdict.Supported [ relevantCitation; irrelevantCitation ] [ "relevant" ]
                  claim KnowledgeClaimSupportVerdict.Unsupported [ irrelevantCitation ] [] ]
                1
                120
                0.02m
                (TimeSpan.FromMilliseconds 30.0)

        let abstained =
            observation KnowledgeGenerationOutcome.Abstained [] 1 40 0.01m (TimeSpan.FromMilliseconds 20.0)

        let dataset =
            KnowledgeEvaluationDataset.create "grounded-generation" 1 [ answerCase; abstentionCase ]

        let quality =
            KnowledgeGroundedGenerationEvaluator.measure
                dataset
                [ KnowledgeGenerationCaseResult.create "answer" [ chunk "1"; chunk "2" ] answered
                  KnowledgeGenerationCaseResult.create "abstain" [ chunk "3" ] abstained ]

        Assert.AreEqual(
            KnowledgeGroundedGenerationEvaluator.descriptor,
            KnowledgeGroundedGenerationQuality.evaluator quality
        )

        Assert.AreSame(dataset, KnowledgeGroundedGenerationQuality.dataset quality)
        Assert.AreEqual(Some 0.75, KnowledgeGroundedGenerationQuality.contextRelevance quality)
        Assert.AreEqual(Some 0.5, KnowledgeGroundedGenerationQuality.answerFaithfulness quality)
        Assert.AreEqual(Some(1.0 / 3.0), KnowledgeGroundedGenerationQuality.citationCorrectness quality)
        Assert.AreEqual(1.0, KnowledgeGroundedGenerationQuality.abstentionAccuracy quality)
        Assert.AreEqual(1, KnowledgeGroundedGenerationQuality.expectedAbstentions quality)
        Assert.AreEqual(1, KnowledgeGroundedGenerationQuality.actualAbstentions quality)
        Assert.AreEqual(1, KnowledgeGroundedGenerationQuality.correctAbstentions quality)
        Assert.AreEqual(1, KnowledgeGroundedGenerationQuality.correctAnswers quality)
        Assert.AreEqual(0, KnowledgeGroundedGenerationQuality.unresolvedResponses quality)
        Assert.AreEqual(2, KnowledgeGroundedGenerationQuality.totalModelCalls quality)
        Assert.AreEqual(160, KnowledgeGroundedGenerationQuality.totalTokens quality)
        Assert.AreEqual(0.03m, KnowledgeGroundedGenerationQuality.totalCostUsd quality)
        Assert.AreEqual(TimeSpan.FromMilliseconds 50.0, KnowledgeGroundedGenerationQuality.totalSupportDuration quality)
        Assert.AreEqual(9, quality |> KnowledgeGroundedGenerationQuality.segments |> List.length)

        let handbook =
            quality
            |> KnowledgeGroundedGenerationQuality.segments
            |> List.find (fun segmentQuality ->
                let segment = KnowledgeGenerationSegmentQuality.segment segmentQuality

                KnowledgeEvaluationSegment.dimension segment = KnowledgeEvaluationSegmentDimension.Source
                && KnowledgeEvaluationSegment.value segment = "handbook")

        Assert.AreEqual(1, KnowledgeGenerationSegmentQuality.caseCount handbook)
        Assert.AreEqual(Some 0.5, KnowledgeGenerationSegmentQuality.contextRelevance handbook)
        Assert.AreEqual(Some 0.5, KnowledgeGenerationSegmentQuality.answerFaithfulness handbook)
        Assert.AreEqual(0.02m, KnowledgeGenerationSegmentQuality.totalCostUsd handbook)

        let snapshot =
            KnowledgeQualitySnapshot.ofGroundedGeneration DateTimeOffset.UtcNow quality

        let snapshotMetrics = KnowledgeQualitySnapshot.metrics snapshot

        Assert.AreEqual(
            1m,
            snapshotMetrics[KnowledgeQualityMetricKey.overall KnowledgeQualityMetric.AbstentionAccuracy]
        )

        Assert.AreEqual(
            0.5m,
            snapshotMetrics[KnowledgeQualityMetricKey.segment
                                (KnowledgeGenerationSegmentQuality.segment handbook)
                                KnowledgeQualityMetric.AnswerFaithfulness]
        )

        let answeredMeasurement =
            quality |> KnowledgeGroundedGenerationQuality.cases |> List.head

        Assert.AreEqual(1, KnowledgeGenerationCaseMeasurement.relevantContextChunks answeredMeasurement)
        Assert.AreEqual(1, KnowledgeGenerationCaseMeasurement.faithfulClaims answeredMeasurement)
        Assert.AreEqual(2, KnowledgeGenerationCaseMeasurement.materialClaims answeredMeasurement)
        Assert.AreEqual(1, KnowledgeGenerationCaseMeasurement.correctCitations answeredMeasurement)
        Assert.AreEqual(3, KnowledgeGenerationCaseMeasurement.citations answeredMeasurement)
        Assert.AreEqual(descriptor, KnowledgeGenerationCaseMeasurement.supportEvaluator answeredMeasurement)

    [<TestMethod>]
    member _.``Unresolved outcomes fail abstention quality and malformed evidence is rejected``() =
        let dataset = KnowledgeEvaluationDataset.create "unresolved" 1 [ answerCase ]

        let rejected =
            observation KnowledgeGenerationOutcome.Rejected [] 0 0 0m TimeSpan.Zero

        let quality =
            KnowledgeGroundedGenerationEvaluator.measure
                dataset
                [ KnowledgeGenerationCaseResult.create "answer" [] rejected ]

        Assert.AreEqual(None, KnowledgeGroundedGenerationQuality.contextRelevance quality)
        Assert.AreEqual(None, KnowledgeGroundedGenerationQuality.answerFaithfulness quality)
        Assert.AreEqual(None, KnowledgeGroundedGenerationQuality.citationCorrectness quality)
        Assert.AreEqual(0.0, KnowledgeGroundedGenerationQuality.abstentionAccuracy quality)
        Assert.AreEqual(1, KnowledgeGroundedGenerationQuality.unresolvedResponses quality)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeGroundedGenerationEvaluator.measure dataset [] |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeGenerationCaseResult.create "answer" [ chunk "1"; chunk "1" ] rejected
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            claim KnowledgeClaimSupportVerdict.Supported [ citation "known" (chunk "1") ] [ "unknown" ]
            |> ignore)
        |> ignore
