namespace Nao.Knowledge

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
type KnowledgeClaimSupportVerdict =
    | Supported
    | Unsupported

type KnowledgeClaimSupportCitation =
    private
        { ReferenceId: KnowledgeGroundedCitationReferenceId
          QuotedText: string
          ContextCitation: KnowledgeContextCitation }

[<RequireQualifiedAccess>]
module KnowledgeClaimSupportCitation =
    let referenceId value = value.ReferenceId
    let quotedText value = value.QuotedText
    let contextCitation value = value.ContextCitation

type KnowledgeClaimSupportInput =
    private
        { ClaimId: KnowledgeGroundedClaimId
          ClaimText: string
          Presentation: KnowledgeGroundedClaimPresentation
          Citations: KnowledgeClaimSupportCitation list }

[<RequireQualifiedAccess>]
module KnowledgeClaimSupportInput =
    let claimId value = value.ClaimId
    let claimText value = value.ClaimText
    let presentation value = value.Presentation
    let citations value = value.Citations

type KnowledgeClaimSupportRequest =
    private
        { AnswerText: string
          Claims: KnowledgeClaimSupportInput list }

[<RequireQualifiedAccess>]
module KnowledgeClaimSupportRequest =
    let answerText value = value.AnswerText
    let claims value = value.Claims

type KnowledgeClaimSupportAssessment =
    private
        { ClaimId: KnowledgeGroundedClaimId
          Verdict: KnowledgeClaimSupportVerdict
          Confidence: Confidence
          SupportingCitationReferenceIds: KnowledgeGroundedCitationReferenceId list
          Explanation: string }

[<RequireQualifiedAccess>]
module KnowledgeClaimSupportAssessment =
    let create claimId verdict confidence supportingCitationReferenceIds explanation =
        if isNull (box supportingCitationReferenceIds) then
            nullArg (nameof supportingCitationReferenceIds)

        if
            supportingCitationReferenceIds
            |> List.distinct
            |> List.length
            |> (<>) supportingCitationReferenceIds.Length
        then
            invalidArg (nameof supportingCitationReferenceIds) "Supporting citation reference IDs must be unique."

        if
            verdict = KnowledgeClaimSupportVerdict.Supported
            && List.isEmpty supportingCitationReferenceIds
        then
            invalidArg
                (nameof supportingCitationReferenceIds)
                "A supported claim must identify at least one supporting citation."

        if String.IsNullOrWhiteSpace explanation || explanation <> explanation.Trim() then
            invalidArg (nameof explanation) "Claim support explanations must be non-blank and trimmed."

        { ClaimId = claimId
          Verdict = verdict
          Confidence = confidence
          SupportingCitationReferenceIds = supportingCitationReferenceIds
          Explanation = explanation }

    let claimId value = value.ClaimId
    let verdict value = value.Verdict
    let confidence value = value.Confidence
    let supportingCitationReferenceIds value = value.SupportingCitationReferenceIds
    let explanation value = value.Explanation

type KnowledgeClaimSupportUsage =
    private
        { ModelCalls: int
          Tokens: int
          CostUsd: decimal }

[<RequireQualifiedAccess>]
module KnowledgeClaimSupportUsage =
    let create modelCalls tokens costUsd =
        if modelCalls < 0 then
            invalidArg (nameof modelCalls) "Claim support model calls cannot be negative."

        if tokens < 0 then
            invalidArg (nameof tokens) "Claim support tokens cannot be negative."

        if costUsd < 0m then
            invalidArg (nameof costUsd) "Claim support cost cannot be negative."

        { ModelCalls = modelCalls
          Tokens = tokens
          CostUsd = costUsd }

    let modelCalls value = value.ModelCalls
    let tokens value = value.Tokens
    let costUsd value = value.CostUsd

type KnowledgeClaimSupportOutput =
    private
        { Assessments: KnowledgeClaimSupportAssessment list
          Usage: KnowledgeClaimSupportUsage }

[<RequireQualifiedAccess>]
module KnowledgeClaimSupportOutput =
    let create assessments usage =
        if isNull (box assessments) then
            nullArg (nameof assessments)

        if isNull (box usage) then
            nullArg (nameof usage)

        { Assessments = assessments
          Usage = usage }

    let assessments value = value.Assessments
    let usage value = value.Usage

