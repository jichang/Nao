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
        KnowledgeRetrievalQualityCase.create id query relevant

    [<TestMethod>]
    member _.``Hybrid evaluation records versioned evidence and improvement over both baselines``() =
        let first = qualityCase "first" "first query" [ chunk "1"; chunk "2" ]

        let second = qualityCase "second" "second query" [ chunk "3" ]

        let dataset =
            KnowledgeRetrievalQualityDataset.create "hybrid-retrieval" "1" [ first; second ]

        let equivalent =
            KnowledgeRetrievalQualityDataset.create "hybrid-retrieval" "1" [ first; second ]

        Assert.AreEqual(
            KnowledgeRetrievalQualityDataset.contentSha256 dataset,
            KnowledgeRetrievalQualityDataset.contentSha256 equivalent
        )

        Assert.AreEqual(64, KnowledgeRetrievalQualityDataset.contentSha256 dataset |> String.length)

        let invocations =
            ConcurrentBag<KnowledgeRetrievalEvaluationStrategy * string * int>()

        let retriever: KnowledgeRankedRetrievalEvaluator =
            { RetrieveAsync =
                fun strategy case cutoff _ ->
                    invocations.Add(strategy, KnowledgeRetrievalQualityCase.id case, cutoff)

                    match KnowledgeRetrievalQualityCase.id case, strategy with
                    | "first", KnowledgeRetrievalEvaluationStrategy.Lexical -> Task.FromResult [ chunk "1"; chunk "8" ]
                    | "first", KnowledgeRetrievalEvaluationStrategy.Vector -> Task.FromResult [ chunk "2"; chunk "8" ]
                    | "first", KnowledgeRetrievalEvaluationStrategy.Hybrid -> Task.FromResult [ chunk "1"; chunk "2" ]
                    | "second", KnowledgeRetrievalEvaluationStrategy.Lexical -> Task.FromResult [ chunk "8"; chunk "3" ]
                    | "second", KnowledgeRetrievalEvaluationStrategy.Vector -> Task.FromResult [ chunk "3"; chunk "8" ]
                    | "second", KnowledgeRetrievalEvaluationStrategy.Hybrid -> Task.FromResult [ chunk "3" ]
                    | value -> invalidOp $"Unexpected evaluation input: {value}." }

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

    [<TestMethod>]
    member _.``Parity requires justification and malformed rankings fail closed``() =
        let relevant = chunk "1"

        let dataset =
            KnowledgeRetrievalQualityDataset.create "parity" "1" [ qualityCase "case" "query" [ relevant ] ]

        let parityRetriever: KnowledgeRankedRetrievalEvaluator =
            { RetrieveAsync = fun _ _ _ _ -> Task.FromResult [ relevant ] }

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
            { RetrieveAsync = fun _ _ _ _ -> Task.FromResult [ relevant; relevant ] }

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
