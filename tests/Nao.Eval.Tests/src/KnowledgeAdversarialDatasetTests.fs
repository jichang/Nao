namespace Nao.Eval.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Eval
open Nao.Knowledge

[<TestClass>]
type KnowledgeAdversarialDatasetTests() =
    [<TestMethod>]
    member _.``Built-in knowledge corpus covers every adversarial risk with executable judgments``() =
        let dataset = KnowledgeAdversarialDatasets.adversarialV1
        let corpus = KnowledgeAdversarialDataset.corpus dataset
        let cases = KnowledgeAdversarialDataset.cases dataset

        Assert.AreEqual(
            KnowledgeAdversarialDataset.currentSchemaVersion,
            KnowledgeAdversarialDataset.schemaVersion dataset
        )

        Assert.AreEqual("rag-adversarial", KnowledgeAdversarialDataset.name dataset)
        Assert.AreEqual(1, KnowledgeAdversarialDataset.revision dataset)

        Assert.AreEqual(
            64,
            dataset
            |> KnowledgeAdversarialDataset.contentHash
            |> ContentHash.value
            |> String.length
        )

        Assert.AreEqual(10, corpus.Length)
        Assert.AreEqual(5, cases.Length)

        let risks =
            cases
            |> List.collect (KnowledgeAdversarialCase.risks >> Set.toList)
            |> Set.ofList

        Assert.AreEqual(
            Set.ofList
                [ KnowledgeAdversarialRisk.Poisoning
                  KnowledgeAdversarialRisk.PromptInjection
                  KnowledgeAdversarialRisk.StaleSource
                  KnowledgeAdversarialRisk.Ambiguity
                  KnowledgeAdversarialRisk.AclBoundary ],
            risks
        )

        let promptInjection =
            corpus
            |> List.find (
                KnowledgeAdversarialCorpusChunk.risks
                >> Set.contains KnowledgeAdversarialRisk.PromptInjection
            )

        StringAssert.Contains(KnowledgeAdversarialCorpusChunk.text promptInjection, "Ignore previous instructions")

        let stale =
            corpus
            |> List.find (
                KnowledgeAdversarialCorpusChunk.risks
                >> Set.contains KnowledgeAdversarialRisk.StaleSource
            )

        Assert.AreEqual(KnowledgeAdversarialFreshness.Stale, KnowledgeAdversarialCorpusChunk.freshness stale)

        let unauthorized =
            corpus
            |> List.find (fun chunk ->
                let access = KnowledgeAdversarialCorpusChunk.access chunk
                access = KnowledgeAdversarialAccess.Unauthorized)

        Assert.IsTrue(
            unauthorized
            |> KnowledgeAdversarialCorpusChunk.risks
            |> Set.contains KnowledgeAdversarialRisk.AclBoundary
        )

        let evaluationDataset = KnowledgeAdversarialDataset.evaluationDataset dataset
        Assert.AreEqual(5, evaluationDataset |> KnowledgeEvaluationDataset.cases |> List.length)
        Assert.AreEqual(KnowledgeAdversarialDataset.name dataset, KnowledgeEvaluationDataset.name evaluationDataset)

        Assert.AreEqual(
            KnowledgeAdversarialDataset.revision dataset,
            KnowledgeEvaluationDataset.revision evaluationDataset
        )

    [<TestMethod>]
    member _.``Adversarial hash changes with corpus and scenario judgments``() =
        let original = KnowledgeAdversarialDatasets.adversarialV1
        let corpus = KnowledgeAdversarialDataset.corpus original
        let cases = KnowledgeAdversarialDataset.cases original
        let first = corpus.Head

        let changedChunk =
            KnowledgeAdversarialCorpusChunk.create
                (KnowledgeAdversarialCorpusChunk.sourceId first)
                (KnowledgeAdversarialCorpusChunk.documentId first)
                (KnowledgeAdversarialCorpusChunk.chunkId first)
                (KnowledgeAdversarialCorpusChunk.text first + " Additional governed text.")
                (KnowledgeAdversarialCorpusChunk.trust first)
                (KnowledgeAdversarialCorpusChunk.freshness first)
                (KnowledgeAdversarialCorpusChunk.access first)
                (KnowledgeAdversarialCorpusChunk.risks first)

        let changed =
            KnowledgeAdversarialDataset.create
                (KnowledgeAdversarialDataset.name original)
                (KnowledgeAdversarialDataset.revision original)
                (changedChunk :: corpus.Tail)
                cases

        Assert.AreNotEqual(
            KnowledgeAdversarialDataset.contentHash original,
            KnowledgeAdversarialDataset.contentHash changed
        )

    [<TestMethod>]
    member _.``Adversarial contracts reject unknown evidence duplicate chunks and unrepresented risks``() =
        let original = KnowledgeAdversarialDatasets.adversarialV1
        let corpus = KnowledgeAdversarialDataset.corpus original
        let firstCase = original |> KnowledgeAdversarialDataset.cases |> List.head

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeAdversarialDataset.create "duplicate" 1 [ corpus.Head; corpus.Head ] [ firstCase ]
            |> ignore)
        |> ignore

        let evaluationCase = KnowledgeAdversarialCase.evaluationCase firstCase

        let unrepresented =
            KnowledgeAdversarialCase.create [ KnowledgeAdversarialRisk.AclBoundary ] evaluationCase

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeAdversarialDataset.create "unrepresented" 1 corpus [ unrepresented ]
            |> ignore)
        |> ignore

        let unknownChunk = KnowledgeChunkId.parse ("chunk_" + String.replicate 64 "f")

        let unknownCase =
            let dimensions =
                KnowledgeEvaluationDimensions.create
                    [ KnowledgeSourceId.parse "unknown-source" ]
                    [ "text/plain" ]
                    "en"
                    "standard"
                    KnowledgeQueryType.FactLookup

            KnowledgeEvaluationCase.create "unknown" "query" dimensions [] [ unknownChunk ] [] []
            |> KnowledgeAdversarialCase.create [ KnowledgeAdversarialRisk.Poisoning ]

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeAdversarialDataset.create "unknown" 1 corpus [ unknownCase ] |> ignore)
        |> ignore
