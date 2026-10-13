namespace Nao.Eval

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents
open Nao.Knowledge

type KnowledgeGroundedProductionCase =
    private
        { EvaluationCase: KnowledgeEvaluationCase
          Authorization: AuthorizationScope
          Correlation: CorrelationContext
          Context: KnowledgeAssembledContext }

[<RequireQualifiedAccess>]
module KnowledgeGroundedProductionCase =
    let create evaluationCase authorization correlation context =
        if isNull (box evaluationCase) then
            nullArg (nameof evaluationCase)

        if isNull (box authorization) then
            nullArg (nameof authorization)

        if isNull (box correlation) then
            nullArg (nameof correlation)

        if isNull (box context) then
            nullArg (nameof context)

        let contextAuthorization =
            context
            |> KnowledgeAssembledContext.reranking
            |> KnowledgeReranking.request
            |> KnowledgeRerankRequest.retrievalRequest
            |> KnowledgeRetrievalRequest.authorization

        if authorization <> contextAuthorization then
            raise (UnauthorizedAccessException "Qualification authorization must match the assembled context.")

        { EvaluationCase = evaluationCase
          Authorization = authorization
          Correlation = correlation
          Context = context }

    let evaluationCase value = value.EvaluationCase
    let authorization value = value.Authorization
    let correlation value = value.Correlation
    let context value = value.Context

type KnowledgeGroundedProductionCaseEvidence =
    private
        { Case: KnowledgeGroundedProductionCase
          Generation: KnowledgeGroundedGenerationAdapterResult
          Verification: KnowledgeGroundedVerificationResult option
          Support: KnowledgeGroundedClaimSupportResult option
          EvaluationResult: KnowledgeGenerationCaseResult }

[<RequireQualifiedAccess>]
module KnowledgeGroundedProductionCaseEvidence =
    let case value = value.Case
    let generation value = value.Generation
    let verification value = value.Verification
    let support value = value.Support
    let evaluationResult value = value.EvaluationResult

type KnowledgeGroundedProductionQualificationEvidence =
    private
        { Cases: KnowledgeGroundedProductionCaseEvidence list
          Quality: KnowledgeGroundedGenerationQuality
          Qualification: KnowledgeGroundedReleaseQualificationResult }

[<RequireQualifiedAccess>]
module KnowledgeGroundedProductionQualificationEvidence =
    let cases value = value.Cases
    let quality value = value.Quality
    let qualification value = value.Qualification

