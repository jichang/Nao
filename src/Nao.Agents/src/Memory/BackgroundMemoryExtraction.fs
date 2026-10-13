namespace Nao.Agents

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

[<Struct>]
type MemoryExtractionConfidence = private MemoryExtractionConfidence of float

[<RequireQualifiedAccess>]
module MemoryExtractionConfidence =
    let tryCreate value =
        if Double.IsFinite value && value >= 0.0 && value <= 1.0 then
            Some(MemoryExtractionConfidence value)
        else
            None

    let create value =
        tryCreate value
        |> Option.defaultWith (fun () ->
            invalidArg (nameof value) "Memory extraction confidence must be finite and between zero and one.")

    let value (MemoryExtractionConfidence value) = value

type MemoryExtractionProposal =
    private
        { Key: string
          Value: string
          SourceText: string
          Confidence: MemoryExtractionConfidence }

[<RequireQualifiedAccess>]
module MemoryExtractionProposal =
    let create key value sourceText confidence =
        if String.IsNullOrWhiteSpace key || key <> key.Trim() then
            invalidArg (nameof key) "Extracted memory keys must be non-blank and trimmed."

        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg (nameof value) "Extracted memory values must be non-blank and trimmed."

        if String.IsNullOrWhiteSpace sourceText || sourceText <> sourceText.Trim() then
            invalidArg (nameof sourceText) "Extracted memory source text must be non-blank and trimmed."

        { Key = key
          Value = value
          SourceText = sourceText
          Confidence = confidence }

    let key value = value.Key
    let value value = value.Value
    let sourceText value = value.SourceText
    let confidence value = value.Confidence

type MemoryExtractionRequest =
    private
        { EventId: Guid
          EventKind: SessionMemoryEventKind
          Content: string
          Correlation: CorrelationContext }

[<RequireQualifiedAccess>]
module MemoryExtractionRequest =
    let eventId value = value.EventId
    let eventKind value = value.EventKind
    let content value = value.Content
    let correlation value = value.Correlation

type MemoryExtractionUsage =
    private
        { ModelCalls: int
          Tokens: int
          CostUsd: decimal }

[<RequireQualifiedAccess>]
module MemoryExtractionUsage =
    let create modelCalls tokens costUsd =
        if modelCalls < 0 then
            invalidArg (nameof modelCalls) "Memory extraction model calls cannot be negative."

        if tokens < 0 then
            invalidArg (nameof tokens) "Memory extraction tokens cannot be negative."

        if costUsd < 0m then
            invalidArg (nameof costUsd) "Memory extraction cost cannot be negative."

        { ModelCalls = modelCalls
          Tokens = tokens
          CostUsd = costUsd }

    let modelCalls value = value.ModelCalls
    let tokens value = value.Tokens
    let costUsd value = value.CostUsd

type MemoryExtractionOutput =
    private
        { Proposals: MemoryExtractionProposal list
          Usage: MemoryExtractionUsage }

[<RequireQualifiedAccess>]
module MemoryExtractionOutput =
    let create proposals usage =
        if isNull (box proposals) then
            nullArg (nameof proposals)

        if isNull (box usage) then
            nullArg (nameof usage)

        { Proposals = proposals; Usage = usage }

    let proposals value = value.Proposals
    let usage value = value.Usage

type MemoryExtractorDescriptor =
    private
        { ComponentId: string
          ComponentVersion: string
          ModelId: string option
          ModelVersion: string option
          ConfigurationHash: string
          Deterministic: bool }

