namespace Nao.Eval

open System
open System.Globalization
open System.Text
open Nao.Agents
open Nao.Knowledge

[<RequireQualifiedAccess>]
type KnowledgeQualityMetric =
    | RetrievalRecall
    | RetrievalPrecision
    | MeanReciprocalRank
    | NormalizedDiscountedCumulativeGain
    | ContextRelevance
    | AnswerFaithfulness
    | CitationCorrectness
    | AbstentionAccuracy
    | MeanLatencyMilliseconds
    | P95LatencyMilliseconds
    | MeanSupportLatencyMilliseconds
    | TotalCostUsd

[<RequireQualifiedAccess>]
type KnowledgeMetricDirection =
    | HigherIsBetter
    | LowerIsBetter

[<RequireQualifiedAccess>]
module KnowledgeQualityMetric =
    let key =
        function
        | KnowledgeQualityMetric.RetrievalRecall -> "retrieval-recall"
        | KnowledgeQualityMetric.RetrievalPrecision -> "retrieval-precision"
        | KnowledgeQualityMetric.MeanReciprocalRank -> "mean-reciprocal-rank"
        | KnowledgeQualityMetric.NormalizedDiscountedCumulativeGain -> "normalized-discounted-cumulative-gain"
        | KnowledgeQualityMetric.ContextRelevance -> "context-relevance"
        | KnowledgeQualityMetric.AnswerFaithfulness -> "answer-faithfulness"
        | KnowledgeQualityMetric.CitationCorrectness -> "citation-correctness"
        | KnowledgeQualityMetric.AbstentionAccuracy -> "abstention-accuracy"
        | KnowledgeQualityMetric.MeanLatencyMilliseconds -> "mean-latency-ms"
        | KnowledgeQualityMetric.P95LatencyMilliseconds -> "p95-latency-ms"
        | KnowledgeQualityMetric.MeanSupportLatencyMilliseconds -> "mean-support-latency-ms"
        | KnowledgeQualityMetric.TotalCostUsd -> "total-cost-usd"

    let direction =
        function
        | KnowledgeQualityMetric.RetrievalRecall
        | KnowledgeQualityMetric.RetrievalPrecision
        | KnowledgeQualityMetric.MeanReciprocalRank
        | KnowledgeQualityMetric.NormalizedDiscountedCumulativeGain
        | KnowledgeQualityMetric.ContextRelevance
        | KnowledgeQualityMetric.AnswerFaithfulness
        | KnowledgeQualityMetric.CitationCorrectness
        | KnowledgeQualityMetric.AbstentionAccuracy -> KnowledgeMetricDirection.HigherIsBetter
        | KnowledgeQualityMetric.MeanLatencyMilliseconds
        | KnowledgeQualityMetric.P95LatencyMilliseconds
        | KnowledgeQualityMetric.MeanSupportLatencyMilliseconds
        | KnowledgeQualityMetric.TotalCostUsd -> KnowledgeMetricDirection.LowerIsBetter

    let isUnitInterval metric =
        direction metric = KnowledgeMetricDirection.HigherIsBetter

[<RequireQualifiedAccess>]
type KnowledgeQualityMetricScope =
    | Overall
    | Segment of KnowledgeEvaluationSegment

type KnowledgeQualityMetricKey =
    private
        { Metric: KnowledgeQualityMetric
          Scope: KnowledgeQualityMetricScope }

[<RequireQualifiedAccess>]
module KnowledgeQualityMetricKey =
    let overall metric =
        { Metric = metric
          Scope = KnowledgeQualityMetricScope.Overall }

    let segment segment metric =
        { Metric = metric
          Scope = KnowledgeQualityMetricScope.Segment segment }

    let metric value = value.Metric
    let scope value = value.Scope

    let key value =
        match value.Scope with
        | KnowledgeQualityMetricScope.Overall -> $"overall:{KnowledgeQualityMetric.key value.Metric}"
        | KnowledgeQualityMetricScope.Segment segment ->
            let dimension = KnowledgeEvaluationSegment.dimension segment
            let segmentValue = KnowledgeEvaluationSegment.value segment
            $"segment:{dimension}:{segmentValue}:{KnowledgeQualityMetric.key value.Metric}"

