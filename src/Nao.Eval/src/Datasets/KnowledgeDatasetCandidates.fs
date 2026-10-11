namespace Nao.Eval

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents
open Nao.Knowledge

[<RequireQualifiedAccess>]
type KnowledgeProductionRetrievalFailureKind =
    | NoRelevantResults
    | MissingRelevantResult
    | ProhibitedResultRetrieved
    | RankingRegression
    | GroundedAnswerFailure
    | UserReported

type KnowledgeQueryRedaction =
    private
        { Text: string
          RemovedValueCount: int }

[<RequireQualifiedAccess>]
module KnowledgeQueryRedaction =
    let create text removedValueCount =
        if String.IsNullOrWhiteSpace text || text <> text.Trim() then
            invalidArg (nameof text) "Redacted production queries must be non-blank and trimmed."

        if removedValueCount < 0 then
            invalidArg (nameof removedValueCount) "Redaction removal counts cannot be negative."

        { Text = text
          RemovedValueCount = removedValueCount }

    let text value = value.Text
    let removedValueCount value = value.RemovedValueCount

type KnowledgeQueryRedactorDescriptor =
    private
        { Component: ComponentVersion
          ConfigurationHash: ContentHash }

[<RequireQualifiedAccess>]
module KnowledgeQueryRedactorDescriptor =
    let create evaluatorComponent configurationHash =
        if isNull (box evaluatorComponent) then
            nullArg (nameof evaluatorComponent)

        { Component = evaluatorComponent
          ConfigurationHash = configurationHash }

    let evaluatorComponent value = value.Component
    let configurationHash value = value.ConfigurationHash

type KnowledgeProductionQueryRedactor =
    { Descriptor: KnowledgeQueryRedactorDescriptor
      Redact: string -> KnowledgeQueryRedaction }

type KnowledgeProductionRetrievalFailure =
    private
        { Query: string
          Dimensions: KnowledgeEvaluationDimensions
          ObservedChunkIds: KnowledgeChunkId list
          Kind: KnowledgeProductionRetrievalFailureKind
          CorrelationId: CorrelationId }

[<RequireQualifiedAccess>]
module KnowledgeProductionRetrievalFailure =
    let create query dimensions observedChunkIds kind correlationId =
        if String.IsNullOrWhiteSpace query || query <> query.Trim() then
            invalidArg (nameof query) "Production retrieval failure queries must be non-blank and trimmed."

        if isNull (box dimensions) then
            nullArg (nameof dimensions)

        if isNull (box observedChunkIds) then
            nullArg (nameof observedChunkIds)

        if observedChunkIds |> List.distinct |> List.length <> observedChunkIds.Length then
            invalidArg (nameof observedChunkIds) "Observed production retrieval chunks must be unique."

        { Query = query
          Dimensions = dimensions
          ObservedChunkIds = observedChunkIds
          Kind = kind
          CorrelationId = correlationId }

type KnowledgeDatasetCandidateCapturePolicy =
    private
        { ConsentRecordHash: ContentHash
          RetainUntil: DateTimeOffset }

[<RequireQualifiedAccess>]
module KnowledgeDatasetCandidateCapturePolicy =
    let create consentRecordHash retainUntil =
        { ConsentRecordHash = consentRecordHash
          RetainUntil = retainUntil }

    let consentRecordHash value = value.ConsentRecordHash
    let retainUntil value = value.RetainUntil

type KnowledgeDatasetCandidate =
    private
        { Version: int
          Id: Guid
          Owner: AuthorizationIdentity
          Query: string
          Dimensions: KnowledgeEvaluationDimensions
          ObservedChunkIds: KnowledgeChunkId list
          FailureKind: KnowledgeProductionRetrievalFailureKind
          CorrelationId: CorrelationId
          ConsentRecordHash: ContentHash
          Redactor: KnowledgeQueryRedactorDescriptor
          RemovedValueCount: int
          CapturedAt: DateTimeOffset
          RetainUntil: DateTimeOffset }