[<RequireQualifiedAccess>]
module KnowledgeGroundedProductionQualification =
    let private contextChunkIds context =
        context
        |> KnowledgeAssembledContext.items
        |> List.map (KnowledgeContextItem.citation >> KnowledgeContextCitation.chunkId)

    let private emptyObservation outcome descriptor =
        KnowledgeGenerationObservation.create
            outcome
            []
            descriptor
            (KnowledgeClaimSupportUsage.create 0 0 0m)
            TimeSpan.Zero

    let private observedClaims evaluation =
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
            let assessment = assessments |> Map.find (KnowledgeGroundedClaim.id claim)

            let citations =
                verifiedClaim
                |> KnowledgeVerifiedClaim.citations
                |> List.map (fun citation ->
                    KnowledgeGenerationCitationObservation.create
                        (KnowledgeVerifiedCitation.referenceId citation)
                        (citation
                         |> KnowledgeVerifiedCitation.contextCitation
                         |> KnowledgeContextCitation.chunkId))

            KnowledgeGenerationClaimObservation.create
                (KnowledgeClaimSupportAssessment.verdict assessment)
                citations
                (KnowledgeClaimSupportAssessment.supportingCitationReferenceIds assessment))

    let private supportObservation result =
        let evaluation =
            match result with
            | KnowledgeGroundedClaimSupportResult.Supported value
            | KnowledgeGroundedClaimSupportResult.Unsupported value -> value

        KnowledgeGenerationObservation.create
            KnowledgeGenerationOutcome.Accepted
            (observedClaims evaluation)
            (KnowledgeGroundedClaimSupportEvaluation.evaluator evaluation)
            (KnowledgeGroundedClaimSupportEvaluation.usage evaluation)
            (KnowledgeGroundedClaimSupportEvaluation.duration evaluation)

    let runAsync
        profile
        generationAdapter
        (generator: KnowledgeGroundedGenerationAdapter)
        (supportEvaluator: KnowledgeClaimSupportEvaluator)
        supportBudget
        dataset
        cases
        qualifiedAt
        (cancellationToken: CancellationToken)
        =
        task {
            if isNull (box profile) then
                nullArg (nameof profile)

            if isNull (box generationAdapter) then
                nullArg (nameof generationAdapter)

            if isNull (box generator) then
                nullArg (nameof generator)

            if isNull (box supportEvaluator) then
                nullArg (nameof supportEvaluator)

            if isNull (box supportBudget) then
                nullArg (nameof supportBudget)

            if isNull (box dataset) then
                nullArg (nameof dataset)

            if isNull (box cases) then
                nullArg (nameof cases)

            let expectedCaseIds =
                dataset
                |> KnowledgeEvaluationDataset.cases
                |> List.map KnowledgeEvaluationCase.id

            let actualCaseIds =
                cases
                |> List.map (KnowledgeGroundedProductionCase.evaluationCase >> KnowledgeEvaluationCase.id)

            if
                actualCaseIds.Length <> expectedCaseIds.Length
                || Set.ofList actualCaseIds <> Set.ofList expectedCaseIds
                || (actualCaseIds |> Set.ofList).Count <> actualCaseIds.Length
            then
                invalidArg (nameof cases) "Production qualification requires exactly one input for every dataset case."

            let evidence = ResizeArray<KnowledgeGroundedProductionCaseEvidence>()

            for qualificationCase in cases do
                cancellationToken.ThrowIfCancellationRequested()

                let evaluationCase =
                    KnowledgeGroundedProductionCase.evaluationCase qualificationCase

                let authorization = KnowledgeGroundedProductionCase.authorization qualificationCase
                let context = KnowledgeGroundedProductionCase.context qualificationCase

                let! generation =
                    generator.GenerateAsync
                        authorization
                        (KnowledgeGroundedProductionCase.correlation qualificationCase)
                        context
                        (KnowledgeEvaluationCase.query evaluationCase)
                        cancellationToken

                let caseId = KnowledgeEvaluationCase.id evaluationCase
                let contextIds = contextChunkIds context

                match generation with
                | KnowledgeGroundedGenerationAdapterResult.Rejected _ ->
                    let result =
                        KnowledgeGenerationCaseResult.create
                            caseId
                            contextIds
                            (emptyObservation KnowledgeGenerationOutcome.Rejected supportEvaluator.Descriptor)

                    evidence.Add(
                        { Case = qualificationCase
                          Generation = generation
                          Verification = None
                          Support = None
                          EvaluationResult = result }
                    )
                | KnowledgeGroundedGenerationAdapterResult.Generated execution ->
                    let verification =
                        KnowledgeGroundedCitationVerifier.verify
                            context
                            (KnowledgeGroundedGenerationExecution.result execution)

                    match verification with
                    | KnowledgeGroundedVerificationResult.Rejected _ ->
                        let result =
                            KnowledgeGenerationCaseResult.create
                                caseId
                                contextIds
                                (emptyObservation KnowledgeGenerationOutcome.Rejected supportEvaluator.Descriptor)

                        evidence.Add(
                            { Case = qualificationCase
                              Generation = generation
                              Verification = Some verification
                              Support = None
                              EvaluationResult = result }
                        )
                    | KnowledgeGroundedVerificationResult.InsufficientEvidence _ ->
                        let result =
                            KnowledgeGenerationCaseResult.create
                                caseId
                                contextIds
                                (emptyObservation KnowledgeGenerationOutcome.Abstained supportEvaluator.Descriptor)

                        evidence.Add(
                            { Case = qualificationCase
                              Generation = generation
                              Verification = Some verification
                              Support = None
                              EvaluationResult = result }
                        )
                    | KnowledgeGroundedVerificationResult.VerifiedAnswer verified ->
                        let! support =
                            KnowledgeGroundedClaimSupportEvaluator.evaluateAsync
                                supportEvaluator
                                authorization
                                supportBudget
                                verified
                                cancellationToken

                        let result =
                            KnowledgeGenerationCaseResult.create caseId contextIds (supportObservation support)

                        evidence.Add(
                            { Case = qualificationCase
                              Generation = generation
                              Verification = Some verification
                              Support = Some support
                              EvaluationResult = result }
                        )

            let cases = List.ofSeq evidence

            let quality =
                cases
                |> List.map KnowledgeGroundedProductionCaseEvidence.evaluationResult
                |> KnowledgeGroundedGenerationEvaluator.measure dataset

            let qualification =
                KnowledgeGroundedReleaseQualification.qualify profile generationAdapter qualifiedAt quality

            return
                { Cases = cases
                  Quality = quality
                  Qualification = qualification }
        }
