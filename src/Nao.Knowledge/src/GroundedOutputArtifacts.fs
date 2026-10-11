namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

type KnowledgeGroundedCitationProvenance =
    private
        { ReferenceId: KnowledgeGroundedCitationReferenceId
          SourceId: KnowledgeSourceId
          DocumentId: KnowledgeDocumentId
          DocumentVersion: int
          DocumentVersionId: KnowledgeDocumentVersionId
          ChunkId: KnowledgeChunkId
          Locations: SourceLocation list
          QuotedContentHash: ContentHash }

[<RequireQualifiedAccess>]
module KnowledgeGroundedCitationProvenance =
    let referenceId value = value.ReferenceId
    let sourceId value = value.SourceId
    let documentId value = value.DocumentId
    let documentVersion value = value.DocumentVersion
    let documentVersionId value = value.DocumentVersionId
    let chunkId value = value.ChunkId
    let locations value = value.Locations
    let quotedContentHash value = value.QuotedContentHash

type KnowledgeGroundedOutputCitation =
    private
        { Provenance: KnowledgeGroundedCitationProvenance
          QuotedText: string }

[<RequireQualifiedAccess>]
module KnowledgeGroundedOutputCitation =
    let provenance value = value.Provenance
    let quotedText value = value.QuotedText

type KnowledgeGroundedOutputClaim =
    private
        { Claim: KnowledgeGroundedClaim
          Support: KnowledgeClaimSupportAssessment
          Citations: KnowledgeGroundedOutputCitation list }

[<RequireQualifiedAccess>]
module KnowledgeGroundedOutputClaim =
    let claim value = value.Claim
    let support value = value.Support
    let citations value = value.Citations

[<RequireQualifiedAccess>]
type KnowledgeGroundedOutputDisposition =
    | Accepted
    | Warned

