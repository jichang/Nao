namespace Nao.Knowledge

open System

[<Struct>]
type KnowledgeGroundedClaimId = private KnowledgeGroundedClaimId of string

[<RequireQualifiedAccess>]
module KnowledgeGroundedClaimId =
    let tryParse (value: string) =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            None
        else
            Some(KnowledgeGroundedClaimId value)

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () ->
            invalidArg (nameof value) "Grounded claim IDs must be non-blank and have no surrounding whitespace.")

    let value (KnowledgeGroundedClaimId value) = value

[<Struct>]
type KnowledgeGroundedCitationReferenceId = private KnowledgeGroundedCitationReferenceId of string

[<RequireQualifiedAccess>]
module KnowledgeGroundedCitationReferenceId =
    let tryParse (value: string) =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            None
        else
            Some(KnowledgeGroundedCitationReferenceId value)

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () ->
            invalidArg
                (nameof value)
                "Grounded citation reference IDs must be non-blank and have no surrounding whitespace.")

    let value (KnowledgeGroundedCitationReferenceId value) = value

type KnowledgeGroundedCitationReference =
    private
        { Id: KnowledgeGroundedCitationReferenceId
          Marker: string
          ChunkId: KnowledgeChunkId }

[<RequireQualifiedAccess>]
module KnowledgeGroundedCitationReference =
    let create id marker chunkId =
        let expectedMarker = $"[nao-citation:{KnowledgeChunkId.value chunkId}]"

        if marker <> expectedMarker then
            invalidArg (nameof marker) "Citation reference marker must exactly match its chunk identity."

        { Id = id
          Marker = marker
          ChunkId = chunkId }

    let id value = value.Id
    let marker value = value.Marker
    let chunkId value = value.ChunkId

[<RequireQualifiedAccess>]
type KnowledgeGroundedClaimPresentation =
    | SourceQuotation
    | GeneratedInterpretation

type KnowledgeGroundedClaim =
    private
        { Id: KnowledgeGroundedClaimId
          Text: string
          AnswerOffset: int
          AnswerLength: int
          Presentation: KnowledgeGroundedClaimPresentation
          CitationReferenceIds: KnowledgeGroundedCitationReferenceId list
          Confidence: Confidence }

[<RequireQualifiedAccess>]
module KnowledgeGroundedClaim =
    let create (answerText: string) id answerOffset answerLength presentation citationReferenceIds confidence =
        if isNull answerText then
            nullArg (nameof answerText)

        if
            answerOffset < 0
            || answerLength <= 0
            || answerOffset + answerLength > answerText.Length
        then
            invalidArg (nameof answerLength) "Grounded claim range must be non-empty and fit the answer text."

        let text = answerText.Substring(answerOffset, answerLength)

        if String.IsNullOrWhiteSpace text then
            invalidArg (nameof answerLength) "Grounded claims cannot contain only whitespace."

        if isNull (box citationReferenceIds) then
            nullArg (nameof citationReferenceIds)

        if
            citationReferenceIds |> List.distinct |> List.length
            <> citationReferenceIds.Length
        then
            invalidArg (nameof citationReferenceIds) "Grounded claim citation references must be unique."

        { Id = id
          Text = text
          AnswerOffset = answerOffset
          AnswerLength = answerLength
          Presentation = presentation
          CitationReferenceIds = citationReferenceIds
          Confidence = confidence }

    let id value = value.Id
    let text value = value.Text
    let answerOffset value = value.AnswerOffset
    let answerLength value = value.AnswerLength
    let presentation value = value.Presentation
    let citationReferenceIds value = value.CitationReferenceIds
    let confidence value = value.Confidence

type KnowledgeGroundedAnswerProposal =
    private
        { Text: string
          Claims: KnowledgeGroundedClaim list
          CitationReferences: KnowledgeGroundedCitationReference list
          Confidence: Confidence }

[<RequireQualifiedAccess>]
module KnowledgeGroundedAnswerProposal =
    let create text claims citationReferences confidence =
        if String.IsNullOrWhiteSpace text || text <> text.Trim() then
            invalidArg (nameof text) "Grounded answer text must be non-blank and have no surrounding whitespace."

        if isNull (box claims) || List.isEmpty claims then
            invalidArg (nameof claims) "Grounded answer proposals require at least one material claim."

        if isNull (box citationReferences) then
            nullArg (nameof citationReferences)

        let claimIds = claims |> List.map KnowledgeGroundedClaim.id

        if claimIds |> List.distinct |> List.length <> claimIds.Length then
            invalidArg (nameof claims) "Grounded answer claim IDs must be unique."

        claims
        |> List.iter (fun claim ->
            let offset = KnowledgeGroundedClaim.answerOffset claim
            let length = KnowledgeGroundedClaim.answerLength claim

            if
                offset + length > text.Length
                || text.Substring(offset, length) <> KnowledgeGroundedClaim.text claim
            then
                invalidArg (nameof claims) "Grounded claim text must exactly match its answer range.")

        claims
        |> List.pairwise
        |> List.iter (fun (previous, current) ->
            let previousEnd =
                KnowledgeGroundedClaim.answerOffset previous
                + KnowledgeGroundedClaim.answerLength previous

            if KnowledgeGroundedClaim.answerOffset current < previousEnd then
                invalidArg (nameof claims) "Grounded claims must be ordered and non-overlapping.")

        let citationIds =
            citationReferences |> List.map KnowledgeGroundedCitationReference.id

        if citationIds |> List.distinct |> List.length <> citationIds.Length then
            invalidArg (nameof citationReferences) "Grounded citation reference IDs must be unique."

        let assertedIds =
            claims |> List.collect KnowledgeGroundedClaim.citationReferenceIds |> Set.ofList

        citationIds
        |> List.iter (fun id ->
            if not (assertedIds.Contains id) then
                invalidArg
                    (nameof citationReferences)
                    "Grounded citation references must be asserted by at least one material claim.")

        { Text = text
          Claims = claims
          CitationReferences = citationReferences
          Confidence = confidence }

    let text value = value.Text
    let claims value = value.Claims
    let citationReferences value = value.CitationReferences
    let confidence value = value.Confidence

[<RequireQualifiedAccess>]
type KnowledgeGroundedInsufficiencyReason =
    | ContextInsufficient
    | NoMaterialClaims
    | ModelAbstained
    | PolicyRejected

type KnowledgeGroundedInsufficiency =
    private
        { Reason: KnowledgeGroundedInsufficiencyReason
          Explanation: string }

[<RequireQualifiedAccess>]
module KnowledgeGroundedInsufficiency =
    let create reason explanation =
        if String.IsNullOrWhiteSpace explanation || explanation <> explanation.Trim() then
            invalidArg (nameof explanation) "Grounded insufficiency explanations must be non-blank and trimmed."

        { Reason = reason
          Explanation = explanation }

    let reason value = value.Reason
    let explanation value = value.Explanation

[<RequireQualifiedAccess>]
type KnowledgeGroundedGenerationResult =
    | ProposedAnswer of KnowledgeGroundedAnswerProposal
    | InsufficientEvidence of KnowledgeGroundedInsufficiency
