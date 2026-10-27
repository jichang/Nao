namespace Nao.Eval

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Nao.Knowledge

type KnowledgeGraphEvaluationOutput =
    private
        { ChunkIds: KnowledgeChunkId list
          CostUsd: decimal }

[<RequireQualifiedAccess>]
module KnowledgeGraphEvaluationOutput =
    let create chunkIds costUsd =
        if (chunkIds |> Set.ofList).Count <> chunkIds.Length then
            invalidArg (nameof chunkIds) "Graph evaluation output cannot contain duplicate chunks."

        if costUsd < 0M then
            invalidArg (nameof costUsd) "Graph evaluation cost cannot be negative."

        { ChunkIds = chunkIds
          CostUsd = costUsd }

    let chunkIds value = value.ChunkIds
    let costUsd value = value.CostUsd

type KnowledgeGraphRetrievalEvaluator =
    { RetrieveAsync: bool -> KnowledgeEvaluationCase -> int -> CancellationToken -> Task<KnowledgeGraphEvaluationOutput> }

type KnowledgeGraphRetrievalQuality =
    private
        { Recall: float
          NormalizedDiscountedCumulativeGain: float
          MeanLatency: TimeSpan
          TotalCostUsd: decimal }

[<RequireQualifiedAccess>]
module KnowledgeGraphRetrievalQuality =
    let recall value = value.Recall

    let normalizedDiscountedCumulativeGain value =
        value.NormalizedDiscountedCumulativeGain

    let meanLatency value = value.MeanLatency
    let totalCostUsd value = value.TotalCostUsd

type KnowledgeGraphQualityGate =
    private
        { MinimumRecallDelta: float
          MinimumNdcgDelta: float
          MaximumLatencyIncrease: TimeSpan
          MaximumCostIncreaseUsd: decimal }

[<RequireQualifiedAccess>]
module KnowledgeGraphQualityGate =
    let create minimumRecallDelta minimumNdcgDelta maximumLatencyIncrease maximumCostIncreaseUsd =
        if
            not (Double.IsFinite minimumRecallDelta)
            || not (Double.IsFinite minimumNdcgDelta)
            || minimumRecallDelta < 0.0
            || minimumNdcgDelta < 0.0
            || maximumLatencyIncrease < TimeSpan.Zero
            || maximumCostIncreaseUsd < 0M
        then
            invalidArg (nameof minimumRecallDelta) "Graph quality thresholds must be finite and non-negative."

        { MinimumRecallDelta = minimumRecallDelta
          MinimumNdcgDelta = minimumNdcgDelta
          MaximumLatencyIncrease = maximumLatencyIncrease
          MaximumCostIncreaseUsd = maximumCostIncreaseUsd }

[<RequireQualifiedAccess>]
type KnowledgeGraphEnablementDecision =
    | Enabled
    | Disabled of reasons: string list

type KnowledgeGraphRetrievalEvaluation =
    private
        { Baseline: KnowledgeGraphRetrievalQuality
          WithGraph: KnowledgeGraphRetrievalQuality
          RecallDelta: float
          NdcgDelta: float
          LatencyIncrease: TimeSpan
          CostIncreaseUsd: decimal
          Decision: KnowledgeGraphEnablementDecision }

[<RequireQualifiedAccess>]
module KnowledgeGraphRetrievalEvaluation =
    let baseline value = value.Baseline
    let withGraph value = value.WithGraph
    let recallDelta value = value.RecallDelta
    let ndcgDelta value = value.NdcgDelta
    let latencyIncrease value = value.LatencyIncrease
    let costIncreaseUsd value = value.CostIncreaseUsd
    let decision value = value.Decision

    let enabled value =
        match value.Decision with
        | KnowledgeGraphEnablementDecision.Enabled -> true
        | KnowledgeGraphEnablementDecision.Disabled _ -> false

