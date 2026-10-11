namespace Nao.Eval.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval
open Nao.Knowledge

[<TestClass>]
type KnowledgeQualityRegressionTests() =
    let dimensions =
        KnowledgeEvaluationDimensions.create
            [ KnowledgeSourceId.parse "source" ]
            [ "text/plain" ]
            "en"
            "standard"
            KnowledgeQueryType.FactLookup

    let dataset =
        let chunk = KnowledgeChunkId.parse ("chunk_" + String.replicate 64 "1")

        KnowledgeEvaluationCase.create "case" "query" dimensions [] [ chunk ] [] []
        |> List.singleton
        |> KnowledgeEvaluationDataset.create "quality-gate" 1

    let evaluator = ComponentVersion.create "knowledge-evaluator" "1"
    let recordedAt = DateTimeOffset(2026, 10, 11, 23, 30, 0, TimeSpan.Zero)

    let snapshot caseCount metrics =
        metrics
        |> List.map (fun (metric, value) -> KnowledgeQualityMetricKey.overall metric, value)
        |> fun values -> KnowledgeQualitySnapshot.create dataset evaluator caseCount values recordedAt

    let config =
        KnowledgeQualityGateConfig.create
            10
            [ KnowledgeMetricThreshold.create
                  (KnowledgeQualityMetricKey.overall KnowledgeQualityMetric.RetrievalRecall)
                  0.8m
                  0.02m
              KnowledgeMetricThreshold.create
                  (KnowledgeQualityMetricKey.overall KnowledgeQualityMetric.P95LatencyMilliseconds)
                  250m
                  20m ]

    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "reviewer") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

    let owner = AuthorizationScope.identity authorization

    [<TestMethod>]
    member _.``Passing evidence promotes an auditable baseline and allows bounded regression``() =
        let initial =
            snapshot
                20
                [ KnowledgeQualityMetric.RetrievalRecall, 0.9m
                  KnowledgeQualityMetric.P95LatencyMilliseconds, 200m ]

        let initialGate = KnowledgeQualityGate.evaluate config None initial
        Assert.IsTrue(KnowledgeQualityGateResult.passed initialGate)

        let first =
            KnowledgeQualityBaselinePromotion.promote
                recordedAt
                authorization
                "Initial reviewed release baseline."
                initialGate

        Assert.AreEqual(KnowledgeQualityBaseline.currentVersion, KnowledgeQualityBaseline.version first)
        Assert.AreEqual(None, KnowledgeQualityBaseline.previousBaselineId first)
        Assert.AreEqual(owner, KnowledgeQualityBaseline.promotedBy first)

        Assert.AreEqual(
            KnowledgeQualityGateConfig.configurationHash config,
            KnowledgeQualityBaseline.gateConfigurationHash first
        )

        let candidate =
            snapshot
                20
                [ KnowledgeQualityMetric.RetrievalRecall, 0.885m
                  KnowledgeQualityMetric.P95LatencyMilliseconds, 215m ]

        let gate = KnowledgeQualityGate.evaluate config (Some first) candidate
        Assert.IsTrue(KnowledgeQualityGateResult.passed gate)

        let promoted =
            KnowledgeQualityBaselinePromotion.promote
                (recordedAt.AddDays 1.0)
                authorization
                "Reviewed candidate remains within release tolerances."
                gate

        Assert.AreEqual(Some(KnowledgeQualityBaseline.id first), KnowledgeQualityBaseline.previousBaselineId promoted)
        Assert.AreSame(candidate, KnowledgeQualityBaseline.snapshot promoted)

    [<TestMethod>]
    member _.``Absolute regression missing metric and sample failures block promotion``() =
        let baselineSnapshot =
            snapshot
                20
                [ KnowledgeQualityMetric.RetrievalRecall, 0.9m
                  KnowledgeQualityMetric.P95LatencyMilliseconds, 200m ]

        let baseline =
            KnowledgeQualityGate.evaluate config None baselineSnapshot
            |> KnowledgeQualityBaselinePromotion.promote recordedAt authorization "Initial baseline."

        let failing = snapshot 5 [ KnowledgeQualityMetric.RetrievalRecall, 0.75m ]

        let result = KnowledgeQualityGate.evaluate config (Some baseline) failing
        Assert.IsFalse(KnowledgeQualityGateResult.minimumCaseCountPassed result)
        Assert.IsFalse(KnowledgeQualityGateResult.passed result)

        let checks = KnowledgeQualityGateResult.checks result

        let recall =
            checks
            |> List.find (KnowledgeMetricGateCheck.metric >> (=) KnowledgeQualityMetric.RetrievalRecall)

        Assert.IsTrue(
            recall
            |> KnowledgeMetricGateCheck.failures
            |> List.contains KnowledgeThresholdFailure.BelowAbsoluteMinimum
        )

        Assert.IsTrue(
            recall
            |> KnowledgeMetricGateCheck.failures
            |> List.contains KnowledgeThresholdFailure.RegressedBeyondTolerance
        )

        let latency =
            checks
            |> List.find (
                KnowledgeMetricGateCheck.metric
                >> (=) KnowledgeQualityMetric.P95LatencyMilliseconds
            )

        Assert.AreEqual([ KnowledgeThresholdFailure.MissingMetric ], KnowledgeMetricGateCheck.failures latency)

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeQualityBaselinePromotion.promote recordedAt authorization "Must not promote." result
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Baseline comparison rejects different datasets and evaluator families``() =
        let candidate =
            snapshot
                20
                [ KnowledgeQualityMetric.RetrievalRecall, 0.9m
                  KnowledgeQualityMetric.P95LatencyMilliseconds, 200m ]

        let baseline =
            KnowledgeQualityGate.evaluate config None candidate
            |> KnowledgeQualityBaselinePromotion.promote recordedAt authorization "Initial baseline."

        let changedDataset =
            KnowledgeEvaluationDataset.create "quality-gate" 2 (KnowledgeEvaluationDataset.cases dataset)

        let changedSnapshot =
            KnowledgeQualitySnapshot.create
                changedDataset
                evaluator
                20
                (candidate |> KnowledgeQualitySnapshot.metrics |> Map.toSeq)
                recordedAt

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeQualityGate.evaluate config (Some baseline) changedSnapshot |> ignore)
        |> ignore

        let changedEvaluator =
            KnowledgeQualitySnapshot.create
                dataset
                (ComponentVersion.create "other-evaluator" "1")
                20
                (candidate |> KnowledgeQualitySnapshot.metrics |> Map.toSeq)
                recordedAt

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeQualityGate.evaluate config (Some baseline) changedEvaluator |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Segment thresholds gate scoped evidence independently``() =
        let sourceSegment =
            dimensions
            |> KnowledgeEvaluationSegment.ofDimensions
            |> List.find (fun segment ->
                let dimension = KnowledgeEvaluationSegment.dimension segment
                dimension = KnowledgeEvaluationSegmentDimension.Source)

        let key =
            KnowledgeQualityMetricKey.segment sourceSegment KnowledgeQualityMetric.AnswerFaithfulness

        let candidate =
            KnowledgeQualitySnapshot.create dataset evaluator 20 [ key, 0.95m ] recordedAt

        let scopedConfig =
            KnowledgeQualityGateConfig.create 10 [ KnowledgeMetricThreshold.create key 0.9m 0.01m ]

        let result = KnowledgeQualityGate.evaluate scopedConfig None candidate
        Assert.IsTrue(KnowledgeQualityGateResult.passed result)

        let check = result |> KnowledgeQualityGateResult.checks |> List.exactlyOne
        Assert.AreEqual(KnowledgeQualityMetricScope.Segment sourceSegment, KnowledgeMetricGateCheck.scope check)
