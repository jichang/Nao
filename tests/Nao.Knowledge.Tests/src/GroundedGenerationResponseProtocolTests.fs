namespace Nao.Knowledge.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Knowledge
open Nao.Protocols

[<TestClass>]
type GroundedGenerationResponseProtocolTests() =
    let chunkId =
        let documentId = KnowledgeDocumentId.parse "grounded-protocol-document"

        let versionId =
            KnowledgeDocumentVersionId.derive documentId 1 (ContentHash.computeText "version")

        KnowledgeChunkId.derive versionId "test-chunker" 0 (ContentHash.computeText "Evidence.")

    let config = KnowledgeGroundedResponseProtocolConfig.create 4000 10 10 4

    let protocol = KnowledgeGroundedResponseProtocol.create config

    let validAnswer =
        let chunk = KnowledgeChunkId.value chunkId

        $$"""{"status":"answer","answer":"Supported fact.","confidence":0.8,"claims":[{"id":"claim-1","offset":0,"length":15,"kind":"generated-interpretation","confidence":0.9,"citationIds":["citation-1"]}],"citations":[{"id":"citation-1","marker":"[nao-citation:{{chunk}}]","chunkId":"{{chunk}}"}]}"""

    [<TestMethod>]
    member _.``Grounded response protocol parses fully cited answer``() =
        match protocol.Parse validAnswer with
        | Ok [ KnowledgeGroundedGenerationResult.ProposedAnswer proposal ] ->
            let claim = proposal |> KnowledgeGroundedAnswerProposal.claims |> List.exactlyOne

            let citation =
                proposal
                |> KnowledgeGroundedAnswerProposal.citationReferences
                |> List.exactlyOne

            Assert.AreEqual("Supported fact.", KnowledgeGroundedClaim.text claim)

            Assert.AreEqual(
                KnowledgeGroundedClaimPresentation.GeneratedInterpretation,
                KnowledgeGroundedClaim.presentation claim
            )

            Assert.AreEqual(
                [ KnowledgeGroundedCitationReference.id citation ],
                KnowledgeGroundedClaim.citationReferenceIds claim
            )

            Assert.AreEqual(chunkId, KnowledgeGroundedCitationReference.chunkId citation)
        | result -> Assert.Fail $"Unexpected parse result: {result}"

        let instructions = ResponseProtocol.promptInstructions protocol
        StringAssert.Contains(instructions, "Every material claim must have one or more citationIds.")
        StringAssert.Contains(instructions, "generated-interpretation")
        StringAssert.Contains(protocol.BuildRepairMessage(ResponseParseError.create "invalid"), "corrected response")

    [<TestMethod>]
    member _.``Grounded response protocol requires explicit claim presentation``() =
        let chunk = KnowledgeChunkId.value chunkId

        let quotation =
            $$"""{"status":"answer","answer":"Evidence.","confidence":0.8,"claims":[{"id":"claim-1","offset":0,"length":9,"kind":"source-quotation","confidence":0.9,"citationIds":["citation-1"]}],"citations":[{"id":"citation-1","marker":"[nao-citation:{{chunk}}]","chunkId":"{{chunk}}"}]}"""

        match protocol.Parse quotation with
        | Ok [ KnowledgeGroundedGenerationResult.ProposedAnswer proposal ] ->
            let claim = proposal |> KnowledgeGroundedAnswerProposal.claims |> List.exactlyOne

            Assert.AreEqual(
                KnowledgeGroundedClaimPresentation.SourceQuotation,
                KnowledgeGroundedClaim.presentation claim
            )
        | result -> Assert.Fail $"Unexpected quotation parse result: {result}"

        let missingKind =
            $$"""{"status":"answer","answer":"Evidence.","confidence":0.8,"claims":[{"id":"claim-1","offset":0,"length":9,"confidence":0.9,"citationIds":["citation-1"]}],"citations":[{"id":"citation-1","marker":"[nao-citation:{{chunk}}]","chunkId":"{{chunk}}"}]}"""

        match protocol.Parse missingKind with
        | Error error ->
            StringAssert.Contains(
                error.SuggestedFix |> Option.defaultValue "",
                "'source-quotation' or 'generated-interpretation'"
            )
        | Ok value -> Assert.Fail $"Unexpected successful parse: {value}"

    [<TestMethod>]
    member _.``Grounded response protocol parses typed insufficiency``() =
        let response =
            """{"status":"insufficient","reason":"context-insufficient","explanation":"The context is insufficient."}"""

        match protocol.Parse response with
        | Ok [ KnowledgeGroundedGenerationResult.InsufficientEvidence insufficiency ] ->
            Assert.AreEqual(
                KnowledgeGroundedInsufficiencyReason.ContextInsufficient,
                KnowledgeGroundedInsufficiency.reason insufficiency
            )
        | result -> Assert.Fail $"Unexpected parse result: {result}"

    [<TestMethod>]
    member _.``Grounded response protocol requires every claim to cite defined evidence``() =
        let uncited =
            """{"status":"answer","answer":"Unsupported.","confidence":0.2,"claims":[{"id":"claim-1","offset":0,"length":12,"kind":"generated-interpretation","confidence":0.2,"citationIds":[]}],"citations":[]}"""

        match protocol.Parse uncited with
        | Error error -> StringAssert.Contains(error.SuggestedFix |> Option.defaultValue "", "at least one citation")
        | Ok value -> Assert.Fail $"Unexpected successful parse: {value}"

        let undefined =
            """{"status":"answer","answer":"Unsupported.","confidence":0.2,"claims":[{"id":"claim-1","offset":0,"length":12,"kind":"generated-interpretation","confidence":0.2,"citationIds":["missing"]}],"citations":[]}"""

        match protocol.Parse undefined with
        | Error error -> StringAssert.Contains(error.SuggestedFix |> Option.defaultValue "", "undefined citation ID")
        | Ok value -> Assert.Fail $"Unexpected successful parse: {value}"

    [<TestMethod>]
    member _.``Grounded response protocol uses ordinary DTO deserialization``() =
        let response =
            """{"status":"insufficient","status":"insufficient","reason":"model-abstained","explanation":"No answer.","extra":true}"""

        match protocol.Parse response with
        | Ok [ KnowledgeGroundedGenerationResult.InsufficientEvidence insufficiency ] ->
            Assert.AreEqual(
                KnowledgeGroundedInsufficiencyReason.ModelAbstained,
                KnowledgeGroundedInsufficiency.reason insufficiency
            )
        | result -> Assert.Fail $"Unexpected parse result: {result}"

    [<TestMethod>]
    member _.``Grounded response protocol enforces declared bounds``() =
        let bounded =
            KnowledgeGroundedResponseProtocol.create (KnowledgeGroundedResponseProtocolConfig.create 20 1 1 1)

        match bounded.Parse validAnswer with
        | Error error -> StringAssert.Contains(error.Summary, "character limit")
        | Ok value -> Assert.Fail $"Unexpected successful parse: {value}"
