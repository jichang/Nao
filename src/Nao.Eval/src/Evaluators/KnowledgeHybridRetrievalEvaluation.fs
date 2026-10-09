namespace Nao.Eval

open System
open System.Text
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Nao.Knowledge

[<RequireQualifiedAccess>]
type KnowledgeRetrievalEvaluationStrategy =
    | Lexical
    | Vector
    | Hybrid

type KnowledgeRetrievalQualityCase =
    private
        { Id: string
          Query: string
          RelevantChunkIds: Set<KnowledgeChunkId> }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalQualityCase =
    let create id query relevantChunkIds =
        if String.IsNullOrWhiteSpace id || id <> id.Trim() then
            invalidArg (nameof id) "Retrieval quality case identity must be non-blank and trimmed."

        if String.IsNullOrWhiteSpace query || query <> query.Trim() then
            invalidArg (nameof query) "Retrieval quality query must be non-blank and trimmed."

        let relevantChunkIds = Set.ofSeq relevantChunkIds

        if relevantChunkIds.IsEmpty then
            invalidArg (nameof relevantChunkIds) "Retrieval quality cases require relevant chunks."

        { Id = id
          Query = query
          RelevantChunkIds = relevantChunkIds }

    let id value = value.Id
    let query value = value.Query
    let relevantChunkIds value = value.RelevantChunkIds

type KnowledgeRetrievalQualityDataset =
    private
        { Name: string
          Version: string
          ContentSha256: string
          Cases: KnowledgeRetrievalQualityCase list }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalQualityDataset =
    let private appendValue (builder: StringBuilder) (value: string) =
        builder.Append(value.Length).Append(':').Append(value).Append('|') |> ignore

    let private computeHash name version cases =
        let canonical = StringBuilder()
        appendValue canonical name
        appendValue canonical version

        for case in cases do
            appendValue canonical (KnowledgeRetrievalQualityCase.id case)
            appendValue canonical (KnowledgeRetrievalQualityCase.query case)

            case
            |> KnowledgeRetrievalQualityCase.relevantChunkIds
            |> Seq.map string
            |> Seq.sort
            |> Seq.iter (appendValue canonical)

        canonical
        |> _.ToString()
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let create name version cases =
        if String.IsNullOrWhiteSpace name || name <> name.Trim() then
            invalidArg (nameof name) "Retrieval quality dataset name must be non-blank and trimmed."

        if String.IsNullOrWhiteSpace version || version <> version.Trim() then
            invalidArg (nameof version) "Retrieval quality dataset version must be non-blank and trimmed."

        if List.isEmpty cases then
            invalidArg (nameof cases) "Retrieval quality datasets require at least one case."

        let duplicateIds =
            cases
            |> List.countBy KnowledgeRetrievalQualityCase.id
            |> List.choose (fun (id, count) -> if count > 1 then Some id else None)

        if not duplicateIds.IsEmpty then
            invalidArg (nameof cases) "Retrieval quality case identities must be unique."

        { Name = name
          Version = version
          ContentSha256 = computeHash name version cases
          Cases = cases }

    let name value = value.Name
    let version value = value.Version
    let contentSha256 value = value.ContentSha256
    let cases value = value.Cases

type KnowledgeRankedRetrievalEvaluator =
    { RetrieveAsync:
        KnowledgeRetrievalEvaluationStrategy
            -> KnowledgeRetrievalQualityCase
            -> int
            -> CancellationToken
            -> Task<KnowledgeChunkId list> }

type KnowledgeRetrievalCaseMeasurement =
    private
        { Case: KnowledgeRetrievalQualityCase
          Retrieved: KnowledgeChunkId list
          RelevantRetrieved: int
          Recall: float
          Precision: float
          ReciprocalRank: float
          NormalizedDiscountedCumulativeGain: float }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalCaseMeasurement =
    let case value = value.Case
    let retrieved value = value.Retrieved
    let relevantRetrieved value = value.RelevantRetrieved
    let recall value = value.Recall
    let precision value = value.Precision
    let reciprocalRank value = value.ReciprocalRank

    let normalizedDiscountedCumulativeGain value =
        value.NormalizedDiscountedCumulativeGain

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
          NormalizedDiscountedCumulativeGain: float }

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
          Dataset: KnowledgeRetrievalQualityDataset
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

    let private measureCase case retrieved =
        if isNull (box retrieved) then
            invalidOp "Ranked retrieval evaluator returned a null result."

        if (retrieved |> Set.ofList).Count <> retrieved.Length then
            invalidOp "Ranked retrieval results cannot contain duplicate chunk identities."

        let relevant = KnowledgeRetrievalQualityCase.relevantChunkIds case
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
                discountedCumulativeGain / idealDiscountedCumulativeGain }

    let private aggregate
        strategy
        (measurements: KnowledgeRetrievalCaseMeasurement list)
        : KnowledgeRetrievalStrategyQuality =
        { Strategy = strategy
          Cases = measurements
          RetrievedChunks = measurements |> List.sumBy (_.Retrieved.Length)
          RelevantChunks = measurements |> List.sumBy (_.Case.RelevantChunkIds.Count)
          RelevantRetrieved = measurements |> List.sumBy _.RelevantRetrieved
          Recall = average _.Recall measurements
          Precision = average _.Precision measurements
          MeanReciprocalRank = average _.ReciprocalRank measurements
          NormalizedDiscountedCumulativeGain = average _.NormalizedDiscountedCumulativeGain measurements }

    let evaluateAsync
        (retriever: KnowledgeRankedRetrievalEvaluator)
        (dataset: KnowledgeRetrievalQualityDataset)
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
                KnowledgeRetrievalQualityDataset.cases dataset
                |> List.collect (fun case ->
                    strategies
                    |> List.map (fun strategy ->
                        task {
                            let! retrieved = retriever.RetrieveAsync strategy case cutoff cancellationToken

                            if not (isNull (box retrieved)) && retrieved.Length > cutoff then
                                invalidOp "Ranked retrieval evaluator exceeded the requested cutoff."

                            return strategy, measureCase case retrieved
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
