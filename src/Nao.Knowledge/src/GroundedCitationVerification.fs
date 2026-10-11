namespace Nao.Knowledge

open System
open System.Collections.Generic

[<RequireQualifiedAccess>]
type KnowledgeGroundedVerificationFailure =
    | DuplicateContextChunk of KnowledgeChunkId
    | InvalidContextStatementRange of KnowledgeChunkId
    | ContextMarkerMismatch of KnowledgeChunkId
    | ContextStatementTextMismatch of KnowledgeChunkId
    | ContextStatementHashMismatch of KnowledgeChunkId
    | InvalidSourceLocation of KnowledgeChunkId * SourceLocation
    | UndefinedCitationReference of KnowledgeGroundedClaimId * KnowledgeGroundedCitationReferenceId
    | CitationChunkNotInContext of KnowledgeGroundedCitationReferenceId * KnowledgeChunkId
    | CitationMarkerMismatch of KnowledgeGroundedCitationReferenceId * KnowledgeChunkId
    | SourceQuotationNotFound of KnowledgeGroundedClaimId

type KnowledgeVerifiedCitation =
    private
        { ReferenceId: KnowledgeGroundedCitationReferenceId
          ContextCitation: KnowledgeContextCitation
          QuotedText: string }

[<RequireQualifiedAccess>]
module KnowledgeVerifiedCitation =
    let referenceId value = value.ReferenceId
    let contextCitation value = value.ContextCitation
    let quotedText value = value.QuotedText

type KnowledgeVerifiedClaim =
    private
        { Claim: KnowledgeGroundedClaim
          Citations: KnowledgeVerifiedCitation list }

[<RequireQualifiedAccess>]
module KnowledgeVerifiedClaim =
    let claim value = value.Claim
    let citations value = value.Citations

type KnowledgeVerifiedGroundedAnswer =
    private
        { Proposal: KnowledgeGroundedAnswerProposal
          Context: KnowledgeAssembledContext
          Claims: KnowledgeVerifiedClaim list }

[<RequireQualifiedAccess>]
module KnowledgeVerifiedGroundedAnswer =
    let proposal value = value.Proposal
    let context value = value.Context
    let claims value = value.Claims

    let text value =
        value.Proposal |> KnowledgeGroundedAnswerProposal.text

    let confidence value =
        value.Proposal |> KnowledgeGroundedAnswerProposal.confidence

[<RequireQualifiedAccess>]
type KnowledgeGroundedVerificationResult =
    | VerifiedAnswer of KnowledgeVerifiedGroundedAnswer
    | InsufficientEvidence of KnowledgeGroundedInsufficiency
    | Rejected of KnowledgeGroundedVerificationFailure list