type KnowledgeGroundedOutputArtifact =
    private
        { Id: ArtifactId
          Disposition: KnowledgeGroundedOutputDisposition
          Text: string
          Confidence: Confidence
          Claims: KnowledgeGroundedOutputClaim list
          CreatedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module KnowledgeGroundedOutputArtifact =
    let id value = value.Id
    let disposition value = value.Disposition
    let text value = value.Text
    let confidence value = value.Confidence
    let claims value = value.Claims
    let createdAt value = value.CreatedAt

[<RequireQualifiedAccess>]
type KnowledgeGroundedAuditOutcome =
    | Accepted
    | Abstained
    | RepairRequired
    | Warned
    | Rejected

type KnowledgeGroundedClaimAuditEvidence =
    private
        { ClaimId: KnowledgeGroundedClaimId
          ClaimContentHash: ContentHash
          AnswerOffset: int
          AnswerLength: int
          Presentation: KnowledgeGroundedClaimPresentation
          SupportVerdict: KnowledgeClaimSupportVerdict
          SupportConfidence: Confidence
          Citations: KnowledgeGroundedCitationProvenance list }

[<RequireQualifiedAccess>]
module KnowledgeGroundedClaimAuditEvidence =
    let claimId value = value.ClaimId
    let claimContentHash value = value.ClaimContentHash
    let answerOffset value = value.AnswerOffset
    let answerLength value = value.AnswerLength
    let presentation value = value.Presentation
    let supportVerdict value = value.SupportVerdict
    let supportConfidence value = value.SupportConfidence
    let citations value = value.Citations

type KnowledgeGroundedAuditRecord =
    private
        { Version: int
          Id: Guid
          ArtifactId: ArtifactId option
          Outcome: KnowledgeGroundedAuditOutcome
          AnswerContentHash: ContentHash
          Claims: KnowledgeGroundedClaimAuditEvidence list
          Evaluator: KnowledgeClaimSupportEvaluatorDescriptor
          Usage: KnowledgeClaimSupportUsage
          EvaluationDuration: TimeSpan
          RecordedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module KnowledgeGroundedAuditRecord =
    let currentVersion = 1
    let version value = value.Version
    let id value = value.Id
    let artifactId value = value.ArtifactId
    let outcome value = value.Outcome
    let answerContentHash value = value.AnswerContentHash
    let claims value = value.Claims
    let evaluator value = value.Evaluator
    let usage value = value.Usage
    let evaluationDuration value = value.EvaluationDuration
    let recordedAt value = value.RecordedAt

type KnowledgeGroundedOutput =
    private
        { Artifact: KnowledgeGroundedOutputArtifact option
          AuditRecord: KnowledgeGroundedAuditRecord }

[<RequireQualifiedAccess>]
module KnowledgeGroundedOutput =
    let artifact value = value.Artifact
    let auditRecord value = value.AuditRecord

type KnowledgeGroundedAuditRecorder =
    { RecordAsync: AuthorizationScope -> KnowledgeGroundedAuditRecord -> CancellationToken -> Task }

[<RequireQualifiedAccess>]
module KnowledgeGroundedOutputProjector =
    let private citationProvenance citation =
        let contextCitation = KnowledgeVerifiedCitation.contextCitation citation

        { ReferenceId = KnowledgeVerifiedCitation.referenceId citation
          SourceId = KnowledgeContextCitation.sourceId contextCitation
          DocumentId = KnowledgeContextCitation.documentId contextCitation
          DocumentVersion = KnowledgeContextCitation.documentVersion contextCitation
          DocumentVersionId = KnowledgeContextCitation.documentVersionId contextCitation
          ChunkId = KnowledgeContextCitation.chunkId contextCitation
          Locations = KnowledgeContextCitation.locations contextCitation
          QuotedContentHash = KnowledgeContextCitation.quotedContentHash contextCitation }

    let private projectClaims evaluation =
        let assessments =
            evaluation
            |> KnowledgeGroundedClaimSupportEvaluation.assessments
            |> List.map (fun assessment -> KnowledgeClaimSupportAssessment.claimId assessment, assessment)
            |> Map.ofList

        evaluation
        |> KnowledgeGroundedClaimSupportEvaluation.verifiedAnswer
        |> KnowledgeVerifiedGroundedAnswer.claims
        |> List.map (fun verifiedClaim ->
            let claim = KnowledgeVerifiedClaim.claim verifiedClaim
            let claimId = KnowledgeGroundedClaim.id claim
            let support = Map.find claimId assessments

            let citations =
                verifiedClaim
                |> KnowledgeVerifiedClaim.citations
                |> List.map (fun citation ->
                    { Provenance = citationProvenance citation
                      QuotedText = KnowledgeVerifiedCitation.quotedText citation })

            { Claim = claim
              Support = support
              Citations = citations })

    let private policyEvidence policyResult =
        match policyResult with
        | KnowledgeGroundedSupportPolicyResult.Accepted evaluation ->
            evaluation, KnowledgeGroundedAuditOutcome.Accepted, Some KnowledgeGroundedOutputDisposition.Accepted
        | KnowledgeGroundedSupportPolicyResult.Abstained(evaluation, _) ->
            evaluation, KnowledgeGroundedAuditOutcome.Abstained, None
        | KnowledgeGroundedSupportPolicyResult.RepairRequired request ->
            KnowledgeGroundedAnswerRepairRequest.evaluation request, KnowledgeGroundedAuditOutcome.RepairRequired, None
        | KnowledgeGroundedSupportPolicyResult.Warned evaluation ->
            evaluation, KnowledgeGroundedAuditOutcome.Warned, Some KnowledgeGroundedOutputDisposition.Warned
        | KnowledgeGroundedSupportPolicyResult.Rejected evaluation ->
            evaluation, KnowledgeGroundedAuditOutcome.Rejected, None

    let project recordedAt policyResult =
        let evaluation, auditOutcome, outputDisposition = policyEvidence policyResult

        let verifiedAnswer =
            KnowledgeGroundedClaimSupportEvaluation.verifiedAnswer evaluation

        let claims = projectClaims evaluation

        let artifact =
            outputDisposition
            |> Option.map (fun disposition ->
                { Id = ArtifactId.generate ()
                  Disposition = disposition
                  Text = KnowledgeVerifiedGroundedAnswer.text verifiedAnswer
                  Confidence = KnowledgeVerifiedGroundedAnswer.confidence verifiedAnswer
                  Claims = claims
                  CreatedAt = recordedAt })

        let auditClaims =
            claims
            |> List.map (fun outputClaim ->
                let claim = KnowledgeGroundedOutputClaim.claim outputClaim
                let support = KnowledgeGroundedOutputClaim.support outputClaim

                { ClaimId = KnowledgeGroundedClaim.id claim
                  ClaimContentHash = ContentHash.computeText (KnowledgeGroundedClaim.text claim)
                  AnswerOffset = KnowledgeGroundedClaim.answerOffset claim
                  AnswerLength = KnowledgeGroundedClaim.answerLength claim
                  Presentation = KnowledgeGroundedClaim.presentation claim
                  SupportVerdict = KnowledgeClaimSupportAssessment.verdict support
                  SupportConfidence = KnowledgeClaimSupportAssessment.confidence support
                  Citations =
                    outputClaim
                    |> KnowledgeGroundedOutputClaim.citations
                    |> List.map KnowledgeGroundedOutputCitation.provenance })

        let auditRecord =
            { Version = KnowledgeGroundedAuditRecord.currentVersion
              Id = Guid.NewGuid()
              ArtifactId = artifact |> Option.map KnowledgeGroundedOutputArtifact.id
              Outcome = auditOutcome
              AnswerContentHash =
                verifiedAnswer
                |> KnowledgeVerifiedGroundedAnswer.text
                |> ContentHash.computeText
              Claims = auditClaims
              Evaluator = KnowledgeGroundedClaimSupportEvaluation.evaluator evaluation
              Usage = KnowledgeGroundedClaimSupportEvaluation.usage evaluation
              EvaluationDuration = KnowledgeGroundedClaimSupportEvaluation.duration evaluation
              RecordedAt = recordedAt }

        { Artifact = artifact
          AuditRecord = auditRecord }

[<RequireQualifiedAccess>]
module KnowledgeGroundedOutputEmitter =
    let emitAsync
        (recorder: KnowledgeGroundedAuditRecorder)
        (authorization: AuthorizationScope)
        (recordedAt: DateTimeOffset)
        (policyResult: KnowledgeGroundedSupportPolicyResult)
        (cancellationToken: CancellationToken)
        =
        task {
            if isNull (box recorder) || isNull (box recorder.RecordAsync) then
                nullArg (nameof recorder)

            if isNull (box authorization) then
                nullArg (nameof authorization)

            cancellationToken.ThrowIfCancellationRequested()
            let output = KnowledgeGroundedOutputProjector.project recordedAt policyResult
            let auditRecord = KnowledgeGroundedOutput.auditRecord output
            do! recorder.RecordAsync authorization auditRecord cancellationToken
            return output
        }
