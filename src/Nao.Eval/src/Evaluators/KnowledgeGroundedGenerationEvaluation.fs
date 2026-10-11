namespace Nao.Eval

open System
open Nao.Knowledge

[<RequireQualifiedAccess>]
type KnowledgeGenerationOutcome =
    | Accepted
    | Warned
    | Abstained
    | RepairRequired
    | Rejected

type KnowledgeGenerationCitationObservation =
    private
        { ReferenceId: KnowledgeGroundedCitationReferenceId
          ChunkId: KnowledgeChunkId }

[<RequireQualifiedAccess>]
module KnowledgeGenerationCitationObservation =
    let create referenceId chunkId =
        { ReferenceId = referenceId
          ChunkId = chunkId }

    let referenceId value = value.ReferenceId
    let chunkId value = value.ChunkId

type KnowledgeGenerationClaimObservation =
    private
        { Verdict: KnowledgeClaimSupportVerdict
          Citations: KnowledgeGenerationCitationObservation list
          SupportingCitationReferenceIds: Set<KnowledgeGroundedCitationReferenceId> }

[<RequireQualifiedAccess>]
module KnowledgeGenerationClaimObservation =
    let create verdict citations supportingCitationReferenceIds =
        if isNull (box citations) then
            nullArg (nameof citations)

        let referenceIds =
            citations |> List.map KnowledgeGenerationCitationObservation.referenceId

        if referenceIds |> List.distinct |> List.length <> referenceIds.Length then
            invalidArg (nameof citations) "Generation claim citation references must be unique."

        let supportingCitationReferenceIds = Set.ofSeq supportingCitationReferenceIds

        let unknownSupportingReferences =
            Set.difference supportingCitationReferenceIds (Set.ofList referenceIds)

        if not unknownSupportingReferences.IsEmpty then
            invalidArg
                (nameof supportingCitationReferenceIds)
                "Supporting citation references must belong to the same generation claim."

        { Verdict = verdict
          Citations = citations
          SupportingCitationReferenceIds = supportingCitationReferenceIds }

    let verdict value = value.Verdict
    let citations value = value.Citations
    let supportingCitationReferenceIds value = value.SupportingCitationReferenceIds

type KnowledgeGenerationObservation =
    private
        { Outcome: KnowledgeGenerationOutcome
          Claims: KnowledgeGenerationClaimObservation list
          SupportEvaluator: KnowledgeClaimSupportEvaluatorDescriptor
          SupportUsage: KnowledgeClaimSupportUsage
          SupportDuration: TimeSpan }