type KnowledgeQualitySnapshot =
    private
        { DatasetName: string
          DatasetRevision: int
          DatasetContentHash: ContentHash
          Evaluator: ComponentVersion
          CaseCount: int
          Metrics: Map<KnowledgeQualityMetricKey, decimal>
          RecordedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module KnowledgeQualitySnapshot =
    let create dataset evaluator caseCount metrics recordedAt =
        if isNull (box dataset) then
            nullArg (nameof dataset)

        if isNull (box evaluator) then
            nullArg (nameof evaluator)

        if caseCount <= 0 then
            invalidArg (nameof caseCount) "Knowledge quality snapshots require a positive case count."

        if isNull (box metrics) then
            nullArg (nameof metrics)

        let metrics = Map.ofSeq metrics

        if metrics.IsEmpty then
            invalidArg (nameof metrics) "Knowledge quality snapshots require at least one metric."

        metrics
        |> Map.iter (fun key value ->
            if value < 0m then
                invalidArg (nameof metrics) "Knowledge quality metric values cannot be negative."

            if
                key |> KnowledgeQualityMetricKey.metric |> KnowledgeQualityMetric.isUnitInterval
                && value > 1m
            then
                invalidArg (nameof metrics) "Knowledge quality ratio metrics cannot exceed one.")

        { DatasetName = KnowledgeEvaluationDataset.name dataset
          DatasetRevision = KnowledgeEvaluationDataset.revision dataset
          DatasetContentHash = KnowledgeEvaluationDataset.contentHash dataset
          Evaluator = evaluator
          CaseCount = caseCount
          Metrics = metrics
          RecordedAt = recordedAt }

    let datasetName value = value.DatasetName
    let datasetRevision value = value.DatasetRevision
    let datasetContentHash value = value.DatasetContentHash
    let evaluator value = value.Evaluator
    let caseCount value = value.CaseCount
    let metrics value = value.Metrics
    let recordedAt value = value.RecordedAt

    let ofHybridRetrieval recordedAt evaluation =
        if isNull (box evaluation) then
            nullArg (nameof evaluation)

        let dataset = KnowledgeHybridRetrievalEvaluation.dataset evaluation
        let quality = KnowledgeHybridRetrievalEvaluation.hybrid evaluation

        let values key (quality: KnowledgeRetrievalSegmentQuality) =
            [ key KnowledgeQualityMetric.RetrievalRecall, decimal (KnowledgeRetrievalSegmentQuality.recall quality)
              key KnowledgeQualityMetric.RetrievalPrecision,
              decimal (KnowledgeRetrievalSegmentQuality.precision quality)
              key KnowledgeQualityMetric.MeanReciprocalRank,
              decimal (KnowledgeRetrievalSegmentQuality.meanReciprocalRank quality)
              key KnowledgeQualityMetric.NormalizedDiscountedCumulativeGain,
              decimal (KnowledgeRetrievalSegmentQuality.normalizedDiscountedCumulativeGain quality)
              key KnowledgeQualityMetric.MeanLatencyMilliseconds,
              decimal (KnowledgeRetrievalSegmentQuality.meanLatency quality).TotalMilliseconds
              key KnowledgeQualityMetric.P95LatencyMilliseconds,
              decimal (KnowledgeRetrievalSegmentQuality.p95Latency quality).TotalMilliseconds
              key KnowledgeQualityMetric.TotalCostUsd, KnowledgeRetrievalSegmentQuality.totalCostUsd quality ]

        let segmented =
            quality
            |> KnowledgeRetrievalStrategyQuality.segments
            |> List.collect (fun segmentQuality ->
                let segment = KnowledgeRetrievalSegmentQuality.segment segmentQuality
                values (KnowledgeQualityMetricKey.segment segment) segmentQuality)

        create
            dataset
            (KnowledgeHybridRetrievalEvaluation.evaluator evaluation)
            (quality |> KnowledgeRetrievalStrategyQuality.cases |> List.length)
            ([ KnowledgeQualityMetricKey.overall KnowledgeQualityMetric.RetrievalRecall,
               decimal (KnowledgeRetrievalStrategyQuality.recall quality)
               KnowledgeQualityMetricKey.overall KnowledgeQualityMetric.RetrievalPrecision,
               decimal (KnowledgeRetrievalStrategyQuality.precision quality)
               KnowledgeQualityMetricKey.overall KnowledgeQualityMetric.MeanReciprocalRank,
               decimal (KnowledgeRetrievalStrategyQuality.meanReciprocalRank quality)
               KnowledgeQualityMetricKey.overall KnowledgeQualityMetric.NormalizedDiscountedCumulativeGain,
               decimal (KnowledgeRetrievalStrategyQuality.normalizedDiscountedCumulativeGain quality)
               KnowledgeQualityMetricKey.overall KnowledgeQualityMetric.MeanLatencyMilliseconds,
               decimal (KnowledgeRetrievalStrategyQuality.meanLatency quality).TotalMilliseconds
               KnowledgeQualityMetricKey.overall KnowledgeQualityMetric.P95LatencyMilliseconds,
               decimal (KnowledgeRetrievalStrategyQuality.p95Latency quality).TotalMilliseconds
               KnowledgeQualityMetricKey.overall KnowledgeQualityMetric.TotalCostUsd,
               KnowledgeRetrievalStrategyQuality.totalCostUsd quality ]
             @ segmented)
            recordedAt

    let ofGroundedGeneration recordedAt quality =
        if isNull (box quality) then
            nullArg (nameof quality)

        let cases = KnowledgeGroundedGenerationQuality.cases quality

        let values
            (key: KnowledgeQualityMetric -> KnowledgeQualityMetricKey)
            (contextRelevance: float option)
            (answerFaithfulness: float option)
            (citationCorrectness: float option)
            (abstentionAccuracy: float)
            (duration: TimeSpan)
            (caseCount: int)
            (cost: decimal)
            =
            [ contextRelevance
              |> Option.map (fun value -> key KnowledgeQualityMetric.ContextRelevance, decimal value)
              answerFaithfulness
              |> Option.map (fun value -> key KnowledgeQualityMetric.AnswerFaithfulness, decimal value)
              citationCorrectness
              |> Option.map (fun value -> key KnowledgeQualityMetric.CitationCorrectness, decimal value)
              Some(key KnowledgeQualityMetric.AbstentionAccuracy, decimal abstentionAccuracy)
              Some(
                  key KnowledgeQualityMetric.MeanSupportLatencyMilliseconds,
                  decimal duration.TotalMilliseconds / decimal caseCount
              )
              Some(key KnowledgeQualityMetric.TotalCostUsd, cost) ]
            |> List.choose id

        let segmented =
            quality
            |> KnowledgeGroundedGenerationQuality.segments
            |> List.collect (fun segmentQuality ->
                let segment = KnowledgeGenerationSegmentQuality.segment segmentQuality

                values
                    (KnowledgeQualityMetricKey.segment segment)
                    (KnowledgeGenerationSegmentQuality.contextRelevance segmentQuality)
                    (KnowledgeGenerationSegmentQuality.answerFaithfulness segmentQuality)
                    (KnowledgeGenerationSegmentQuality.citationCorrectness segmentQuality)
                    (KnowledgeGenerationSegmentQuality.abstentionAccuracy segmentQuality)
                    (KnowledgeGenerationSegmentQuality.totalSupportDuration segmentQuality)
                    (KnowledgeGenerationSegmentQuality.caseCount segmentQuality)
                    (KnowledgeGenerationSegmentQuality.totalCostUsd segmentQuality))

        let metrics =
            values
                KnowledgeQualityMetricKey.overall
                (KnowledgeGroundedGenerationQuality.contextRelevance quality)
                (KnowledgeGroundedGenerationQuality.answerFaithfulness quality)
                (KnowledgeGroundedGenerationQuality.citationCorrectness quality)
                (KnowledgeGroundedGenerationQuality.abstentionAccuracy quality)
                (KnowledgeGroundedGenerationQuality.totalSupportDuration quality)
                cases.Length
                (KnowledgeGroundedGenerationQuality.totalCostUsd quality)
            |> fun overall -> overall @ segmented

        create
            (KnowledgeGroundedGenerationQuality.dataset quality)
            (KnowledgeGroundedGenerationQuality.evaluator quality)
            cases.Length
            metrics
            recordedAt

type KnowledgeMetricThreshold =
    private
        { Key: KnowledgeQualityMetricKey
          AbsoluteThreshold: decimal
          MaximumRegression: decimal }

[<RequireQualifiedAccess>]
module KnowledgeMetricThreshold =
    let create key absoluteThreshold maximumRegression =
        if absoluteThreshold < 0m then
            invalidArg (nameof absoluteThreshold) "Knowledge quality absolute thresholds cannot be negative."

        if maximumRegression < 0m then
            invalidArg (nameof maximumRegression) "Knowledge quality regression tolerances cannot be negative."

        if
            (key |> KnowledgeQualityMetricKey.metric |> KnowledgeQualityMetric.isUnitInterval)
            && (absoluteThreshold > 1m || maximumRegression > 1m)
        then
            invalidArg (nameof absoluteThreshold) "Knowledge quality ratio thresholds cannot exceed one."

        { Key = key
          AbsoluteThreshold = absoluteThreshold
          MaximumRegression = maximumRegression }

    let key value = value.Key

    let metric value =
        value.Key |> KnowledgeQualityMetricKey.metric

    let absoluteThreshold value = value.AbsoluteThreshold
    let maximumRegression value = value.MaximumRegression

type KnowledgeQualityGateConfig =
    private
        { MinimumCaseCount: int
          Thresholds: KnowledgeMetricThreshold list
          ConfigurationHash: ContentHash }

[<RequireQualifiedAccess>]
module KnowledgeQualityGateConfig =
    let create minimumCaseCount thresholds =
        if minimumCaseCount <= 0 then
            invalidArg (nameof minimumCaseCount) "Knowledge quality gates require a positive minimum case count."

        if isNull (box thresholds) || List.isEmpty thresholds then
            invalidArg (nameof thresholds) "Knowledge quality gates require at least one metric threshold."

        let duplicateMetrics =
            thresholds
            |> List.countBy KnowledgeMetricThreshold.key
            |> List.exists (fun (_, count) -> count > 1)

        if duplicateMetrics then
            invalidArg (nameof thresholds) "Knowledge quality gate metrics must be unique."

        let thresholds =
            thresholds
            |> List.sortBy (KnowledgeMetricThreshold.key >> KnowledgeQualityMetricKey.key)

        let canonical = StringBuilder().Append(minimumCaseCount).Append('|')

        thresholds
        |> List.iter (fun threshold ->
            canonical
                .Append(threshold |> KnowledgeMetricThreshold.key |> KnowledgeQualityMetricKey.key)
                .Append('|')
                .Append(
                    threshold
                    |> KnowledgeMetricThreshold.absoluteThreshold
                    |> _.ToString(CultureInfo.InvariantCulture)
                )
                .Append('|')
                .Append(
                    threshold
                    |> KnowledgeMetricThreshold.maximumRegression
                    |> _.ToString(CultureInfo.InvariantCulture)
                )
                .Append('|')
            |> ignore)

        { MinimumCaseCount = minimumCaseCount
          Thresholds = thresholds
          ConfigurationHash = canonical.ToString() |> ContentHash.computeText }

    let minimumCaseCount value = value.MinimumCaseCount
    let thresholds value = value.Thresholds
    let configurationHash value = value.ConfigurationHash

type KnowledgeQualityBaseline =
    private
        { Version: int
          Id: Guid
          Snapshot: KnowledgeQualitySnapshot
          GateConfigurationHash: ContentHash
          PreviousBaselineId: Guid option
          PromotedBy: AuthorizationIdentity
          Justification: string
          PromotedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module KnowledgeQualityBaseline =
    let currentVersion = 1
    let version value = value.Version
    let id value = value.Id
    let snapshot value = value.Snapshot
    let gateConfigurationHash value = value.GateConfigurationHash
    let previousBaselineId value = value.PreviousBaselineId
    let promotedBy value = value.PromotedBy
    let justification value = value.Justification
    let promotedAt value = value.PromotedAt

[<RequireQualifiedAccess>]
type KnowledgeThresholdFailure =
    | MissingMetric
    | BelowAbsoluteMinimum
    | AboveAbsoluteMaximum
    | RegressedBeyondTolerance

type KnowledgeMetricGateCheck =
    private
        { Key: KnowledgeQualityMetricKey
          Direction: KnowledgeMetricDirection
          CurrentValue: decimal option
          BaselineValue: decimal option
          AbsoluteThreshold: decimal
          MaximumRegression: decimal
          Failures: KnowledgeThresholdFailure list }

[<RequireQualifiedAccess>]
module KnowledgeMetricGateCheck =
    let key value = value.Key

    let metric value =
        value.Key |> KnowledgeQualityMetricKey.metric

    let scope value =
        value.Key |> KnowledgeQualityMetricKey.scope

    let direction value = value.Direction
    let currentValue value = value.CurrentValue
    let baselineValue value = value.BaselineValue
    let absoluteThreshold value = value.AbsoluteThreshold
    let maximumRegression value = value.MaximumRegression
    let failures value = value.Failures
    let passed value = List.isEmpty value.Failures

type KnowledgeQualityGateResult =
    private
        { Candidate: KnowledgeQualitySnapshot
          Baseline: KnowledgeQualityBaseline option
          ConfigurationHash: ContentHash
          MinimumCaseCount: int
          MinimumCaseCountPassed: bool
          Checks: KnowledgeMetricGateCheck list }

[<RequireQualifiedAccess>]
module KnowledgeQualityGateResult =
    let candidate value = value.Candidate
    let baseline value = value.Baseline
    let configurationHash value = value.ConfigurationHash
    let minimumCaseCount value = value.MinimumCaseCount
    let minimumCaseCountPassed value = value.MinimumCaseCountPassed
    let checks value = value.Checks

    let passed value =
        value.MinimumCaseCountPassed
        && (value.Checks |> List.forall KnowledgeMetricGateCheck.passed)

[<RequireQualifiedAccess>]
module KnowledgeQualityGate =
    let private validateBaseline candidate baseline =
        let snapshot = KnowledgeQualityBaseline.snapshot baseline

        if
            KnowledgeQualitySnapshot.datasetContentHash snapshot
            <> KnowledgeQualitySnapshot.datasetContentHash candidate
        then
            invalidArg (nameof baseline) "Knowledge quality baselines must use the candidate dataset content hash."

        if
            (KnowledgeQualitySnapshot.evaluator snapshot).ComponentId
            <> (KnowledgeQualitySnapshot.evaluator candidate).ComponentId
        then
            invalidArg (nameof baseline) "Knowledge quality baselines must use the candidate evaluator family."

    let evaluate config baseline candidate =
        if isNull (box config) then
            nullArg (nameof config)

        if isNull (box candidate) then
            nullArg (nameof candidate)

        baseline |> Option.iter (validateBaseline candidate)

        let candidateMetrics = KnowledgeQualitySnapshot.metrics candidate

        let baselineMetrics =
            baseline
            |> Option.map (KnowledgeQualityBaseline.snapshot >> KnowledgeQualitySnapshot.metrics)

        let checks =
            config
            |> KnowledgeQualityGateConfig.thresholds
            |> List.map (fun threshold ->
                let key = KnowledgeMetricThreshold.key threshold
                let metric = KnowledgeQualityMetricKey.metric key
                let direction = KnowledgeQualityMetric.direction metric
                let currentValue = Map.tryFind key candidateMetrics
                let baselineValue = baselineMetrics |> Option.bind (Map.tryFind key)

                let failures =
                    match currentValue with
                    | None -> [ KnowledgeThresholdFailure.MissingMetric ]
                    | Some current ->
                        let absoluteFailure =
                            match direction with
                            | KnowledgeMetricDirection.HigherIsBetter when
                                current < KnowledgeMetricThreshold.absoluteThreshold threshold
                                ->
                                [ KnowledgeThresholdFailure.BelowAbsoluteMinimum ]
                            | KnowledgeMetricDirection.LowerIsBetter when
                                current > KnowledgeMetricThreshold.absoluteThreshold threshold
                                ->
                                [ KnowledgeThresholdFailure.AboveAbsoluteMaximum ]
                            | _ -> []

                        let regressionFailure =
                            match direction, baselineValue with
                            | KnowledgeMetricDirection.HigherIsBetter, Some baselineValue when
                                baselineValue - current > KnowledgeMetricThreshold.maximumRegression threshold
                                ->
                                [ KnowledgeThresholdFailure.RegressedBeyondTolerance ]
                            | KnowledgeMetricDirection.LowerIsBetter, Some baselineValue when
                                current - baselineValue > KnowledgeMetricThreshold.maximumRegression threshold
                                ->
                                [ KnowledgeThresholdFailure.RegressedBeyondTolerance ]
                            | _ -> []

                        absoluteFailure @ regressionFailure

                { Key = key
                  Direction = direction
                  CurrentValue = currentValue
                  BaselineValue = baselineValue
                  AbsoluteThreshold = KnowledgeMetricThreshold.absoluteThreshold threshold
                  MaximumRegression = KnowledgeMetricThreshold.maximumRegression threshold
                  Failures = failures })

        { Candidate = candidate
          Baseline = baseline
          ConfigurationHash = KnowledgeQualityGateConfig.configurationHash config
          MinimumCaseCount = KnowledgeQualityGateConfig.minimumCaseCount config
          MinimumCaseCountPassed =
            KnowledgeQualitySnapshot.caseCount candidate
            >= KnowledgeQualityGateConfig.minimumCaseCount config
          Checks = checks }

[<RequireQualifiedAccess>]
module KnowledgeQualityBaselinePromotion =
    let promote promotedAt authorization justification gateResult =
        if String.IsNullOrWhiteSpace justification || justification <> justification.Trim() then
            invalidArg (nameof justification) "Knowledge quality baseline promotions require a non-blank justification."

        if isNull (box authorization) then
            nullArg (nameof authorization)

        if isNull (box gateResult) then
            nullArg (nameof gateResult)

        if not (KnowledgeQualityGateResult.passed gateResult) then
            invalidOp "Only passing knowledge quality evidence can be promoted to baseline."

        { Version = KnowledgeQualityBaseline.currentVersion
          Id = Guid.NewGuid()
          Snapshot = KnowledgeQualityGateResult.candidate gateResult
          GateConfigurationHash = KnowledgeQualityGateResult.configurationHash gateResult
          PreviousBaselineId =
            gateResult
            |> KnowledgeQualityGateResult.baseline
            |> Option.map KnowledgeQualityBaseline.id
          PromotedBy = AuthorizationScope.identity authorization
          Justification = justification
          PromotedAt = promotedAt }
