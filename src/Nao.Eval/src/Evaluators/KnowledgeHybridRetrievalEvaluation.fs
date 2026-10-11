namespace Nao.Eval

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Nao.Knowledge

[<RequireQualifiedAccess>]
type KnowledgeRetrievalEvaluationStrategy =
    | Lexical
    | Vector
    | Hybrid

type KnowledgeRankedRetrievalOutput =
    private
        { ChunkIds: KnowledgeChunkId list
          CostUsd: decimal }

[<RequireQualifiedAccess>]
module KnowledgeRankedRetrievalOutput =
    let create chunkIds costUsd =
        if isNull (box chunkIds) then
            nullArg (nameof chunkIds)

        if costUsd < 0m then
            invalidArg (nameof costUsd) "Ranked retrieval cost cannot be negative."

        { ChunkIds = chunkIds
          CostUsd = costUsd }

    let chunkIds value = value.ChunkIds
    let costUsd value = value.CostUsd

type KnowledgeRankedRetrievalEvaluator =
    { RetrieveAsync:
        KnowledgeRetrievalEvaluationStrategy
            -> KnowledgeEvaluationCase
            -> int
            -> CancellationToken
            -> Task<KnowledgeRankedRetrievalOutput> }

type KnowledgeRetrievalCaseMeasurement =
    private
        { Case: KnowledgeEvaluationCase
          Retrieved: KnowledgeChunkId list
          RelevantRetrieved: int
          Recall: float
          Precision: float
          ReciprocalRank: float
          NormalizedDiscountedCumulativeGain: float
          Latency: TimeSpan
          CostUsd: decimal }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalCaseMeasurement =
    let case value = value.Case
    let retrieved value = value.Retrieved
    let relevantRetrieved value = value.RelevantRetrieved
    let recall value = value.Recall
    let precision value = value.Precision
    let reciprocalRank value = value.ReciprocalRank
    let latency value = value.Latency
    let costUsd value = value.CostUsd

    let normalizedDiscountedCumulativeGain value =
        value.NormalizedDiscountedCumulativeGain

type KnowledgeRetrievalSegmentQuality =
    private
        { Segment: KnowledgeEvaluationSegment
          CaseCount: int
          Recall: float
          Precision: float
          MeanReciprocalRank: float
          NormalizedDiscountedCumulativeGain: float
          MeanLatency: TimeSpan
          P95Latency: TimeSpan
          TotalCostUsd: decimal }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalSegmentQuality =
    let segment value = value.Segment
    let caseCount value = value.CaseCount
    let recall value = value.Recall
    let precision value = value.Precision
    let meanReciprocalRank value = value.MeanReciprocalRank

    let normalizedDiscountedCumulativeGain value =
        value.NormalizedDiscountedCumulativeGain

    let meanLatency value = value.MeanLatency
    let p95Latency value = value.P95Latency
    let totalCostUsd value = value.TotalCostUsd

type KnowledgeRetrievalStrategyQuality =
    private
        { Strategy: KnowledgeRetrievalEvaluationStrategy
          Cases: KnowledgeRetrievalCaseMeasurement list
          RetrievedChunks: int
          RelevantChunks: int
          RelevantRetrieved: int
          Recall: float
          Precision: float
          MeanReciprocalRank: float
          NormalizedDiscountedCumulativeGain: float
          MeanLatency: TimeSpan
          P95Latency: TimeSpan
          TotalCostUsd: decimal
          Segments: KnowledgeRetrievalSegmentQuality list }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalStrategyQuality =
    let strategy value = value.Strategy
    let cases value = value.Cases
    let retrievedChunks value = value.RetrievedChunks
    let relevantChunks value = value.RelevantChunks
    let relevantRetrieved value = value.RelevantRetrieved
    let recall value = value.Recall
    let precision value = value.Precision
    let meanReciprocalRank value = value.MeanReciprocalRank
    let meanLatency value = value.MeanLatency
    let p95Latency value = value.P95Latency
    let totalCostUsd value = value.TotalCostUsd
    let segments value = value.Segments

    let normalizedDiscountedCumulativeGain value =
        value.NormalizedDiscountedCumulativeGain