[<RequireQualifiedAccess>]
module MemoryExtractorDescriptor =
    let create componentId componentVersion modelId modelVersion configurationHash deterministic =
        let validate name value =
            if String.IsNullOrWhiteSpace value || value <> value.Trim() then
                invalidArg name "Memory extractor descriptor values must be non-blank and trimmed."

        validate (nameof componentId) componentId
        validate (nameof componentVersion) componentVersion
        validate (nameof configurationHash) configurationHash

        match modelId, modelVersion with
        | None, None -> ()
        | Some id, Some version ->
            validate (nameof modelId) id
            validate (nameof modelVersion) version
        | _ -> invalidArg (nameof modelId) "Memory extractor model identity and version must be supplied together."

        { ComponentId = componentId
          ComponentVersion = componentVersion
          ModelId = modelId
          ModelVersion = modelVersion
          ConfigurationHash = configurationHash
          Deterministic = deterministic }

    let componentId value = value.ComponentId
    let componentVersion value = value.ComponentVersion
    let modelId value = value.ModelId
    let modelVersion value = value.ModelVersion
    let configurationHash value = value.ConfigurationHash
    let deterministic value = value.Deterministic

type BackgroundMemoryExtractor =
    { Descriptor: MemoryExtractorDescriptor
      ExtractAsync: AuthorizationScope -> MemoryExtractionRequest -> CancellationToken -> Task<MemoryExtractionOutput> }

type BackgroundMemoryExtractionPolicy =
    private
        { MinimumConfidence: MemoryExtractionConfidence
          MaximumProposals: int
          MaximumDuration: TimeSpan
          MaximumModelCalls: int
          MaximumTokens: int
          MaximumCostUsd: decimal }

[<RequireQualifiedAccess>]
module BackgroundMemoryExtractionPolicy =
    let create minimumConfidence maximumProposals maximumDuration maximumModelCalls maximumTokens maximumCostUsd =
        if maximumProposals <= 0 then
            invalidArg (nameof maximumProposals) "Memory extraction proposal limits must be positive."

        if maximumDuration <= TimeSpan.Zero then
            invalidArg (nameof maximumDuration) "Memory extraction duration must be positive."

        if maximumModelCalls < 0 then
            invalidArg (nameof maximumModelCalls) "Memory extraction model-call budgets cannot be negative."

        if maximumTokens < 0 then
            invalidArg (nameof maximumTokens) "Memory extraction token budgets cannot be negative."

        if maximumCostUsd < 0m then
            invalidArg (nameof maximumCostUsd) "Memory extraction cost budgets cannot be negative."

        { MinimumConfidence = minimumConfidence
          MaximumProposals = maximumProposals
          MaximumDuration = maximumDuration
          MaximumModelCalls = maximumModelCalls
          MaximumTokens = maximumTokens
          MaximumCostUsd = maximumCostUsd }

    let minimumConfidence value = value.MinimumConfidence
    let maximumProposals value = value.MaximumProposals
    let maximumDuration value = value.MaximumDuration
    let maximumModelCalls value = value.MaximumModelCalls
    let maximumTokens value = value.MaximumTokens
    let maximumCostUsd value = value.MaximumCostUsd

    let defaultPolicy =
        create (MemoryExtractionConfidence.create 0.8) 8 (TimeSpan.FromSeconds 30.0) 1 4096 0.05m

[<RequireQualifiedAccess>]
type MemoryExtractionDisposition =
    | Accepted
    | BelowConfidenceThreshold

type MemoryExtractionAssessment =
    private
        { Proposal: MemoryExtractionProposal
          Disposition: MemoryExtractionDisposition }

[<RequireQualifiedAccess>]
module MemoryExtractionAssessment =
    let proposal value = value.Proposal
    let disposition value = value.Disposition