type KnowledgeClaimSupportEvaluatorDescriptor =
    private
        { Component: ComponentVersion
          Model: ComponentVersion option
          ConfigurationHash: ContentHash
          Deterministic: bool }

[<RequireQualifiedAccess>]
module KnowledgeClaimSupportEvaluatorDescriptor =
    let create evaluatorComponent model configurationHash deterministic =
        if isNull (box evaluatorComponent) then
            nullArg (nameof evaluatorComponent)

        { Component = evaluatorComponent
          Model = model
          ConfigurationHash = configurationHash
          Deterministic = deterministic }

    let evaluatorComponent value = value.Component
    let model value = value.Model
    let configurationHash value = value.ConfigurationHash
    let deterministic value = value.Deterministic

type KnowledgeClaimSupportEvaluator =
    { Descriptor: KnowledgeClaimSupportEvaluatorDescriptor
      EvaluateAsync:
          AuthorizationScope -> KnowledgeClaimSupportRequest -> CancellationToken -> Task<KnowledgeClaimSupportOutput> }

type KnowledgeClaimSupportBudget =
    private
        { MaximumDuration: TimeSpan
          MaximumModelCalls: int
          MaximumTokens: int
          MaximumCostUsd: decimal }

[<RequireQualifiedAccess>]
module KnowledgeClaimSupportBudget =
    let create maximumDuration maximumModelCalls maximumTokens maximumCostUsd =
        if maximumDuration <= TimeSpan.Zero then
            invalidArg (nameof maximumDuration) "Claim support duration must be positive."

        if maximumModelCalls < 0 then
            invalidArg (nameof maximumModelCalls) "Claim support model-call budget cannot be negative."

        if maximumTokens < 0 then
            invalidArg (nameof maximumTokens) "Claim support token budget cannot be negative."

        if maximumCostUsd < 0m then
            invalidArg (nameof maximumCostUsd) "Claim support cost budget cannot be negative."

        { MaximumDuration = maximumDuration
          MaximumModelCalls = maximumModelCalls
          MaximumTokens = maximumTokens
          MaximumCostUsd = maximumCostUsd }

    let maximumDuration value = value.MaximumDuration
    let maximumModelCalls value = value.MaximumModelCalls
    let maximumTokens value = value.MaximumTokens
    let maximumCostUsd value = value.MaximumCostUsd

type KnowledgeGroundedClaimSupportEvaluation =
    private
        { VerifiedAnswer: KnowledgeVerifiedGroundedAnswer
          Evaluator: KnowledgeClaimSupportEvaluatorDescriptor
          Assessments: KnowledgeClaimSupportAssessment list
          Usage: KnowledgeClaimSupportUsage
          Duration: TimeSpan }

[<RequireQualifiedAccess>]
module KnowledgeGroundedClaimSupportEvaluation =
    let verifiedAnswer value = value.VerifiedAnswer
    let evaluator value = value.Evaluator
    let assessments value = value.Assessments
    let usage value = value.Usage
    let duration value = value.Duration

    let unsupportedClaims value =
        value.Assessments
        |> List.filter (
            KnowledgeClaimSupportAssessment.verdict
            >> (=) KnowledgeClaimSupportVerdict.Unsupported
        )

[<RequireQualifiedAccess>]
type KnowledgeGroundedClaimSupportResult =
    | Supported of KnowledgeGroundedClaimSupportEvaluation
    | Unsupported of KnowledgeGroundedClaimSupportEvaluation