type KnowledgeHybridQualityGate =
    private
        { ImprovementEpsilon: float
          ParityTolerance: float
          ParityJustification: string option }

[<RequireQualifiedAccess>]
module KnowledgeHybridQualityGate =
    let create improvementEpsilon parityTolerance parityJustification =
        if not (Double.IsFinite improvementEpsilon) || improvementEpsilon <= 0.0 then
            invalidArg (nameof improvementEpsilon) "Hybrid quality improvement epsilon must be finite and positive."

        if not (Double.IsFinite parityTolerance) || parityTolerance < 0.0 then
            invalidArg (nameof parityTolerance) "Hybrid quality parity tolerance must be finite and non-negative."

        let parityJustification =
            parityJustification
            |> Option.map (fun value ->
                if String.IsNullOrWhiteSpace value || value <> value.Trim() then
                    invalidArg (nameof parityJustification) "Parity justification must be non-blank and trimmed."

                value)

        { ImprovementEpsilon = improvementEpsilon
          ParityTolerance = parityTolerance
          ParityJustification = parityJustification }

[<RequireQualifiedAccess>]
type KnowledgeHybridQualityDecision =
    | Improved
    | JustifiedParity of justification: string
    | Failed

type KnowledgeHybridRetrievalEvaluation =
    private
        { Evaluator: ComponentVersion
          Dataset: KnowledgeEvaluationDataset
          Cutoff: int
          Lexical: KnowledgeRetrievalStrategyQuality
          Vector: KnowledgeRetrievalStrategyQuality
          Hybrid: KnowledgeRetrievalStrategyQuality
          RecallDelta: float
          PrecisionDelta: float
          MeanReciprocalRankDelta: float
          NormalizedDiscountedCumulativeGainDelta: float
          Decision: KnowledgeHybridQualityDecision }

[<RequireQualifiedAccess>]
module KnowledgeHybridRetrievalEvaluation =
    let evaluator value = value.Evaluator
    let dataset value = value.Dataset
    let cutoff value = value.Cutoff
    let lexical value = value.Lexical
    let vector value = value.Vector
    let hybrid value = value.Hybrid
    let recallDelta value = value.RecallDelta
    let precisionDelta value = value.PrecisionDelta
    let meanReciprocalRankDelta value = value.MeanReciprocalRankDelta

    let normalizedDiscountedCumulativeGainDelta value =
        value.NormalizedDiscountedCumulativeGainDelta

    let decision value = value.Decision

    let accepted value =
        match value.Decision with
        | KnowledgeHybridQualityDecision.Improved
        | KnowledgeHybridQualityDecision.JustifiedParity _ -> true
        | KnowledgeHybridQualityDecision.Failed -> false

