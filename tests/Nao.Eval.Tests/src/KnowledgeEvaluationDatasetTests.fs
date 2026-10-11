namespace Nao.Eval.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Eval
open Nao.Knowledge

[<TestClass>]
type KnowledgeEvaluationDatasetTests() =
    let chunk suffix =
        KnowledgeChunkId.parse ("chunk_" + String.replicate 63 "0" + suffix)

    let relevantDocument = KnowledgeDocumentId.parse "relevant-document"
    let prohibitedDocument = KnowledgeDocumentId.parse "prohibited-document"
    let relevantChunk = chunk "1"
    let secondRelevantChunk = chunk "2"
    let prohibitedChunk = chunk "9"

    let dimensions =
        KnowledgeEvaluationDimensions.create
            [ KnowledgeSourceId.parse "test-source" ]
            [ "text/plain" ]
            "en"
            "standard"
            KnowledgeQueryType.FactLookup

    let case expectedFactText prohibitedReason =
        let fact =
            KnowledgeExpectedFact.create "fact-1" expectedFactText [ relevantChunk; secondRelevantChunk ]

        let prohibited =
            [ KnowledgeProhibitedResult.create (KnowledgeProhibitedResultTarget.Chunk prohibitedChunk) prohibitedReason
              KnowledgeProhibitedResult.create
                  (KnowledgeProhibitedResultTarget.Document prohibitedDocument)
                  "The document belongs to an excluded source." ]

        KnowledgeEvaluationCase.create
            "case-1"
            "What is the supported fact?"
            dimensions
            [ relevantDocument ]
            [ relevantChunk; secondRelevantChunk ]
            [ fact ]
            prohibited

    [<TestMethod>]
    member _.``Knowledge dataset versions complete judgments and round trips JSON``() =
        let dataset =
            KnowledgeEvaluationDataset.create "grounded-quality" 1 [ case "The supported fact." "The chunk is stale." ]

        let json = KnowledgeEvaluationDatasetJson.serialize dataset
        let restored = KnowledgeEvaluationDatasetJson.deserialize json

        Assert.AreEqual(
            KnowledgeEvaluationDataset.currentSchemaVersion,
            KnowledgeEvaluationDataset.schemaVersion restored
        )

        Assert.AreEqual("grounded-quality", KnowledgeEvaluationDataset.name restored)
        Assert.AreEqual(1, KnowledgeEvaluationDataset.revision restored)
        Assert.AreEqual(KnowledgeEvaluationDataset.contentHash dataset, KnowledgeEvaluationDataset.contentHash restored)

        let restoredCase = restored |> KnowledgeEvaluationDataset.cases |> List.exactlyOne
        let restoredDimensions = KnowledgeEvaluationCase.dimensions restoredCase

        Assert.AreEqual(
            Set.ofList [ KnowledgeSourceId.parse "test-source" ],
            KnowledgeEvaluationDimensions.sourceIds restoredDimensions
        )

        Assert.AreEqual(Set.ofList [ "text/plain" ], KnowledgeEvaluationDimensions.formats restoredDimensions)
        Assert.AreEqual("en", KnowledgeEvaluationDimensions.languageTag restoredDimensions)
        Assert.AreEqual("standard", KnowledgeEvaluationDimensions.tenantPolicy restoredDimensions)
        Assert.AreEqual(KnowledgeQueryType.FactLookup, KnowledgeEvaluationDimensions.queryType restoredDimensions)

        Assert.AreEqual(Set.ofList [ relevantDocument ], KnowledgeEvaluationCase.relevantDocumentIds restoredCase)

        Assert.AreEqual(
            Set.ofList [ relevantChunk; secondRelevantChunk ],
            KnowledgeEvaluationCase.relevantChunkIds restoredCase
        )

        let fact = restoredCase |> KnowledgeEvaluationCase.expectedFacts |> List.exactlyOne

        Assert.AreEqual("The supported fact.", KnowledgeExpectedFact.text fact)

        Assert.AreEqual(
            Set.ofList [ relevantChunk; secondRelevantChunk ],
            KnowledgeExpectedFact.supportingChunkIds fact
        )

        Assert.AreEqual(2, KnowledgeEvaluationCase.prohibitedResults restoredCase |> List.length)

    [<TestMethod>]
    member _.``Knowledge dataset hash changes with revision facts and prohibited results``() =
        let create revision fact reason =
            KnowledgeEvaluationDataset.create "grounded-quality" revision [ case fact reason ]
            |> KnowledgeEvaluationDataset.contentHash

        let original = create 1 "The supported fact." "The chunk is stale."
        Assert.AreNotEqual(original, create 2 "The supported fact." "The chunk is stale.")
        Assert.AreNotEqual(original, create 1 "A changed fact." "The chunk is stale.")
        Assert.AreNotEqual(original, create 1 "The supported fact." "The chunk is unauthorized.")

    [<TestMethod>]
    member _.``Knowledge dataset rejects contradictory and tampered judgments``() =
        let unknownChunk = chunk "8"

        let fact = KnowledgeExpectedFact.create "fact" "Expected." [ unknownChunk ]

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeEvaluationCase.create "case" "query" dimensions [ relevantDocument ] [ relevantChunk ] [ fact ] []
            |> ignore)
        |> ignore

        let prohibitedRelevant =
            KnowledgeProhibitedResult.create
                (KnowledgeProhibitedResultTarget.Chunk relevantChunk)
                "Contradictory judgment."

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeEvaluationCase.create
                "case"
                "query"
                dimensions
                [ relevantDocument ]
                [ relevantChunk ]
                []
                [ prohibitedRelevant ]
            |> ignore)
        |> ignore

        let dataset =
            KnowledgeEvaluationDataset.create "grounded-quality" 1 [ case "The supported fact." "The chunk is stale." ]

        let tampered =
            KnowledgeEvaluationDatasetJson.serialize dataset
            |> _.Replace("The supported fact.", "A fabricated fact.", StringComparison.Ordinal)

        Assert.ThrowsExactly<ArgumentException>(fun () -> KnowledgeEvaluationDatasetJson.deserialize tampered |> ignore)
        |> ignore