[<RequireQualifiedAccess>]
module KnowledgeGroundedCitationVerifier =
    let private validateContextItem
        (contextText: string)
        (failures: ResizeArray<KnowledgeGroundedVerificationFailure>)
        (item: KnowledgeContextItem)
        =
        let citation = KnowledgeContextItem.citation item
        let chunkId = KnowledgeContextCitation.chunkId citation
        let offset = KnowledgeContextCitation.statementOffset citation
        let length = KnowledgeContextCitation.statementLength citation

        let rangeValid =
            offset >= 0
            && length > 0
            && offset <= contextText.Length
            && length <= contextText.Length - offset

        if not rangeValid then
            failures.Add(KnowledgeGroundedVerificationFailure.InvalidContextStatementRange chunkId)
        else
            let statement = contextText.Substring(offset, length)

            if statement <> KnowledgeContextItem.text item then
                failures.Add(KnowledgeGroundedVerificationFailure.ContextStatementTextMismatch chunkId)

            if
                ContentHash.computeText statement
                <> KnowledgeContextCitation.quotedContentHash citation
            then
                failures.Add(KnowledgeGroundedVerificationFailure.ContextStatementHashMismatch chunkId)

        let marker = KnowledgeContextCitation.marker citation
        let markerOffset = offset - Environment.NewLine.Length - marker.Length

        if
            markerOffset < 0
            || markerOffset > contextText.Length
            || marker.Length > contextText.Length - markerOffset
            || contextText.Substring(markerOffset, marker.Length) <> marker
        then
            failures.Add(KnowledgeGroundedVerificationFailure.ContextMarkerMismatch chunkId)

        KnowledgeContextCitation.locations citation
        |> List.iter (fun location ->
            if
                location.CharacterOffset < 0
                || location.CharacterLength <= 0
                || location.CharacterOffset > length
                || location.CharacterLength > length - location.CharacterOffset
            then
                failures.Add(KnowledgeGroundedVerificationFailure.InvalidSourceLocation(chunkId, location)))

    let verify (context: KnowledgeAssembledContext) (generationResult: KnowledgeGroundedGenerationResult) =
        if isNull (box context) then
            nullArg (nameof context)

        match generationResult with
        | KnowledgeGroundedGenerationResult.InsufficientEvidence insufficiency ->
            KnowledgeGroundedVerificationResult.InsufficientEvidence insufficiency
        | KnowledgeGroundedGenerationResult.ProposedAnswer proposal ->
            let failures = ResizeArray<KnowledgeGroundedVerificationFailure>()
            let contextText = KnowledgeAssembledContext.text context
            let items = KnowledgeAssembledContext.items context

            items
            |> List.groupBy (KnowledgeContextItem.citation >> KnowledgeContextCitation.chunkId)
            |> List.iter (fun (chunkId, grouped) ->
                if grouped.Length > 1 then
                    failures.Add(KnowledgeGroundedVerificationFailure.DuplicateContextChunk chunkId))

            items |> List.iter (validateContextItem contextText failures)

            let contextByChunk =
                items
                |> List.map (fun item ->
                    let citation = KnowledgeContextItem.citation item
                    KnowledgeContextCitation.chunkId citation, citation)
                |> Map.ofList

            let referencesById =
                proposal
                |> KnowledgeGroundedAnswerProposal.citationReferences
                |> List.map (fun reference -> KnowledgeGroundedCitationReference.id reference, reference)
                |> Map.ofList

            let resolvedClaims =
                proposal
                |> KnowledgeGroundedAnswerProposal.claims
                |> List.map (fun claim ->
                    let citations =
                        claim
                        |> KnowledgeGroundedClaim.citationReferenceIds
                        |> List.choose (fun referenceId ->
                            match Map.tryFind referenceId referencesById with
                            | None ->
                                failures.Add(
                                    KnowledgeGroundedVerificationFailure.UndefinedCitationReference(
                                        KnowledgeGroundedClaim.id claim,
                                        referenceId
                                    )
                                )

                                None
                            | Some reference ->
                                let chunkId = KnowledgeGroundedCitationReference.chunkId reference

                                match Map.tryFind chunkId contextByChunk with
                                | None ->
                                    failures.Add(
                                        KnowledgeGroundedVerificationFailure.CitationChunkNotInContext(
                                            referenceId,
                                            chunkId
                                        )
                                    )

                                    None
                                | Some contextCitation ->
                                    if
                                        KnowledgeGroundedCitationReference.marker reference
                                        <> KnowledgeContextCitation.marker contextCitation
                                    then
                                        failures.Add(
                                            KnowledgeGroundedVerificationFailure.CitationMarkerMismatch(
                                                referenceId,
                                                chunkId
                                            )
                                        )

                                        None
                                    else
                                        let offset = KnowledgeContextCitation.statementOffset contextCitation
                                        let length = KnowledgeContextCitation.statementLength contextCitation

                                        let quotedText =
                                            if
                                                offset >= 0
                                                && length > 0
                                                && offset <= contextText.Length
                                                && length <= contextText.Length - offset
                                            then
                                                contextText.Substring(offset, length)
                                            else
                                                ""

                                        Some
                                            { ReferenceId = referenceId
                                              ContextCitation = contextCitation
                                              QuotedText = quotedText })

                    { Claim = claim; Citations = citations })

            resolvedClaims
            |> List.iter (fun verifiedClaim ->
                let claim = KnowledgeVerifiedClaim.claim verifiedClaim

                if
                    KnowledgeGroundedClaim.presentation claim = KnowledgeGroundedClaimPresentation.SourceQuotation
                    && not (
                        verifiedClaim
                        |> KnowledgeVerifiedClaim.citations
                        |> List.exists (fun citation ->
                            (KnowledgeVerifiedCitation.quotedText citation)
                                .Contains(KnowledgeGroundedClaim.text claim, StringComparison.Ordinal))
                    )
                then
                    failures.Add(
                        KnowledgeGroundedVerificationFailure.SourceQuotationNotFound(KnowledgeGroundedClaim.id claim)
                    ))

            if failures.Count > 0 then
                failures
                |> Seq.distinct
                |> List.ofSeq
                |> KnowledgeGroundedVerificationResult.Rejected
            else
                KnowledgeGroundedVerificationResult.VerifiedAnswer
                    { Proposal = proposal
                      Context = context
                      Claims = resolvedClaims }