[<RequireQualifiedAccess>]
module KnowledgeGroundedClaimSupportEvaluator =
    let private createRequest verifiedAnswer =
        let claims =
            verifiedAnswer
            |> KnowledgeVerifiedGroundedAnswer.claims
            |> List.map (fun verifiedClaim ->
                let claim = KnowledgeVerifiedClaim.claim verifiedClaim

                let citations =
                    verifiedClaim
                    |> KnowledgeVerifiedClaim.citations
                    |> List.map (fun citation ->
                        { ReferenceId = KnowledgeVerifiedCitation.referenceId citation
                          QuotedText = KnowledgeVerifiedCitation.quotedText citation
                          ContextCitation = KnowledgeVerifiedCitation.contextCitation citation })

                { ClaimId = KnowledgeGroundedClaim.id claim
                  ClaimText = KnowledgeGroundedClaim.text claim
                  Presentation = KnowledgeGroundedClaim.presentation claim
                  Citations = citations })

        { AnswerText = KnowledgeVerifiedGroundedAnswer.text verifiedAnswer
          Claims = claims }

    let private validateUsage budget usage =
        if KnowledgeClaimSupportUsage.modelCalls usage > KnowledgeClaimSupportBudget.maximumModelCalls budget then
            invalidOp "Claim support evaluator exceeded the model-call budget."

        if KnowledgeClaimSupportUsage.tokens usage > KnowledgeClaimSupportBudget.maximumTokens budget then
            invalidOp "Claim support evaluator exceeded the token budget."

        if KnowledgeClaimSupportUsage.costUsd usage > KnowledgeClaimSupportBudget.maximumCostUsd budget then
            invalidOp "Claim support evaluator exceeded the cost budget."

    let private orderAndValidateAssessments request output =
        let requestedClaims = KnowledgeClaimSupportRequest.claims request

        let requestedClaimIds =
            requestedClaims |> List.map KnowledgeClaimSupportInput.claimId

        let assessments = KnowledgeClaimSupportOutput.assessments output

        let assessmentsByClaimId =
            Dictionary<KnowledgeGroundedClaimId, KnowledgeClaimSupportAssessment>()

        assessments
        |> List.iter (fun assessment ->
            let claimId = KnowledgeClaimSupportAssessment.claimId assessment

            if not (assessmentsByClaimId.TryAdd(claimId, assessment)) then
                invalidOp
                    $"Claim support evaluator returned duplicate assessments for claim '{KnowledgeGroundedClaimId.value claimId}'.")

        if assessmentsByClaimId.Count <> requestedClaimIds.Length then
            invalidOp "Claim support evaluator must return exactly one assessment for every material claim."

        requestedClaims
        |> List.map (fun input ->
            let claimId = KnowledgeClaimSupportInput.claimId input

            match assessmentsByClaimId.TryGetValue claimId with
            | false, _ ->
                invalidOp $"Claim support evaluator omitted material claim '{KnowledgeGroundedClaimId.value claimId}'."
            | true, assessment ->
                let citedIds =
                    input
                    |> KnowledgeClaimSupportInput.citations
                    |> List.map KnowledgeClaimSupportCitation.referenceId
                    |> Set.ofList

                assessment
                |> KnowledgeClaimSupportAssessment.supportingCitationReferenceIds
                |> List.iter (fun referenceId ->
                    if not (citedIds.Contains referenceId) then
                        invalidOp
                            $"Claim support evaluator asserted citation '{KnowledgeGroundedCitationReferenceId.value referenceId}' outside claim '{KnowledgeGroundedClaimId.value claimId}'.")

                assessment)

    let evaluateAsync
        (evaluator: KnowledgeClaimSupportEvaluator)
        (authorization: AuthorizationScope)
        (budget: KnowledgeClaimSupportBudget)
        (verifiedAnswer: KnowledgeVerifiedGroundedAnswer)
        (cancellationToken: CancellationToken)
        =
        task {
            if isNull (box evaluator) || isNull (box evaluator.EvaluateAsync) then
                nullArg (nameof evaluator)

            if isNull (box authorization) then
                nullArg (nameof authorization)

            if isNull (box budget) then
                nullArg (nameof budget)

            if isNull (box verifiedAnswer) then
                nullArg (nameof verifiedAnswer)

            cancellationToken.ThrowIfCancellationRequested()
            let request = createRequest verifiedAnswer
            use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
            timeout.CancelAfter(KnowledgeClaimSupportBudget.maximumDuration budget)
            let stopwatch = Stopwatch.StartNew()
            let! output = evaluator.EvaluateAsync authorization request timeout.Token
            stopwatch.Stop()

            if isNull (box output) then
                invalidOp "Claim support evaluator returned a null output."

            let usage = KnowledgeClaimSupportOutput.usage output
            validateUsage budget usage
            let assessments = orderAndValidateAssessments request output

            let evaluation =
                { VerifiedAnswer = verifiedAnswer
                  Evaluator = evaluator.Descriptor
                  Assessments = assessments
                  Usage = usage
                  Duration = stopwatch.Elapsed }

            if
                assessments
                |> List.exists (
                    KnowledgeClaimSupportAssessment.verdict
                    >> (=) KnowledgeClaimSupportVerdict.Unsupported
                )
            then
                return KnowledgeGroundedClaimSupportResult.Unsupported evaluation
            else
                return KnowledgeGroundedClaimSupportResult.Supported evaluation
        }
