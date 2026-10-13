namespace Nao.Eval.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Eval
open Nao.Knowledge

[<TestClass>]
type KnowledgeRetrievalReleaseQualificationTests() =
    let adversarial = KnowledgeAdversarialDatasets.adversarialV1
    let dataset = KnowledgeAdversarialDataset.evaluationDataset adversarial

    let adapter =
        KnowledgeRetrievalAdapterDescriptor.create
            (ComponentVersion.create "deterministic-retrieval-reference" "1")
            (ContentHash.computeText "deterministic-retrieval-reference")

    let prohibitedChunkIds case =
        let chunksByDocument =
            adversarial
            |> KnowledgeAdversarialDataset.corpus
            |> List.groupBy KnowledgeAdversarialCorpusChunk.documentId
            |> Map.ofList

        case
        |> KnowledgeEvaluationCase.prohibitedResults
        |> List.collect (fun prohibited ->
            match KnowledgeProhibitedResult.target prohibited with
            | KnowledgeProhibitedResultTarget.Chunk chunkId -> [ chunkId ]
            | KnowledgeProhibitedResultTarget.Document documentId ->
                chunksByDocument
                |> Map.find documentId
                |> List.map KnowledgeAdversarialCorpusChunk.chunkId)

    let evaluate actualDataset hybridOverride cost =
        let retriever: KnowledgeRankedRetrievalEvaluator =
            { RetrieveAsync =
                fun strategy case _ _ ->
                    let relevant = case |> KnowledgeEvaluationCase.relevantChunkIds |> Set.toList

                    let retrieved =
                        match strategy, hybridOverride case with
                        | KnowledgeRetrievalEvaluationStrategy.Hybrid, Some overrideChunks -> overrideChunks
                        | _ -> relevant

                    KnowledgeRankedRetrievalOutput.create retrieved cost |> Task.FromResult }

        KnowledgeHybridRetrievalEvaluator.evaluateAsync
            retriever
            actualDataset
            3
            (KnowledgeHybridQualityGate.create 0.000001 0.000001 (Some "Equivalent deterministic strategies."))
            CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Adversarial retrieval release profile passes all five gate dimensions``() =
        let evaluation = evaluate dataset (fun _ -> None) 0.001m

        match
            KnowledgeRetrievalReleaseQualification.qualify
                KnowledgeRetrievalReleaseProfile.adversarialV1
                adapter
                DateTimeOffset.UtcNow
                evaluation
        with
        | KnowledgeRetrievalReleaseQualificationResult.Qualified evidence ->
            Assert.AreEqual(adapter, KnowledgeRetrievalReleaseEvidence.adapter evidence)

            Assert.IsTrue(
                evidence
                |> KnowledgeRetrievalReleaseEvidence.gateResult
                |> KnowledgeQualityGateResult.passed
            )
        | result -> Assert.Fail $"Expected retrieval release qualification, received: {result}"

    [<TestMethod>]
    member _.``Retrieval release rejects prohibited and unauthorized chunks``() =
        let aclCase =
            adversarial
            |> KnowledgeAdversarialDataset.cases
            |> List.find (fun adversarialCase ->
                adversarialCase
                |> KnowledgeAdversarialCase.risks
                |> Set.contains KnowledgeAdversarialRisk.AclBoundary)
            |> KnowledgeAdversarialCase.evaluationCase

        let unauthorized = aclCase |> prohibitedChunkIds |> List.exactlyOne

        let evaluation =
            evaluate
                dataset
                (fun case ->
                    if KnowledgeEvaluationCase.id case = KnowledgeEvaluationCase.id aclCase then
                        Some(
                            case
                            |> KnowledgeEvaluationCase.relevantChunkIds
                            |> Set.toList
                            |> fun relevant -> relevant @ [ unauthorized ]
                        )
                    else
                        None)
                0.001m

        let failures =
            KnowledgeRetrievalReleaseQualification.qualify
                KnowledgeRetrievalReleaseProfile.adversarialV1
                adapter
                DateTimeOffset.UtcNow
                evaluation
            |> KnowledgeRetrievalReleaseQualificationResult.failures

        Assert.IsTrue(
            failures
            |> List.exists (function
                | KnowledgeRetrievalReleaseFailure.SafetyViolation violation ->
                    KnowledgeRetrievalReleaseViolation.chunkId violation = unauthorized
                | _ -> false)
        )

        Assert.IsTrue(
            failures
            |> List.exists (function
                | KnowledgeRetrievalReleaseFailure.IsolationViolation violation ->
                    KnowledgeRetrievalReleaseViolation.chunkId violation = unauthorized
                | _ -> false)
        )

    [<TestMethod>]
    member _.``Retrieval release rejects excessive cost and dataset mismatch``() =
        let expensive = evaluate dataset (fun _ -> None) 0.02m

        let expensiveFailures =
            KnowledgeRetrievalReleaseQualification.qualify
                KnowledgeRetrievalReleaseProfile.adversarialV1
                adapter
                DateTimeOffset.UtcNow
                expensive
            |> KnowledgeRetrievalReleaseQualificationResult.failures

        CollectionAssert.Contains(expensiveFailures |> List.toArray, KnowledgeRetrievalReleaseFailure.QualityGateFailed)

        let mismatched =
            KnowledgeEvaluationDataset.create
                "different-adversarial-dataset"
                1
                (KnowledgeEvaluationDataset.cases dataset)

        let mismatchedFailures =
            evaluate mismatched (fun _ -> None) 0m
            |> KnowledgeRetrievalReleaseQualification.qualify
                KnowledgeRetrievalReleaseProfile.adversarialV1
                adapter
                DateTimeOffset.UtcNow
            |> KnowledgeRetrievalReleaseQualificationResult.failures

        CollectionAssert.Contains(mismatchedFailures |> List.toArray, KnowledgeRetrievalReleaseFailure.DatasetMismatch)

        let slowRetriever: KnowledgeRankedRetrievalEvaluator =
            { RetrieveAsync =
                fun strategy case _ _ ->
                    task {
                        if strategy = KnowledgeRetrievalEvaluationStrategy.Hybrid then
                            do! Task.Delay 550

                        return
                            KnowledgeRankedRetrievalOutput.create
                                (case |> KnowledgeEvaluationCase.relevantChunkIds |> Set.toList)
                                0m
                    } }

        let slowEvaluation =
            KnowledgeHybridRetrievalEvaluator.evaluateAsync
                slowRetriever
                dataset
                3
                (KnowledgeHybridQualityGate.create 0.000001 0.000001 (Some "Equivalent deterministic strategies."))
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let slowFailures =
            KnowledgeRetrievalReleaseQualification.qualify
                KnowledgeRetrievalReleaseProfile.adversarialV1
                adapter
                DateTimeOffset.UtcNow
                slowEvaluation
            |> KnowledgeRetrievalReleaseQualificationResult.failures

        CollectionAssert.Contains(slowFailures |> List.toArray, KnowledgeRetrievalReleaseFailure.QualityGateFailed)