[<RequireQualifiedAccess>]
module KnowledgeGraphRetrievalEvaluationExecutor =
    let private measure (relevant: Set<KnowledgeChunkId>) (retrieved: KnowledgeChunkId list) =
        let hits = retrieved |> List.filter relevant.Contains |> List.length
        let recall = float hits / float relevant.Count

        let dcg =
            retrieved
            |> List.mapi (fun index id ->
                if relevant.Contains id then
                    1.0 / Math.Log2(float (index + 2))
                else
                    0.0)
            |> List.sum

        let ideal =
            [ 0 .. min (relevant.Count - 1) (retrieved.Length - 1) ]
            |> List.sumBy (fun index -> 1.0 / Math.Log2(float (index + 2)))

        recall, if ideal = 0.0 then 0.0 else dcg / ideal

    let evaluateAsync evaluator gate cutoff dataset cancellationToken =
        task {
            if cutoff <= 0 then
                invalidArg (nameof cutoff) "Graph evaluation cutoff must be positive."

            let cases = KnowledgeEvaluationDataset.cases dataset

            if List.isEmpty cases then
                invalidArg (nameof dataset) "Graph evaluation requires cases."

            let! measurements =
                [ false; true ]
                |> List.collect (fun useGraph -> cases |> List.map (fun case -> useGraph, case))
                |> List.map (fun (useGraph, case) ->
                    task {
                        let timer = Stopwatch.StartNew()
                        let! output = evaluator.RetrieveAsync useGraph case cutoff cancellationToken
                        timer.Stop()
                        let relevant = KnowledgeEvaluationCase.relevantChunkIds case

                        if relevant.IsEmpty then
                            invalidOp "Graph evaluation requires relevant chunk judgments."

                        let retrieved =
                            output |> KnowledgeGraphEvaluationOutput.chunkIds |> List.truncate cutoff

                        let recall, ndcg = measure relevant retrieved
                        return useGraph, recall, ndcg, timer.Elapsed, KnowledgeGraphEvaluationOutput.costUsd output
                    })
                |> Task.WhenAll

            let quality useGraph =
                let values =
                    measurements |> Array.filter (fun (graph, _, _, _, _) -> graph = useGraph)

                { Recall = values |> Array.averageBy (fun (_, value, _, _, _) -> value)
                  NormalizedDiscountedCumulativeGain = values |> Array.averageBy (fun (_, _, value, _, _) -> value)
                  MeanLatency =
                    values
                    |> Array.averageBy (fun (_, _, _, value, _) -> value.TotalMilliseconds)
                    |> TimeSpan.FromMilliseconds
                  TotalCostUsd = values |> Array.sumBy (fun (_, _, _, _, value) -> value) }

            let baseline = quality false
            let withGraph = quality true
            let recallDelta = withGraph.Recall - baseline.Recall

            let ndcgDelta =
                withGraph.NormalizedDiscountedCumulativeGain
                - baseline.NormalizedDiscountedCumulativeGain

            let latencyIncrease = withGraph.MeanLatency - baseline.MeanLatency
            let costIncrease = withGraph.TotalCostUsd - baseline.TotalCostUsd

            let reasons =
                [ if recallDelta < gate.MinimumRecallDelta then
                      "recall"
                  if ndcgDelta < gate.MinimumNdcgDelta then
                      "ndcg"
                  if latencyIncrease > gate.MaximumLatencyIncrease then
                      "latency"
                  if costIncrease > gate.MaximumCostIncreaseUsd then
                      "cost" ]

            return
                { Baseline = baseline
                  WithGraph = withGraph
                  RecallDelta = recallDelta
                  NdcgDelta = ndcgDelta
                  LatencyIncrease = latencyIncrease
                  CostIncreaseUsd = costIncrease
                  Decision =
                    if List.isEmpty reasons then
                        KnowledgeGraphEnablementDecision.Enabled
                    else
                        KnowledgeGraphEnablementDecision.Disabled reasons }
        }