[<RequireQualifiedAccess>]
module KnowledgeGenerationObservation =
    let create outcome claims supportEvaluator supportUsage supportDuration =
        if isNull (box claims) then
            nullArg (nameof claims)

        if isNull (box supportEvaluator) then
            nullArg (nameof supportEvaluator)

        if isNull (box supportUsage) then
            nullArg (nameof supportUsage)

        if supportDuration < TimeSpan.Zero then
            invalidArg (nameof supportDuration) "Generation support evaluation duration cannot be negative."

        match outcome with
        | KnowledgeGenerationOutcome.Accepted
        | KnowledgeGenerationOutcome.Warned when List.isEmpty claims ->
            invalidArg (nameof claims) "Published grounded answers require at least one observed claim."
        | _ -> ()

        { Outcome = outcome
          Claims = claims
          SupportEvaluator = supportEvaluator
          SupportUsage = supportUsage
          SupportDuration = supportDuration }

    let outcome value = value.Outcome
    let claims value = value.Claims
    let supportEvaluator value = value.SupportEvaluator
    let supportUsage value = value.SupportUsage
    let supportDuration value = value.SupportDuration

    let ofGroundedOutput output =
        if isNull (box output) then
            nullArg (nameof output)

        let audit = KnowledgeGroundedOutput.auditRecord output

        let outcome =
            match KnowledgeGroundedAuditRecord.outcome audit with
            | KnowledgeGroundedAuditOutcome.Accepted -> KnowledgeGenerationOutcome.Accepted
            | KnowledgeGroundedAuditOutcome.Warned -> KnowledgeGenerationOutcome.Warned
            | KnowledgeGroundedAuditOutcome.Abstained -> KnowledgeGenerationOutcome.Abstained
            | KnowledgeGroundedAuditOutcome.RepairRequired -> KnowledgeGenerationOutcome.RepairRequired
            | KnowledgeGroundedAuditOutcome.Rejected -> KnowledgeGenerationOutcome.Rejected

        let claims =
            match KnowledgeGroundedOutput.artifact output with
            | Some artifact ->
                artifact
                |> KnowledgeGroundedOutputArtifact.claims
                |> List.map (fun outputClaim ->
                    let support = KnowledgeGroundedOutputClaim.support outputClaim

                    let citations =
                        outputClaim
                        |> KnowledgeGroundedOutputClaim.citations
                        |> List.map (fun citation ->
                            let provenance = KnowledgeGroundedOutputCitation.provenance citation

                            KnowledgeGenerationCitationObservation.create
                                (KnowledgeGroundedCitationProvenance.referenceId provenance)
                                (KnowledgeGroundedCitationProvenance.chunkId provenance))

                    KnowledgeGenerationClaimObservation.create
                        (KnowledgeClaimSupportAssessment.verdict support)
                        citations
                        (KnowledgeClaimSupportAssessment.supportingCitationReferenceIds support))
            | None ->
                audit
                |> KnowledgeGroundedAuditRecord.claims
                |> List.map (fun claim ->
                    let citations =
                        claim
                        |> KnowledgeGroundedClaimAuditEvidence.citations
                        |> List.map (fun provenance ->
                            KnowledgeGenerationCitationObservation.create
                                (KnowledgeGroundedCitationProvenance.referenceId provenance)
                                (KnowledgeGroundedCitationProvenance.chunkId provenance))

                    KnowledgeGenerationClaimObservation.create
                        (KnowledgeGroundedClaimAuditEvidence.supportVerdict claim)
                        citations
                        [])

        create
            outcome
            claims
            (KnowledgeGroundedAuditRecord.evaluator audit)
            (KnowledgeGroundedAuditRecord.usage audit)
            (KnowledgeGroundedAuditRecord.evaluationDuration audit)

type KnowledgeGenerationCaseResult =
    private
        { CaseId: string
          ContextChunkIds: KnowledgeChunkId list
          Observation: KnowledgeGenerationObservation }

[<RequireQualifiedAccess>]
module KnowledgeGenerationCaseResult =
    let create caseId contextChunkIds observation =
        if String.IsNullOrWhiteSpace caseId || caseId <> caseId.Trim() then
            invalidArg (nameof caseId) "Generation evaluation case identity must be non-blank and trimmed."

        if isNull (box contextChunkIds) then
            nullArg (nameof contextChunkIds)

        if contextChunkIds |> List.distinct |> List.length <> contextChunkIds.Length then
            invalidArg (nameof contextChunkIds) "Generation evaluation context chunks must be unique."

        if isNull (box observation) then
            nullArg (nameof observation)

        { CaseId = caseId
          ContextChunkIds = contextChunkIds
          Observation = observation }

    let caseId value = value.CaseId
    let contextChunkIds value = value.ContextChunkIds
    let observation value = value.Observation

[<RequireQualifiedAccess>]
type KnowledgeExpectedResponse =
    | Answer
    | Abstain

[<RequireQualifiedAccess>]
type KnowledgeObservedResponse =
    | Answer
    | Abstain
    | Unresolved

type KnowledgeGenerationCaseMeasurement =
    private
        { Case: KnowledgeEvaluationCase
          ContextChunkIds: KnowledgeChunkId list
          RelevantContextChunks: int
          ContextRelevance: float option
          FaithfulClaims: int
          MaterialClaims: int
          AnswerFaithfulness: float option
          CorrectCitations: int
          Citations: int
          CitationCorrectness: float option
          ExpectedResponse: KnowledgeExpectedResponse
          ObservedResponse: KnowledgeObservedResponse
          AbstentionCorrect: bool
          SupportEvaluator: KnowledgeClaimSupportEvaluatorDescriptor
          SupportUsage: KnowledgeClaimSupportUsage
          SupportDuration: TimeSpan }