type BackgroundMemoryExtractionBatch =
    private
        { Source: CrossSessionUserMemoryCandidate
          Extractor: MemoryExtractorDescriptor
          Assessments: MemoryExtractionAssessment list
          Usage: MemoryExtractionUsage
          Duration: TimeSpan
          ExtractedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module BackgroundMemoryExtractionBatch =
    let source value = value.Source
    let extractor value = value.Extractor
    let assessments value = value.Assessments
    let usage value = value.Usage
    let duration value = value.Duration
    let extractedAt value = value.ExtractedAt

    let accepted value =
        value.Assessments
        |> List.filter (fun assessment -> assessment.Disposition = MemoryExtractionDisposition.Accepted)

    let belowConfidenceThreshold value =
        value.Assessments
        |> List.filter (fun assessment -> assessment.Disposition = MemoryExtractionDisposition.BelowConfidenceThreshold)

[<RequireQualifiedAccess>]
module BackgroundMemoryExtraction =
    let private request source =
        let event =
            source
            |> CrossSessionUserMemoryCandidate.candidate
            |> DurableSessionMemoryCandidate.event

        { EventId = SessionMemoryEvent.id event
          EventKind = SessionMemoryEvent.kind event
          Content = SessionMemoryEvent.content event
          Correlation = SessionMemoryEvent.correlation event }

    let private validateUsage policy usage =
        if MemoryExtractionUsage.modelCalls usage > BackgroundMemoryExtractionPolicy.maximumModelCalls policy then
            invalidOp "Memory extractor exceeded the model-call budget."

        if MemoryExtractionUsage.tokens usage > BackgroundMemoryExtractionPolicy.maximumTokens policy then
            invalidOp "Memory extractor exceeded the token budget."

        if MemoryExtractionUsage.costUsd usage > BackgroundMemoryExtractionPolicy.maximumCostUsd policy then
            invalidOp "Memory extractor exceeded the cost budget."

    let private assess
        (policy: BackgroundMemoryExtractionPolicy)
        (sourceContent: string)
        (proposals: MemoryExtractionProposal list)
        =
        if proposals.Length > BackgroundMemoryExtractionPolicy.maximumProposals policy then
            invalidOp "Memory extractor exceeded the proposal limit."

        let keyed =
            Dictionary<string, MemoryExtractionProposal>(StringComparer.OrdinalIgnoreCase)

        proposals
        |> List.map (fun proposal ->
            let key = MemoryExtractionProposal.key proposal

            if not (keyed.TryAdd(key, proposal)) then
                invalidOp $"Memory extractor returned duplicate key '{key}'."

            let sourceText = MemoryExtractionProposal.sourceText proposal

            if not (sourceContent.Contains(sourceText, StringComparison.Ordinal)) then
                invalidOp $"Memory extractor returned source text outside the approved session event for key '{key}'."

            let disposition =
                if
                    MemoryExtractionConfidence.value (MemoryExtractionProposal.confidence proposal)
                    >= MemoryExtractionConfidence.value (BackgroundMemoryExtractionPolicy.minimumConfidence policy)
                then
                    MemoryExtractionDisposition.Accepted
                else
                    MemoryExtractionDisposition.BelowConfidenceThreshold

            { Proposal = proposal
              Disposition = disposition })

    let extractAsync
        (extractor: BackgroundMemoryExtractor)
        (authorization: AuthorizationScope)
        (policy: BackgroundMemoryExtractionPolicy)
        extractedAt
        (source: CrossSessionUserMemoryCandidate)
        (cancellationToken: CancellationToken)
        =
        task {
            if isNull (box extractor) || isNull (box extractor.ExtractAsync) then
                nullArg (nameof extractor)

            if isNull (box authorization) then
                nullArg (nameof authorization)

            if isNull (box policy) then
                nullArg (nameof policy)

            if isNull (box source) then
                nullArg (nameof source)

            if
                AuthorizationScope.identity authorization
                <> CrossSessionUserMemoryCandidate.owner source
            then
                raise (UnauthorizedAccessException "Memory extraction requires the approved cross-session owner.")

            cancellationToken.ThrowIfCancellationRequested()
            let request = request source
            use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
            timeout.CancelAfter(BackgroundMemoryExtractionPolicy.maximumDuration policy)
            let stopwatch = Stopwatch.StartNew()
            let! output = extractor.ExtractAsync authorization request timeout.Token
            stopwatch.Stop()

            if isNull (box output) then
                invalidOp "Memory extractor returned a null output."

            let usage = MemoryExtractionOutput.usage output
            validateUsage policy usage

            let assessments =
                output
                |> MemoryExtractionOutput.proposals
                |> assess policy (MemoryExtractionRequest.content request)

            return
                { Source = source
                  Extractor = extractor.Descriptor
                  Assessments = assessments
                  Usage = usage
                  Duration = stopwatch.Elapsed
                  ExtractedAt = extractedAt }
        }
