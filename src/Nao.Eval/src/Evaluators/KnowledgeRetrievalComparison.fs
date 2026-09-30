namespace Nao.Eval

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Nao.Knowledge

type KnowledgeRetrievalVariantRetriever =
    { RetrieveAsync:
        KnowledgeRetrievalRequest -> KnowledgeQueryVariant -> CancellationToken -> Task<KnowledgeChunkId list> }

type KnowledgeRetrievalQualityMeasurement =
    private
        { QueryCount: int
          RetrievedChunks: int
          RelevantChunks: int
          RelevantRetrieved: int
          Recall: float
          Precision: float
          LatencyMs: float }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalQualityMeasurement =
    let queryCount value = value.QueryCount
    let retrievedChunks value = value.RetrievedChunks
    let relevantChunks value = value.RelevantChunks
    let relevantRetrieved value = value.RelevantRetrieved
    let recall value = value.Recall
    let precision value = value.Precision
    let latencyMs value = value.LatencyMs

type KnowledgeRetrievalVariantMeasurement =
    private
        { Variant: KnowledgeQueryVariant
          Quality: KnowledgeRetrievalQualityMeasurement }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalVariantMeasurement =
    let variant value = value.Variant
    let quality value = value.Quality

type KnowledgeRetrievalComparison =
    private
        { Plan: KnowledgeQueryTransformationPlan
          Original: KnowledgeRetrievalQualityMeasurement
          Transformed: KnowledgeRetrievalQualityMeasurement
          Variants: KnowledgeRetrievalVariantMeasurement list
          RecallDelta: float
          PrecisionDelta: float
          LatencyDeltaMs: float }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalComparison =
    let plan value = value.Plan

    let request value =
        value.Plan |> KnowledgeQueryTransformationPlan.request

    let original value = value.Original
    let transformed value = value.Transformed
    let variants value = value.Variants
    let recallDelta value = value.RecallDelta
    let precisionDelta value = value.PrecisionDelta
    let latencyDeltaMs value = value.LatencyDeltaMs

    let private quality queryCount (relevant: Set<KnowledgeChunkId>) (retrieved: Set<KnowledgeChunkId>) latencyMs =
        let relevantRetrieved = Set.intersect relevant retrieved |> Set.count

        { QueryCount = queryCount
          RetrievedChunks = retrieved.Count
          RelevantChunks = relevant.Count
          RelevantRetrieved = relevantRetrieved
          Recall = float relevantRetrieved / float relevant.Count
          Precision =
            if retrieved.IsEmpty then
                0.0
            else
                float relevantRetrieved / float retrieved.Count
          LatencyMs = latencyMs }

    let evaluateAsync
        (retriever: KnowledgeRetrievalVariantRetriever)
        (plan: KnowledgeQueryTransformationPlan)
        (relevantChunkIds: KnowledgeChunkId seq)
        (cancellationToken: CancellationToken)
        : Task<KnowledgeRetrievalComparison> =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            if isNull (box retriever) || isNull (box retriever.RetrieveAsync) then
                nullArg (nameof retriever)

            let relevant = Set.ofSeq relevantChunkIds

            if relevant.IsEmpty then
                invalidArg (nameof relevantChunkIds) "Retrieval comparison requires at least one relevant chunk."

            let variants = KnowledgeQueryTransformationPlan.variants plan

            match variants with
            | [] -> invalidOp "Retrieval comparison plan contains no query variants."
            | original :: _ when KnowledgeQueryVariant.kind original <> KnowledgeQueryVariantKind.Original ->
                invalidOp "Retrieval comparison plan must begin with the original query variant."
            | _ -> ()

            let request = KnowledgeQueryTransformationPlan.request plan
            let limits = KnowledgeRetrievalRequest.limits request
            let maximumPerQuery = KnowledgeRetrievalLimits.maximumCandidatesPerQuery limits
            let maximumCandidates = KnowledgeRetrievalLimits.maximumCandidates limits

            let! measured =
                variants
                |> List.map (fun variant ->
                    task {
                        let timer = Stopwatch.StartNew()
                        let! retrieved = retriever.RetrieveAsync request variant cancellationToken
                        timer.Stop()

                        if isNull (box retrieved) then
                            invalidOp "Retrieval evaluator returned a null chunk collection."

                        let retrieved = Set.ofList retrieved

                        if retrieved.Count > maximumPerQuery then
                            invalidOp "Retrieval evaluator result exceeds the per-query candidate limit."

                        let quality = quality 1 relevant retrieved timer.Elapsed.TotalMilliseconds

                        return ({ Variant = variant; Quality = quality }, retrieved)
                    })
                |> Task.WhenAll

            let measured = List.ofArray measured
            let original = measured |> List.head
            let generated = measured |> List.tail

            let transformed =
                match generated with
                | [] -> (fst original).Quality
                | _ ->
                    let retrieved = generated |> Seq.collect (snd >> Set.toSeq) |> Set.ofSeq

                    if retrieved.Count > maximumCandidates then
                        invalidOp "Transformed retrieval result exceeds the total candidate limit."

                    quality
                        generated.Length
                        relevant
                        retrieved
                        (generated |> List.sumBy (fun (measurement, _) -> measurement.Quality.LatencyMs))

            let original = fst original
            let measured = measured |> List.map fst

            return
                { Plan = plan
                  Original = original.Quality
                  Transformed = transformed
                  Variants = measured
                  RecallDelta = transformed.Recall - original.Quality.Recall
                  PrecisionDelta = transformed.Precision - original.Quality.Precision
                  LatencyDeltaMs = transformed.LatencyMs - original.Quality.LatencyMs }
        }