[<RequireQualifiedAccess>]
module KnowledgeGenerationCaseMeasurement =
    let case value = value.Case
    let contextChunkIds value = value.ContextChunkIds
    let relevantContextChunks value = value.RelevantContextChunks
    let contextRelevance value = value.ContextRelevance
    let faithfulClaims value = value.FaithfulClaims
    let materialClaims value = value.MaterialClaims
    let answerFaithfulness value = value.AnswerFaithfulness
    let correctCitations value = value.CorrectCitations
    let citations value = value.Citations
    let citationCorrectness value = value.CitationCorrectness
    let expectedResponse value = value.ExpectedResponse
    let observedResponse value = value.ObservedResponse
    let abstentionCorrect value = value.AbstentionCorrect
    let supportEvaluator value = value.SupportEvaluator
    let supportUsage value = value.SupportUsage
    let supportDuration value = value.SupportDuration

type KnowledgeGenerationSegmentQuality =
    private
        { Segment: KnowledgeEvaluationSegment
          CaseCount: int
          ContextRelevance: float option
          AnswerFaithfulness: float option
          CitationCorrectness: float option
          AbstentionAccuracy: float
          TotalModelCalls: int
          TotalTokens: int
          TotalCostUsd: decimal
          TotalSupportDuration: TimeSpan }

[<RequireQualifiedAccess>]
module KnowledgeGenerationSegmentQuality =
    let segment value = value.Segment
    let caseCount value = value.CaseCount
    let contextRelevance value = value.ContextRelevance
    let answerFaithfulness value = value.AnswerFaithfulness
    let citationCorrectness value = value.CitationCorrectness
    let abstentionAccuracy value = value.AbstentionAccuracy
    let totalModelCalls value = value.TotalModelCalls
    let totalTokens value = value.TotalTokens
    let totalCostUsd value = value.TotalCostUsd
    let totalSupportDuration value = value.TotalSupportDuration

type KnowledgeGroundedGenerationQuality =
    private
        { Evaluator: ComponentVersion
          Dataset: KnowledgeEvaluationDataset
          Cases: KnowledgeGenerationCaseMeasurement list
          ContextRelevance: float option
          AnswerFaithfulness: float option
          CitationCorrectness: float option
          AbstentionAccuracy: float
          ExpectedAbstentions: int
          ActualAbstentions: int
          CorrectAbstentions: int
          CorrectAnswers: int
          UnresolvedResponses: int
          TotalModelCalls: int
          TotalTokens: int
          TotalCostUsd: decimal
          TotalSupportDuration: TimeSpan
          Segments: KnowledgeGenerationSegmentQuality list }

[<RequireQualifiedAccess>]
module KnowledgeGroundedGenerationQuality =
    let evaluator value = value.Evaluator
    let dataset value = value.Dataset
    let cases value = value.Cases
    let contextRelevance value = value.ContextRelevance
    let answerFaithfulness value = value.AnswerFaithfulness
    let citationCorrectness value = value.CitationCorrectness
    let abstentionAccuracy value = value.AbstentionAccuracy
    let expectedAbstentions value = value.ExpectedAbstentions
    let actualAbstentions value = value.ActualAbstentions
    let correctAbstentions value = value.CorrectAbstentions
    let correctAnswers value = value.CorrectAnswers
    let unresolvedResponses value = value.UnresolvedResponses
    let totalModelCalls value = value.TotalModelCalls
    let totalTokens value = value.TotalTokens
    let totalCostUsd value = value.TotalCostUsd
    let totalSupportDuration value = value.TotalSupportDuration
    let segments value = value.Segments