[<RequireQualifiedAccess>]
module KnowledgeDatasetCandidate =
    let currentVersion = 1
    let version value = value.Version
    let id value = value.Id
    let owner value = value.Owner
    let query value = value.Query
    let dimensions value = value.Dimensions
    let observedChunkIds value = value.ObservedChunkIds
    let failureKind value = value.FailureKind
    let correlationId value = value.CorrelationId
    let consentRecordHash value = value.ConsentRecordHash
    let redactor value = value.Redactor
    let removedValueCount value = value.RemovedValueCount
    let capturedAt value = value.CapturedAt
    let retainUntil value = value.RetainUntil

    let capture authorization capturedAt policy redactor (failure: KnowledgeProductionRetrievalFailure) =
        if isNull (box authorization) then
            nullArg (nameof authorization)

        if isNull (box policy) then
            nullArg (nameof policy)

        if
            isNull (box redactor)
            || isNull (box redactor.Descriptor)
            || isNull (box redactor.Redact)
        then
            nullArg (nameof redactor)

        if isNull (box failure) then
            nullArg (nameof failure)

        if KnowledgeDatasetCandidateCapturePolicy.retainUntil policy <= capturedAt then
            invalidArg (nameof policy) "Dataset candidate retention must end after capture."

        let redaction = redactor.Redact failure.Query

        if isNull (box redaction) then
            invalidOp "Production query redactor returned a null result."

        { Version = currentVersion
          Id = Guid.NewGuid()
          Owner = AuthorizationScope.identity authorization
          Query = KnowledgeQueryRedaction.text redaction
          Dimensions = failure.Dimensions
          ObservedChunkIds = failure.ObservedChunkIds
          FailureKind = failure.Kind
          CorrelationId = failure.CorrelationId
          ConsentRecordHash = KnowledgeDatasetCandidateCapturePolicy.consentRecordHash policy
          Redactor = redactor.Descriptor
          RemovedValueCount = KnowledgeQueryRedaction.removedValueCount redaction
          CapturedAt = capturedAt
          RetainUntil = KnowledgeDatasetCandidateCapturePolicy.retainUntil policy }

type KnowledgeDatasetCandidateJudgment =
    private
        { CaseId: string
          RelevantDocumentIds: KnowledgeDocumentId list
          RelevantChunkIds: KnowledgeChunkId list
          ExpectedFacts: KnowledgeExpectedFact list
          ProhibitedResults: KnowledgeProhibitedResult list
          Justification: string }

[<RequireQualifiedAccess>]
module KnowledgeDatasetCandidateJudgment =
    let create caseId relevantDocumentIds relevantChunkIds expectedFacts prohibitedResults justification =
        if String.IsNullOrWhiteSpace caseId || caseId <> caseId.Trim() then
            invalidArg (nameof caseId) "Reviewed dataset candidate case identities must be non-blank and trimmed."

        if String.IsNullOrWhiteSpace justification || justification <> justification.Trim() then
            invalidArg (nameof justification) "Dataset candidate review justifications must be non-blank and trimmed."

        { CaseId = caseId
          RelevantDocumentIds = List.ofSeq relevantDocumentIds
          RelevantChunkIds = List.ofSeq relevantChunkIds
          ExpectedFacts = List.ofSeq expectedFacts
          ProhibitedResults = List.ofSeq prohibitedResults
          Justification = justification }

[<RequireQualifiedAccess>]
type KnowledgeDatasetCandidateReviewRequest =
    | Approve of KnowledgeDatasetCandidateJudgment
    | Reject of reason: string

type KnowledgeDatasetCandidateReviewAuthorizer =
    private
        { AuthorizeAsync: AuthorizationScope -> ResourceAccess -> CancellationToken -> Task<PermissionDecisionEvidence> }

[<RequireQualifiedAccess>]
module KnowledgeDatasetCandidateReviewAuthorizer =
    let create authorizeAsync =
        if isNull (box authorizeAsync) then
            nullArg (nameof authorizeAsync)

        { AuthorizeAsync = authorizeAsync }