[<RequireQualifiedAccess>]
module KnowledgeHybridRetrievalEvaluator =
    let descriptor = ComponentVersion.create "nao-hybrid-retrieval-evaluation" "1"

    let private average
        (selector: KnowledgeRetrievalCaseMeasurement -> float)
        (values: KnowledgeRetrievalCaseMeasurement list)
        =
        values |> List.averageBy selector

    let private measureCase case retrieved latency costUsd =
        if isNull (box retrieved) then
            invalidOp "Ranked retrieval evaluator returned a null result."

        if (retrieved |> Set.ofList).Count <> retrieved.Length then
            invalidOp "Ranked retrieval results cannot contain duplicate chunk identities."

        let relevant = KnowledgeEvaluationCase.relevantChunkIds case

        if relevant.IsEmpty then
            invalidOp "Hybrid retrieval evaluation requires relevant chunk judgments for every case."

        let relevantRetrieved = retrieved |> List.filter relevant.Contains |> List.length

        let reciprocalRank =
            retrieved
            |> List.tryFindIndex relevant.Contains
            |> Option.map (fun index -> 1.0 / float (index + 1))
            |> Option.defaultValue 0.0

        let discountedCumulativeGain =
            retrieved
            |> List.mapi (fun index chunkId ->
                if relevant.Contains chunkId then
                    1.0 / Math.Log2(float (index + 2))
                else
                    0.0)
            |> List.sum

        let idealDiscountedCumulativeGain =
            [ 0 .. min relevant.Count retrieved.Length - 1 ]
            |> List.sumBy (fun index -> 1.0 / Math.Log2(float (index + 2)))

        { Case = case
          Retrieved = retrieved
          RelevantRetrieved = relevantRetrieved
          Recall = float relevantRetrieved / float relevant.Count
          Precision =
            if retrieved.IsEmpty then
                0.0
            else
                float relevantRetrieved / float retrieved.Length
          ReciprocalRank = reciprocalRank
          NormalizedDiscountedCumulativeGain =
            if idealDiscountedCumulativeGain = 0.0 then
                0.0
            else
                discountedCumulativeGain / idealDiscountedCumulativeGain
          Latency = latency
          CostUsd = costUsd }

    let private meanLatency measurements =
        measurements
        |> List.averageBy (KnowledgeRetrievalCaseMeasurement.latency >> _.Ticks >> float)
        |> int64
        |> TimeSpan.FromTicks

    let private p95Latency measurements =
        let ordered =
            measurements
            |> List.map KnowledgeRetrievalCaseMeasurement.latency
            |> List.sort
            |> List.toArray

        let index = int (Math.Ceiling(0.95 * float ordered.Length)) - 1
        ordered[max 0 index]

    let private segmentQuality segment (measurements: KnowledgeRetrievalCaseMeasurement list) =
        { Segment = segment
          CaseCount = measurements.Length
          Recall = average _.Recall measurements
          Precision = average _.Precision measurements
          MeanReciprocalRank = average _.ReciprocalRank measurements
          NormalizedDiscountedCumulativeGain = average _.NormalizedDiscountedCumulativeGain measurements
          MeanLatency = meanLatency measurements
          P95Latency = p95Latency measurements
          TotalCostUsd = measurements |> List.sumBy KnowledgeRetrievalCaseMeasurement.costUsd }

    let private segments measurements =
        measurements
        |> List.collect (fun measurement ->
            measurement
            |> KnowledgeRetrievalCaseMeasurement.case
            |> KnowledgeEvaluationCase.dimensions
            |> KnowledgeEvaluationSegment.ofDimensions
            |> List.map (fun segment -> segment, measurement))
        |> List.groupBy fst
        |> List.map (fun (segment, values) -> values |> List.map snd |> segmentQuality segment)
        |> List.sortBy (fun quality ->
            let segment = KnowledgeRetrievalSegmentQuality.segment quality
            KnowledgeEvaluationSegment.dimension segment, KnowledgeEvaluationSegment.value segment)

    let private aggregate
        strategy
        (measurements: KnowledgeRetrievalCaseMeasurement list)
        : KnowledgeRetrievalStrategyQuality =
        { Strategy = strategy
          Cases = measurements
          RetrievedChunks = measurements |> List.sumBy (_.Retrieved.Length)
          RelevantChunks =
            measurements
            |> List.sumBy (
                KnowledgeRetrievalCaseMeasurement.case
                >> KnowledgeEvaluationCase.relevantChunkIds
                >> _.Count
            )
          RelevantRetrieved = measurements |> List.sumBy _.RelevantRetrieved
          Recall = average _.Recall measurements
          Precision = average _.Precision measurements
          MeanReciprocalRank = average _.ReciprocalRank measurements
          NormalizedDiscountedCumulativeGain = average _.NormalizedDiscountedCumulativeGain measurements
          MeanLatency = meanLatency measurements
          P95Latency = p95Latency measurements
          TotalCostUsd = measurements |> List.sumBy KnowledgeRetrievalCaseMeasurement.costUsd
          Segments = segments measurements }

    let evaluateAsync
        (retriever: KnowledgeRankedRetrievalEvaluator)
        (dataset: KnowledgeEvaluationDataset)
        cutoff
        (gate: KnowledgeHybridQualityGate)
        (cancellationToken: CancellationToken)
        : Task<KnowledgeHybridRetrievalEvaluation> =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            if isNull (box retriever) || isNull (box retriever.RetrieveAsync) then
                nullArg (nameof retriever)

            if isNull (box dataset) then
                nullArg (nameof dataset)

            if cutoff <= 0 then
                invalidArg (nameof cutoff) "Retrieval quality cutoff must be positive."

            if isNull (box gate) then
                nullArg (nameof gate)

            let strategies =
                [ KnowledgeRetrievalEvaluationStrategy.Lexical
                  KnowledgeRetrievalEvaluationStrategy.Vector
                  KnowledgeRetrievalEvaluationStrategy.Hybrid ]

            let! measured =
                KnowledgeEvaluationDataset.cases dataset
                |> List.collect (fun case ->
                    strategies
                    |> List.map (fun strategy ->
                        task {
                            let stopwatch = Stopwatch.StartNew()
                            let! output = retriever.RetrieveAsync strategy case cutoff cancellationToken
                            stopwatch.Stop()

                            if isNull (box output) then
                                invalidOp "Ranked retrieval evaluator returned a null output."

                            let retrieved = KnowledgeRankedRetrievalOutput.chunkIds output

                            if retrieved.Length > cutoff then
                                invalidOp "Ranked retrieval evaluator exceeded the requested cutoff."

                            return
                                strategy,
                                measureCase
                                    case
                                    retrieved
                                    stopwatch.Elapsed
                                    (KnowledgeRankedRetrievalOutput.costUsd output)
                        }))
                |> Task.WhenAll

            let byStrategy strategy =
                measured
                |> Array.choose (fun (actual, measurement) -> if actual = strategy then Some measurement else None)
                |> List.ofArray
                |> aggregate strategy

            let lexical = byStrategy KnowledgeRetrievalEvaluationStrategy.Lexical
            let vector = byStrategy KnowledgeRetrievalEvaluationStrategy.Vector
            let hybrid = byStrategy KnowledgeRetrievalEvaluationStrategy.Hybrid

            let delta selector =
                selector hybrid - max (selector lexical) (selector vector)

            let recallDelta = delta _.Recall
            let precisionDelta = delta _.Precision
            let meanReciprocalRankDelta = delta _.MeanReciprocalRank

            let normalizedDiscountedCumulativeGainDelta =
                delta _.NormalizedDiscountedCumulativeGain

            let deltas =
                [ recallDelta
                  precisionDelta
                  meanReciprocalRankDelta
                  normalizedDiscountedCumulativeGainDelta ]

            let noRegression =
                deltas |> List.forall (fun value -> value >= -gate.ParityTolerance)

            let improved =
                noRegression
                && (deltas |> List.exists (fun value -> value >= gate.ImprovementEpsilon))

            let parity = deltas |> List.forall (fun value -> abs value <= gate.ParityTolerance)

            let decision =
                if improved then
                    KnowledgeHybridQualityDecision.Improved
                elif noRegression && parity then
                    match gate.ParityJustification with
                    | Some justification -> KnowledgeHybridQualityDecision.JustifiedParity justification
                    | None -> KnowledgeHybridQualityDecision.Failed
                else
                    KnowledgeHybridQualityDecision.Failed

            return
                { Evaluator = descriptor
                  Dataset = dataset
                  Cutoff = cutoff
                  Lexical = lexical
                  Vector = vector
                  Hybrid = hybrid
                  RecallDelta = recallDelta
                  PrecisionDelta = precisionDelta
                  MeanReciprocalRankDelta = meanReciprocalRankDelta
                  NormalizedDiscountedCumulativeGainDelta = normalizedDiscountedCumulativeGainDelta
                  Decision = decision }
        }