[<RequireQualifiedAccess>]
module KnowledgeGroundedGenerationEvaluator =
    let descriptor = ComponentVersion.create "nao-grounded-generation-evaluation" "1"

    let private averageMeasured selector measurements =
        let values = measurements |> List.choose selector

        if List.isEmpty values then
            None
        else
            Some(List.average values)

    let private ratio numerator denominator =
        if denominator = 0 then
            None
        else
            Some(float numerator / float denominator)

    let private measureCase case result =
        let contextChunkIds = KnowledgeGenerationCaseResult.contextChunkIds result
        let relevantChunks = KnowledgeEvaluationCase.relevantChunkIds case

        let relevantContextChunks =
            contextChunkIds |> List.filter relevantChunks.Contains |> List.length

        let observation = KnowledgeGenerationCaseResult.observation result
        let outcome = KnowledgeGenerationObservation.outcome observation
        let claims = KnowledgeGenerationObservation.claims observation

        let published =
            match outcome with
            | KnowledgeGenerationOutcome.Accepted
            | KnowledgeGenerationOutcome.Warned -> true
            | _ -> false

        let faithfulClaims =
            if published then
                claims
                |> List.filter (
                    KnowledgeGenerationClaimObservation.verdict
                    >> (=) KnowledgeClaimSupportVerdict.Supported
                )
                |> List.length
            else
                0

        let materialClaims = if published then claims.Length else 0

        let correctCitations, citations =
            if published then
                claims
                |> List.fold
                    (fun (correct, total) claim ->
                        let supportingReferences =
                            KnowledgeGenerationClaimObservation.supportingCitationReferenceIds claim

                        claim
                        |> KnowledgeGenerationClaimObservation.citations
                        |> List.fold
                            (fun (claimCorrect, claimTotal) citation ->
                                let correctCitation =
                                    supportingReferences.Contains(
                                        KnowledgeGenerationCitationObservation.referenceId citation
                                    )
                                    && relevantChunks.Contains(
                                        KnowledgeGenerationCitationObservation.chunkId citation
                                    )

                                claimCorrect + (if correctCitation then 1 else 0), claimTotal + 1)
                            (correct, total))
                    (0, 0)
            else
                0, 0

        let expectedResponse =
            if case |> KnowledgeEvaluationCase.expectedFacts |> List.isEmpty then
                KnowledgeExpectedResponse.Abstain
            else
                KnowledgeExpectedResponse.Answer

        let observedResponse =
            match outcome with
            | KnowledgeGenerationOutcome.Accepted
            | KnowledgeGenerationOutcome.Warned -> KnowledgeObservedResponse.Answer
            | KnowledgeGenerationOutcome.Abstained -> KnowledgeObservedResponse.Abstain
            | KnowledgeGenerationOutcome.RepairRequired
            | KnowledgeGenerationOutcome.Rejected -> KnowledgeObservedResponse.Unresolved

        { Case = case
          ContextChunkIds = contextChunkIds
          RelevantContextChunks = relevantContextChunks
          ContextRelevance = ratio relevantContextChunks contextChunkIds.Length
          FaithfulClaims = faithfulClaims
          MaterialClaims = materialClaims
          AnswerFaithfulness = ratio faithfulClaims materialClaims
          CorrectCitations = correctCitations
          Citations = citations
          CitationCorrectness = ratio correctCitations citations
          ExpectedResponse = expectedResponse
          ObservedResponse = observedResponse
          AbstentionCorrect =
            match expectedResponse, observedResponse with
            | KnowledgeExpectedResponse.Answer, KnowledgeObservedResponse.Answer
            | KnowledgeExpectedResponse.Abstain, KnowledgeObservedResponse.Abstain -> true
            | _ -> false
          SupportEvaluator = KnowledgeGenerationObservation.supportEvaluator observation
          SupportUsage = KnowledgeGenerationObservation.supportUsage observation
          SupportDuration = KnowledgeGenerationObservation.supportDuration observation }

    let private segmentQuality segment (measurements: KnowledgeGenerationCaseMeasurement list) =
        let usage = measurements |> List.map KnowledgeGenerationCaseMeasurement.supportUsage

        { Segment = segment
          CaseCount = measurements.Length
          ContextRelevance = averageMeasured KnowledgeGenerationCaseMeasurement.contextRelevance measurements
          AnswerFaithfulness = averageMeasured KnowledgeGenerationCaseMeasurement.answerFaithfulness measurements
          CitationCorrectness = averageMeasured KnowledgeGenerationCaseMeasurement.citationCorrectness measurements
          AbstentionAccuracy =
            measurements
            |> List.averageBy (fun measurement ->
                if KnowledgeGenerationCaseMeasurement.abstentionCorrect measurement then
                    1.0
                else
                    0.0)
          TotalModelCalls = usage |> List.sumBy KnowledgeClaimSupportUsage.modelCalls
          TotalTokens = usage |> List.sumBy KnowledgeClaimSupportUsage.tokens
          TotalCostUsd = usage |> List.sumBy KnowledgeClaimSupportUsage.costUsd
          TotalSupportDuration =
            measurements
            |> List.sumBy (KnowledgeGenerationCaseMeasurement.supportDuration >> _.Ticks)
            |> TimeSpan.FromTicks }

    let private segments measurements =
        measurements
        |> List.collect (fun measurement ->
            measurement
            |> KnowledgeGenerationCaseMeasurement.case
            |> KnowledgeEvaluationCase.dimensions
            |> KnowledgeEvaluationSegment.ofDimensions
            |> List.map (fun segment -> segment, measurement))
        |> List.groupBy fst
        |> List.map (fun (segment, values) -> values |> List.map snd |> segmentQuality segment)
        |> List.sortBy (fun quality ->
            let segment = KnowledgeGenerationSegmentQuality.segment quality
            KnowledgeEvaluationSegment.dimension segment, KnowledgeEvaluationSegment.value segment)

    let measure dataset results =
        if isNull (box dataset) then
            nullArg (nameof dataset)

        if isNull (box results) then
            nullArg (nameof results)

        let duplicateCaseIds =
            results
            |> List.countBy KnowledgeGenerationCaseResult.caseId
            |> List.filter (fun (_, count) -> count > 1)

        if not (List.isEmpty duplicateCaseIds) then
            invalidArg (nameof results) "Generation evaluation results must contain each case exactly once."

        let resultsByCase =
            results
            |> List.map (fun result -> KnowledgeGenerationCaseResult.caseId result, result)
            |> Map.ofList

        let cases = KnowledgeEvaluationDataset.cases dataset
        let expectedCaseIds = cases |> List.map KnowledgeEvaluationCase.id |> Set.ofList
        let actualCaseIds = resultsByCase |> Map.keys |> Set.ofSeq

        if expectedCaseIds <> actualCaseIds then
            invalidArg (nameof results) "Generation evaluation results must exactly match the dataset cases."

        let measurements =
            cases
            |> List.map (fun case -> measureCase case (Map.find (KnowledgeEvaluationCase.id case) resultsByCase))

        let usage = measurements |> List.map KnowledgeGenerationCaseMeasurement.supportUsage

        { Evaluator = descriptor
          Dataset = dataset
          Cases = measurements
          ContextRelevance = averageMeasured KnowledgeGenerationCaseMeasurement.contextRelevance measurements
          AnswerFaithfulness = averageMeasured KnowledgeGenerationCaseMeasurement.answerFaithfulness measurements
          CitationCorrectness = averageMeasured KnowledgeGenerationCaseMeasurement.citationCorrectness measurements
          AbstentionAccuracy =
            measurements
            |> List.averageBy (fun measurement ->
                if KnowledgeGenerationCaseMeasurement.abstentionCorrect measurement then
                    1.0
                else
                    0.0)
          ExpectedAbstentions =
            measurements
            |> List.filter (
                KnowledgeGenerationCaseMeasurement.expectedResponse
                >> (=) KnowledgeExpectedResponse.Abstain
            )
            |> List.length
          ActualAbstentions =
            measurements
            |> List.filter (
                KnowledgeGenerationCaseMeasurement.observedResponse
                >> (=) KnowledgeObservedResponse.Abstain
            )
            |> List.length
          CorrectAbstentions =
            measurements
            |> List.filter (fun measurement ->
                let expected = KnowledgeGenerationCaseMeasurement.expectedResponse measurement

                expected = KnowledgeExpectedResponse.Abstain
                && KnowledgeGenerationCaseMeasurement.abstentionCorrect measurement)
            |> List.length
          CorrectAnswers =
            measurements
            |> List.filter (fun measurement ->
                let expected = KnowledgeGenerationCaseMeasurement.expectedResponse measurement

                expected = KnowledgeExpectedResponse.Answer
                && KnowledgeGenerationCaseMeasurement.abstentionCorrect measurement)
            |> List.length
          UnresolvedResponses =
            measurements
            |> List.filter (
                KnowledgeGenerationCaseMeasurement.observedResponse
                >> (=) KnowledgeObservedResponse.Unresolved
            )
            |> List.length
          TotalModelCalls = usage |> List.sumBy KnowledgeClaimSupportUsage.modelCalls
          TotalTokens = usage |> List.sumBy KnowledgeClaimSupportUsage.tokens
          TotalCostUsd = usage |> List.sumBy KnowledgeClaimSupportUsage.costUsd
          TotalSupportDuration =
            measurements
            |> List.sumBy (KnowledgeGenerationCaseMeasurement.supportDuration >> _.Ticks)
            |> TimeSpan.FromTicks
          Segments = segments measurements }
