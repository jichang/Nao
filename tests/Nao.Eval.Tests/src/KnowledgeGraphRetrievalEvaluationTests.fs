namespace Nao.Eval.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Eval
open Nao.Knowledge

[<TestClass>]
type KnowledgeGraphRetrievalEvaluationTests() =
    let chunk suffix =
        KnowledgeChunkId.parse ("chunk_" + String.replicate 63 "0" + suffix)

    let case =
        KnowledgeEvaluationCase.create
            "graph"
            "related evidence"
            (KnowledgeEvaluationDimensions.create
                [ KnowledgeSourceId.parse "source" ]
                [ "text/plain" ]
                "en"
                "standard"
                KnowledgeQueryType.FactLookup)
            []
            [ chunk "1"; chunk "2" ]
            []
            []

    let dataset = KnowledgeEvaluationDataset.create "graph-retrieval" 1 [ case ]

    [<TestMethod>]
    member _.GraphExpansionIsEnabledOnlyWhenIncrementalQualityPassesBudgets() =
        let evaluator: KnowledgeGraphRetrievalEvaluator =
            { RetrieveAsync =
                fun useGraph _ _ _ ->
                    Task.FromResult(
                        if useGraph then
                            KnowledgeGraphEvaluationOutput.create [ chunk "1"; chunk "2" ] 0.02M
                        else
                            KnowledgeGraphEvaluationOutput.create [ chunk "1" ] 0.01M
                    ) }

        let evaluation =
            KnowledgeGraphRetrievalEvaluationExecutor.evaluateAsync
                evaluator
                (KnowledgeGraphQualityGate.create 0.4 0.0 (TimeSpan.FromSeconds 1.0) 0.02M)
                2
                dataset
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.IsTrue(KnowledgeGraphRetrievalEvaluation.enabled evaluation)
        Assert.IsTrue(KnowledgeGraphRetrievalEvaluation.recallDelta evaluation >= 0.5)

        let degraded: KnowledgeGraphRetrievalEvaluator =
            { RetrieveAsync =
                fun useGraph _ _ _ ->
                    Task.FromResult(
                        if useGraph then
                            KnowledgeGraphEvaluationOutput.create [] 0.5M
                        else
                            KnowledgeGraphEvaluationOutput.create [ chunk "1" ] 0.01M
                    ) }

        let rejected =
            KnowledgeGraphRetrievalEvaluationExecutor.evaluateAsync
                degraded
                (KnowledgeGraphQualityGate.create 0.0 0.0 (TimeSpan.FromSeconds 1.0) 0.02M)
                2
                dataset
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.IsFalse(KnowledgeGraphRetrievalEvaluation.enabled rejected)

        match KnowledgeGraphRetrievalEvaluation.decision rejected with
        | KnowledgeGraphEnablementDecision.Disabled reasons ->
            Assert.IsTrue(List.contains "recall" reasons)
            Assert.IsTrue(List.contains "cost" reasons)
        | decision -> Assert.Fail $"Expected graph rejection, got {decision}."
