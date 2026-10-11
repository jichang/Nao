namespace Nao.Knowledge.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Knowledge

[<TestClass>]
type GroundedAnswerContractTests() =
    let chunkId =
        let documentId = KnowledgeDocumentId.parse "grounded-answer-document"

        let versionId =
            KnowledgeDocumentVersionId.derive documentId 1 (ContentHash.computeText "version")

        KnowledgeChunkId.derive versionId "test-chunker" 0 (ContentHash.computeText "evidence")

    let citationId = KnowledgeGroundedCitationReferenceId.parse "citation-1"

    let citation =
        KnowledgeGroundedCitationReference.create citationId $"[nao-citation:{KnowledgeChunkId.value chunkId}]" chunkId

    [<TestMethod>]
    member _.``Grounded proposal separates text claims citations and confidence``() =
        let firstText = "Nao uses cited evidence."
        let secondText = "A second material claim."
        let answerText = firstText + " " + secondText

        let firstClaim =
            KnowledgeGroundedClaim.create
                answerText
                (KnowledgeGroundedClaimId.parse "claim-1")
                0
                firstText.Length
                KnowledgeGroundedClaimPresentation.GeneratedInterpretation
                [ citationId ]
                (Confidence.create 0.9)

        let secondClaim =
            KnowledgeGroundedClaim.create
                answerText
                (KnowledgeGroundedClaimId.parse "claim-2")
                (firstText.Length + 1)
                secondText.Length
                KnowledgeGroundedClaimPresentation.GeneratedInterpretation
                []
                (Confidence.create 0.7)

        let proposal =
            KnowledgeGroundedAnswerProposal.create
                answerText
                [ firstClaim; secondClaim ]
                [ citation ]
                (Confidence.create 0.8)

        Assert.AreEqual(answerText, KnowledgeGroundedAnswerProposal.text proposal)
        Assert.AreEqual(2, KnowledgeGroundedAnswerProposal.claims proposal |> List.length)
        Assert.AreEqual([ citationId ], KnowledgeGroundedClaim.citationReferenceIds firstClaim)
        Assert.AreEqual([], KnowledgeGroundedClaim.citationReferenceIds secondClaim)
        Assert.AreEqual(0.8, proposal |> KnowledgeGroundedAnswerProposal.confidence |> Confidence.value)

    [<TestMethod>]
    member _.``Grounded proposal remains explicitly unverified``() =
        let answerText = "An asserted claim."
        let unresolvedId = KnowledgeGroundedCitationReferenceId.parse "missing-citation"

        let claim =
            KnowledgeGroundedClaim.create
                answerText
                (KnowledgeGroundedClaimId.parse "claim")
                0
                answerText.Length
                KnowledgeGroundedClaimPresentation.GeneratedInterpretation
                [ unresolvedId ]
                (Confidence.create 0.5)

        let proposal =
            KnowledgeGroundedAnswerProposal.create answerText [ claim ] [] (Confidence.create 0.5)

        match KnowledgeGroundedGenerationResult.ProposedAnswer proposal with
        | KnowledgeGroundedGenerationResult.ProposedAnswer value ->
            Assert.AreEqual(
                [ unresolvedId ],
                value
                |> KnowledgeGroundedAnswerProposal.claims
                |> List.head
                |> KnowledgeGroundedClaim.citationReferenceIds
            )
        | KnowledgeGroundedGenerationResult.InsufficientEvidence _ ->
            Assert.Fail "Expected an explicitly unverified answer proposal."

    [<TestMethod>]
    member _.``Grounded contracts reject spoofed markers and ambiguous claim ranges``() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeGroundedCitationReference.create citationId "[nao-citation:spoofed]" chunkId
            |> ignore)
        |> ignore

        let answerText = "first second"

        let first =
            KnowledgeGroundedClaim.create
                answerText
                (KnowledgeGroundedClaimId.parse "first")
                0
                5
                KnowledgeGroundedClaimPresentation.GeneratedInterpretation
                [ citationId ]
                (Confidence.create 0.8)

        let overlapping =
            KnowledgeGroundedClaim.create
                answerText
                (KnowledgeGroundedClaimId.parse "second")
                3
                8
                KnowledgeGroundedClaimPresentation.GeneratedInterpretation
                []
                (Confidence.create 0.7)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeGroundedAnswerProposal.create
                answerText
                [ first; overlapping ]
                [ citation ]
                (Confidence.create 0.75)
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Insufficient evidence is a successful typed result``() =
        let insufficiency =
            KnowledgeGroundedInsufficiency.create
                KnowledgeGroundedInsufficiencyReason.ContextInsufficient
                "The assembled context does not contain enough evidence."

        match KnowledgeGroundedGenerationResult.InsufficientEvidence insufficiency with
        | KnowledgeGroundedGenerationResult.InsufficientEvidence value ->
            Assert.AreEqual(
                KnowledgeGroundedInsufficiencyReason.ContextInsufficient,
                KnowledgeGroundedInsufficiency.reason value
            )

            Assert.AreEqual(
                "The assembled context does not contain enough evidence.",
                KnowledgeGroundedInsufficiency.explanation value
            )
        | KnowledgeGroundedGenerationResult.ProposedAnswer _ -> Assert.Fail "Expected typed insufficient evidence."