type KnowledgeApprovedDatasetCandidate =
    private
        { Candidate: KnowledgeDatasetCandidate
          EvaluationCase: KnowledgeEvaluationCase
          Reviewer: AuthorizationIdentity
          Justification: string
          PermissionEvidence: PermissionDecisionEvidence
          ReviewedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module KnowledgeApprovedDatasetCandidate =
    let candidate value = value.Candidate
    let evaluationCase value = value.EvaluationCase
    let reviewer value = value.Reviewer
    let justification value = value.Justification
    let permissionEvidence value = value.PermissionEvidence
    let reviewedAt value = value.ReviewedAt

type KnowledgeRejectedDatasetCandidate =
    private
        { Candidate: KnowledgeDatasetCandidate
          Reviewer: AuthorizationIdentity
          Reason: string
          PermissionEvidence: PermissionDecisionEvidence
          ReviewedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module KnowledgeRejectedDatasetCandidate =
    let candidate value = value.Candidate
    let reviewer value = value.Reviewer
    let reason value = value.Reason
    let permissionEvidence value = value.PermissionEvidence
    let reviewedAt value = value.ReviewedAt

[<RequireQualifiedAccess>]
type KnowledgeDatasetCandidateReview =
    | Approved of KnowledgeApprovedDatasetCandidate
    | Rejected of KnowledgeRejectedDatasetCandidate

[<RequireQualifiedAccess>]
type KnowledgeDatasetCandidateReviewResult =
    | Authorized of KnowledgeDatasetCandidateReview
    | Denied of PermissionDecisionEvidence

[<RequireQualifiedAccess>]
module KnowledgeDatasetCandidateReview =
    let private candidateResource candidate =
        let candidateId = KnowledgeDatasetCandidate.id candidate

        ResourceAccess.administration $"knowledge-dataset-candidate/{candidateId:D}" AdministrationAction.Manage

    let reviewAsync authorizer authorization reviewedAt candidate request (cancellationToken: CancellationToken) =
        task {
            if isNull (box authorizer) then
                nullArg (nameof authorizer)

            if isNull (box authorization) then
                nullArg (nameof authorization)

            if isNull (box candidate) then
                nullArg (nameof candidate)

            cancellationToken.ThrowIfCancellationRequested()
            let access = candidateResource candidate

            let! evidence =
                authorizer.AuthorizeAsync authorization access cancellationToken
                |> _.WaitAsync(cancellationToken)

            if isNull (box evidence) then
                invalidOp "Dataset candidate review authorizer returned null evidence."

            if evidence.Access <> access then
                invalidOp "Dataset candidate review authorizer returned evidence for a different resource."

            if evidence.Decision <> PermissionDecision.Allow then
                return KnowledgeDatasetCandidateReviewResult.Denied evidence
            elif reviewedAt > KnowledgeDatasetCandidate.retainUntil candidate then
                return invalidOp "Expired production retrieval candidates cannot be reviewed."
            else
                let reviewer = AuthorizationScope.identity authorization

                return
                    match request with
                    | KnowledgeDatasetCandidateReviewRequest.Approve judgment ->
                        let evaluationCase =
                            KnowledgeEvaluationCase.create
                                judgment.CaseId
                                (KnowledgeDatasetCandidate.query candidate)
                                (KnowledgeDatasetCandidate.dimensions candidate)
                                judgment.RelevantDocumentIds
                                judgment.RelevantChunkIds
                                judgment.ExpectedFacts
                                judgment.ProhibitedResults

                        KnowledgeDatasetCandidateReviewResult.Authorized(
                            KnowledgeDatasetCandidateReview.Approved
                                { Candidate = candidate
                                  EvaluationCase = evaluationCase
                                  Reviewer = reviewer
                                  Justification = judgment.Justification
                                  PermissionEvidence = evidence
                                  ReviewedAt = reviewedAt }
                        )
                    | KnowledgeDatasetCandidateReviewRequest.Reject reason ->
                        if String.IsNullOrWhiteSpace reason || reason <> reason.Trim() then
                            invalidArg
                                (nameof request)
                                "Dataset candidate rejection reasons must be non-blank and trimmed."

                        KnowledgeDatasetCandidateReviewResult.Authorized(
                            KnowledgeDatasetCandidateReview.Rejected
                                { Candidate = candidate
                                  Reviewer = reviewer
                                  Reason = reason
                                  PermissionEvidence = evidence
                                  ReviewedAt = reviewedAt }
                        )
        }

type KnowledgeDatasetCandidatePromotion =
    private
        { Dataset: KnowledgeEvaluationDataset
          CandidateIds: Guid list
          PromotedBy: AuthorizationIdentity
          PermissionEvidence: PermissionDecisionEvidence
          PromotedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module KnowledgeDatasetCandidatePromotion =
    let dataset value = value.Dataset
    let candidateIds value = value.CandidateIds
    let promotedBy value = value.PromotedBy
    let permissionEvidence value = value.PermissionEvidence
    let promotedAt value = value.PromotedAt

[<RequireQualifiedAccess>]
type KnowledgeDatasetCandidatePromotionResult =
    | Promoted of KnowledgeDatasetCandidatePromotion
    | Denied of PermissionDecisionEvidence

[<RequireQualifiedAccess>]
module KnowledgeDatasetCandidatePromotionWorkflow =
    let promoteAsync
        authorizer
        authorization
        promotedAt
        datasetName
        revision
        existingDataset
        reviews
        (cancellationToken: CancellationToken)
        =
        task {
            if isNull (box authorizer) then
                nullArg (nameof authorizer)

            if isNull (box authorization) then
                nullArg (nameof authorization)

            if String.IsNullOrWhiteSpace datasetName || datasetName <> datasetName.Trim() then
                invalidArg (nameof datasetName) "Promoted dataset names must be non-blank and trimmed."

            if isNull (box reviews) || List.isEmpty reviews then
                invalidArg (nameof reviews) "Dataset promotion requires at least one reviewed candidate."

            cancellationToken.ThrowIfCancellationRequested()

            let access =
                ResourceAccess.administration $"knowledge-evaluation-dataset/{datasetName}" AdministrationAction.Manage

            let! evidence =
                authorizer.AuthorizeAsync authorization access cancellationToken
                |> _.WaitAsync(cancellationToken)

            if isNull (box evidence) then
                invalidOp "Dataset promotion authorizer returned null evidence."

            if evidence.Access <> access then
                invalidOp "Dataset promotion authorizer returned evidence for a different resource."

            if evidence.Decision <> PermissionDecision.Allow then
                return KnowledgeDatasetCandidatePromotionResult.Denied evidence
            else
                let approved =
                    reviews
                    |> List.map (function
                        | KnowledgeDatasetCandidateReview.Approved value -> value
                        | KnowledgeDatasetCandidateReview.Rejected _ ->
                            invalidArg (nameof reviews) "Rejected candidates cannot be promoted.")

                let candidateIds =
                    approved
                    |> List.map (KnowledgeApprovedDatasetCandidate.candidate >> KnowledgeDatasetCandidate.id)

                if candidateIds |> List.distinct |> List.length <> candidateIds.Length then
                    invalidArg (nameof reviews) "Dataset promotion candidates must be unique."

                approved
                |> List.iter (fun review ->
                    let retainUntil =
                        review
                        |> KnowledgeApprovedDatasetCandidate.candidate
                        |> KnowledgeDatasetCandidate.retainUntil

                    if retainUntil < promotedAt then
                        invalidOp "Expired production retrieval candidates cannot be promoted.")

                let existingCases =
                    match existingDataset with
                    | None -> []
                    | Some dataset ->
                        if KnowledgeEvaluationDataset.name dataset <> datasetName then
                            invalidArg (nameof existingDataset) "Existing dataset name must match the promotion target."

                        if revision <= KnowledgeEvaluationDataset.revision dataset then
                            invalidArg (nameof revision) "Promoted dataset revision must advance the existing revision."

                        KnowledgeEvaluationDataset.cases dataset

                let candidateCases =
                    approved |> List.map KnowledgeApprovedDatasetCandidate.evaluationCase

                let dataset =
                    KnowledgeEvaluationDataset.create datasetName revision (existingCases @ candidateCases)

                return
                    KnowledgeDatasetCandidatePromotionResult.Promoted
                        { Dataset = dataset
                          CandidateIds = candidateIds
                          PromotedBy = AuthorizationScope.identity authorization
                          PermissionEvidence = evidence
                          PromotedAt = promotedAt }
        }
