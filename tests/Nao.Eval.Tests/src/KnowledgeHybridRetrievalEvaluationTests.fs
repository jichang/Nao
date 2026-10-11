namespace Nao.Eval.Tests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Eval
open Nao.Knowledge

[<TestClass>]
type KnowledgeHybridRetrievalEvaluationTests() =
    let chunk suffix =
        KnowledgeChunkId.parse ("chunk_" + String.replicate 63 "0" + suffix)

    let qualityCase id query relevant =
        let dimensions =
            KnowledgeEvaluationDimensions.create
                [ KnowledgeSourceId.parse ("source-" + id) ]
                [ if id = "first" then "text/markdown" else "application/pdf" ]
                (if id = "first" then "en" else "fr")
                (if id = "first" then "standard" else "regulated")
                (if id = "first" then
                     KnowledgeQueryType.FactLookup
                 else
                     KnowledgeQueryType.Procedural)

        KnowledgeEvaluationCase.create id query dimensions [] relevant [] []

    [<TestMethod>]
    member _.``Hybrid evaluation records versioned evidence and improvement over both baselines``() =
        let first = qualityCase "first" "first query" [ chunk "1"; chunk "2" ]

        let second = qualityCase "second" "second query" [ chunk "3" ]

        let dataset =
            KnowledgeEvaluationDataset.create "hybrid-retrieval" 1 [ first; second ]

        let equivalent =
            KnowledgeEvaluationDataset.create "hybrid-retrieval" 1 [ first; second ]

        Assert.AreEqual(
            KnowledgeEvaluationDataset.contentHash dataset,
            KnowledgeEvaluationDataset.contentHash equivalent
        )

        Assert.AreEqual(
            64,
            dataset
            |> KnowledgeEvaluationDataset.contentHash
            |> ContentHash.value
            |> String.length
        )

        let invocations =
            ConcurrentBag<KnowledgeRetrievalEvaluationStrategy * string * int>()

        let retriever: KnowledgeRankedRetrievalEvaluator =
            { RetrieveAsync =
                fun strategy case cutoff _ ->
                    invocations.Add(strategy, KnowledgeEvaluationCase.id case, cutoff)

                    task {
                        do! Task.Delay 5

                        let chunks =
                            match KnowledgeEvaluationCase.id case, strategy with
                            | "first", KnowledgeRetrievalEvaluationStrategy.Lexical -> [ chunk "1"; chunk "8" ]
                            | "first", KnowledgeRetrievalEvaluationStrategy.Vector -> [ chunk "2"; chunk "8" ]
                            | "first", KnowledgeRetrievalEvaluationStrategy.Hybrid -> [ chunk "1"; chunk "2" ]
                            | "second", KnowledgeRetrievalEvaluationStrategy.Lexical -> [ chunk "8"; chunk "3" ]
                            | "second", KnowledgeRetrievalEvaluationStrategy.Vector -> [ chunk "3"; chunk "8" ]
                            | "second", KnowledgeRetrievalEvaluationStrategy.Hybrid -> [ chunk "3" ]
                            | value -> invalidOp $"Unexpected evaluation input: {value}."

                        let cost =
                            match strategy with
                            | KnowledgeRetrievalEvaluationStrategy.Lexical -> 0.001m
                            | KnowledgeRetrievalEvaluationStrategy.Vector -> 0.002m
                            | KnowledgeRetrievalEvaluationStrategy.Hybrid -> 0.003m

                        return KnowledgeRankedRetrievalOutput.create chunks cost
                    } }

        let evaluation =
            KnowledgeHybridRetrievalEvaluator.evaluateAsync
                retriever
                dataset
                2
                (KnowledgeHybridQualityGate.create 0.000001 0.000001 None)
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(6, invocations.Count)
        invocations |> Seq.iter (fun (_, _, cutoff) -> Assert.AreEqual(2, cutoff))

        Assert.AreEqual(
            KnowledgeHybridRetrievalEvaluator.descriptor,
            KnowledgeHybridRetrievalEvaluation.evaluator evaluation
        )

        Assert.AreSame(dataset, KnowledgeHybridRetrievalEvaluation.dataset evaluation)
        Assert.AreEqual(2, KnowledgeHybridRetrievalEvaluation.cutoff evaluation)
        Assert.AreEqual(KnowledgeHybridQualityDecision.Improved, KnowledgeHybridRetrievalEvaluation.decision evaluation)
        Assert.IsTrue(KnowledgeHybridRetrievalEvaluation.accepted evaluation)
        Assert.IsTrue(KnowledgeHybridRetrievalEvaluation.recallDelta evaluation > 0.0)
        Assert.IsTrue(KnowledgeHybridRetrievalEvaluation.precisionDelta evaluation > 0.0)
        Assert.AreEqual(0.0, KnowledgeHybridRetrievalEvaluation.meanReciprocalRankDelta evaluation, 0.000001)
        Assert.IsTrue(KnowledgeHybridRetrievalEvaluation.normalizedDiscountedCumulativeGainDelta evaluation > 0.0)

        let hybrid = KnowledgeHybridRetrievalEvaluation.hybrid evaluation
        Assert.AreEqual(1.0, KnowledgeRetrievalStrategyQuality.recall hybrid, 0.000001)
        Assert.AreEqual(1.0, KnowledgeRetrievalStrategyQuality.precision hybrid, 0.000001)
        Assert.AreEqual(1.0, KnowledgeRetrievalStrategyQuality.meanReciprocalRank hybrid, 0.000001)
        Assert.AreEqual(1.0, KnowledgeRetrievalStrategyQuality.normalizedDiscountedCumulativeGain hybrid, 0.000001)
        Assert.AreEqual(0.006m, KnowledgeRetrievalStrategyQuality.totalCostUsd hybrid)
        Assert.IsTrue(KnowledgeRetrievalStrategyQuality.meanLatency hybrid > TimeSpan.Zero)
        Assert.AreEqual(10, hybrid |> KnowledgeRetrievalStrategyQuality.segments |> List.length)

        let firstSource =
            hybrid
            |> KnowledgeRetrievalStrategyQuality.segments
            |> List.find (fun quality ->
                let segment = KnowledgeRetrievalSegmentQuality.segment quality

                KnowledgeEvaluationSegment.dimension segment = KnowledgeEvaluationSegmentDimension.Source
                && KnowledgeEvaluationSegment.value segment = "source-first")

        Assert.AreEqual(1, KnowledgeRetrievalSegmentQuality.caseCount firstSource)
        Assert.AreEqual(1.0, KnowledgeRetrievalSegmentQuality.recall firstSource)
        Assert.AreEqual(0.003m, KnowledgeRetrievalSegmentQuality.totalCostUsd firstSource)

        let snapshot =
            KnowledgeQualitySnapshot.ofHybridRetrieval DateTimeOffset.UtcNow evaluation

        let snapshotMetrics = KnowledgeQualitySnapshot.metrics snapshot

        Assert.AreEqual(1m, snapshotMetrics[KnowledgeQualityMetricKey.overall KnowledgeQualityMetric.RetrievalRecall])

        Assert.AreEqual(
            1m,
            snapshotMetrics[KnowledgeQualityMetricKey.segment
                                (KnowledgeRetrievalSegmentQuality.segment firstSource)
                                KnowledgeQualityMetric.RetrievalRecall]
        )

        Assert.IsTrue(
            KnowledgeRetrievalStrategyQuality.p95Latency hybrid
            >= KnowledgeRetrievalStrategyQuality.meanLatency hybrid
        )

        hybrid
        |> KnowledgeRetrievalStrategyQuality.cases
        |> List.iter (fun measurement ->
            Assert.AreEqual(0.003m, KnowledgeRetrievalCaseMeasurement.costUsd measurement)
            Assert.IsTrue(KnowledgeRetrievalCaseMeasurement.latency measurement > TimeSpan.Zero))

    [<TestMethod>]
    member _.``Parity requires justification and malformed rankings fail closed``() =
        let relevant = chunk "1"

        let dataset =
            KnowledgeEvaluationDataset.create "parity" 1 [ qualityCase "case" "query" [ relevant ] ]

        let parityRetriever: KnowledgeRankedRetrievalEvaluator =
            { RetrieveAsync = fun _ _ _ _ -> KnowledgeRankedRetrievalOutput.create [ relevant ] 0m |> Task.FromResult }

        let evaluate gate (retriever: KnowledgeRankedRetrievalEvaluator) =
            KnowledgeHybridRetrievalEvaluator.evaluateAsync retriever dataset 1 gate CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let unjustified =
            evaluate (KnowledgeHybridQualityGate.create 0.000001 0.000001 None) parityRetriever

        Assert.AreEqual(KnowledgeHybridQualityDecision.Failed, KnowledgeHybridRetrievalEvaluation.decision unjustified)
        Assert.IsFalse(KnowledgeHybridRetrievalEvaluation.accepted unjustified)

        let justification =
            "Hybrid retains one operational path while matching both baselines."

        let justified =
            evaluate (KnowledgeHybridQualityGate.create 0.000001 0.000001 (Some justification)) parityRetriever

        Assert.AreEqual(
            KnowledgeHybridQualityDecision.JustifiedParity justification,
            KnowledgeHybridRetrievalEvaluation.decision justified
        )

        Assert.IsTrue(KnowledgeHybridRetrievalEvaluation.accepted justified)

        let duplicateRetriever: KnowledgeRankedRetrievalEvaluator =
            { RetrieveAsync =
                fun _ _ _ _ ->
                    KnowledgeRankedRetrievalOutput.create [ relevant; relevant ] 0m
                    |> Task.FromResult }

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeHybridRetrievalEvaluator.evaluateAsync
                duplicateRetriever
                dataset
                2
                (KnowledgeHybridQualityGate.create 0.000001 0.000001 None)
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeRankedRetrievalOutput.create [ relevant ] -0.01m |> ignore)
        |> ignore
