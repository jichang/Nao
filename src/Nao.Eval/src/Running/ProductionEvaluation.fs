namespace Nao.Eval

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open Nao.Agents

[<RequireQualifiedAccess>]
type EvalProductionSignal =
    | Failure
    | Denial
    | Retry
    | Abandonment
    | LowConfidence
    | NegativeFeedback

type EvalProductionRedaction =
    private
        { Text: string
          RemovedValueCount: int
          AppliedRules: string list }

[<RequireQualifiedAccess>]
module EvalProductionRedaction =
    let create text removedValueCount appliedRules =
        if String.IsNullOrWhiteSpace text || text <> text.Trim() then
            invalidArg (nameof text) "Redacted production text must be canonical and non-blank."

        if removedValueCount < 0 then
            invalidArg (nameof removedValueCount) "Removed-value counts cannot be negative."

        if List.isEmpty appliedRules then
            invalidArg (nameof appliedRules) "Production redaction requires at least one explicit rule."

        appliedRules
        |> List.iter (fun rule ->
            if String.IsNullOrWhiteSpace rule || rule <> rule.Trim() then
                invalidArg (nameof appliedRules) "Redaction rules must be canonical and non-blank.")

        { Text = text
          RemovedValueCount = removedValueCount
          AppliedRules = List.distinct appliedRules }

    let text value = value.Text
    let removedValueCount value = value.RemovedValueCount
    let appliedRules value = value.AppliedRules

type EvalProductionRedactor =
    { Identity: EvalComponentIdentity
      Redact: string -> EvalProductionRedaction }

type EvalProductionSamplingPolicy =
    private
        { PolicyId: string
          SampleRate: decimal
          SamplingSalt: string
          ConsentRecordSha256: string
          RetainFor: TimeSpan
          AllowedSignals: Set<EvalProductionSignal>
          Redactor: EvalProductionRedactor }

[<RequireQualifiedAccess>]
module EvalProductionSamplingPolicy =
    let create policyId sampleRate samplingSalt consentRecordSha256 retainFor allowedSignals redactor =
        if String.IsNullOrWhiteSpace policyId || policyId <> policyId.Trim() then
            invalidArg (nameof policyId) "Sampling policy identity must be canonical and non-blank."

        if sampleRate <= 0m || sampleRate > 1m then
            invalidArg (nameof sampleRate) "Production sample rates must be greater than zero and at most one."

        if String.IsNullOrWhiteSpace samplingSalt then
            invalidArg (nameof samplingSalt) "Production sampling requires a private non-blank salt."

        if
            String.IsNullOrWhiteSpace consentRecordSha256
            || consentRecordSha256.Length <> 64
            || consentRecordSha256
               |> Seq.exists (fun character -> not (Char.IsDigit character || character >= 'a' && character <= 'f'))
        then
            invalidArg (nameof consentRecordSha256) "Consent records require a canonical lowercase SHA-256."

        if retainFor <= TimeSpan.Zero then
            invalidArg (nameof retainFor) "Production samples require a positive retention period."

        if Set.isEmpty allowedSignals then
            invalidArg (nameof allowedSignals) "Production sampling requires at least one allowed signal."

        if isNull (box redactor) || isNull (box redactor.Redact) then
            nullArg (nameof redactor)

        { PolicyId = policyId
          SampleRate = sampleRate
          SamplingSalt = samplingSalt
          ConsentRecordSha256 = consentRecordSha256
          RetainFor = retainFor
          AllowedSignals = allowedSignals
          Redactor = redactor }

type EvalProductionObservation =
    private
        { SchemaVersion: int
          Id: Guid
          Owner: AuthorizationIdentity
          TurnId: string
          ExecutionId: ExecutionId
          Configuration: EvalExecutableConfiguration
          Input: EvalProductionRedaction
          Output: EvalProductionRedaction
          Feedback: EvalProductionRedaction option
          Signal: EvalProductionSignal
          Segments: Map<EvalSegmentDimension, string>
          ConsentRecordSha256: string
          SamplingPolicyId: string
          Redactor: EvalComponentIdentity
          CapturedAt: DateTimeOffset
          RetainUntil: DateTimeOffset }

[<RequireQualifiedAccess>]
module EvalProductionObservation =
    [<Literal>]
    let CurrentSchemaVersion = 1

    let id value = value.Id
    let owner value = value.Owner
    let turnId value = value.TurnId
    let executionId value = value.ExecutionId
    let configuration value = value.Configuration
    let input value = value.Input
    let output value = value.Output
    let feedback value = value.Feedback
    let signal value = value.Signal
    let segments value = value.Segments
    let consentRecordSha256 value = value.ConsentRecordSha256
    let samplingPolicyId value = value.SamplingPolicyId
    let capturedAt value = value.CapturedAt
    let retainUntil value = value.RetainUntil

    let private bucket policy executionId =
        let canonical =
            String.concat "|" [ policy.SamplingSalt; policy.PolicyId; ExecutionId.serialize executionId ]

        let bytes = canonical |> Encoding.UTF8.GetBytes |> SHA256.HashData
        let value = BitConverter.ToUInt64(bytes, 0)
        decimal (value % 1_000_000UL) / 1_000_000m

    let tryCapture authorization capturedAt configuration segments signal feedback (turn: TurnRecord) policy =
        if not (policy.AllowedSignals |> Set.contains signal) then
            None
        elif bucket policy turn.Correlation.ExecutionId >= policy.SampleRate then
            None
        else
            if String.IsNullOrWhiteSpace turn.TurnId then
                invalidArg (nameof turn) "Production observations require a stable turn identity."

            if capturedAt < turn.CreatedAt then
                invalidArg (nameof capturedAt) "Production observations cannot predate the completed turn."

            segments
            |> Map.iter (fun _ value ->
                if String.IsNullOrWhiteSpace value || value <> value.Trim() then
                    invalidArg (nameof segments) "Production observation segments must be canonical and non-blank.")

            let redact fieldName value =
                if String.IsNullOrWhiteSpace value then
                    invalidArg fieldName "Production observation text cannot be blank before redaction."

                let result = policy.Redactor.Redact value

                if isNull (box result) then
                    invalidOp "Production redactor returned null evidence."

                result

            let feedback = feedback |> Option.map (redact (nameof feedback))

            Some
                { SchemaVersion = CurrentSchemaVersion
                  Id = Guid.NewGuid()
                  Owner = AuthorizationScope.identity authorization
                  TurnId = turn.TurnId
                  ExecutionId = turn.Correlation.ExecutionId
                  Configuration = configuration
                  Input = redact (nameof turn.Input) turn.Input
                  Output = redact (nameof turn.Output) turn.Output
                  Feedback = feedback
                  Signal = signal
                  Segments = segments
                  ConsentRecordSha256 = policy.ConsentRecordSha256
                  SamplingPolicyId = policy.PolicyId
                  Redactor = policy.Redactor.Identity
                  CapturedAt = capturedAt
                  RetainUntil = capturedAt + policy.RetainFor }

    let captureFeedback authorization capturedAt configuration segments (turn: TurnRecord) (feedback: Feedback) policy =
        if feedback.TurnId <> turn.TurnId then
            invalidArg (nameof feedback) "User feedback must reference the captured turn."

        if feedback.CreatedAt < turn.CreatedAt || feedback.CreatedAt > capturedAt then
            invalidArg (nameof feedback) "User feedback must follow the turn and precede observation capture."

        let signal =
            match feedback.Sentiment with
            | FeedbackSentiment.Negative -> EvalProductionSignal.NegativeFeedback
            | FeedbackSentiment.Positive
            | FeedbackSentiment.Neutral ->
                invalidArg (nameof feedback) "Only explicit negative feedback creates a failure candidate."

        tryCapture authorization capturedAt configuration segments signal feedback.Comment turn policy

type EvalProductionFailureCluster =
    { Signal: EvalProductionSignal
      Segments: Map<EvalSegmentDimension, string>
      Configuration: EvalExecutableConfiguration
      ObservationIds: Guid list
      Count: int
      FirstObservedAt: DateTimeOffset
      LastObservedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module EvalProductionFailureClustering =
    let cluster observations =
        observations
        |> List.groupBy (fun observation ->
            EvalProductionObservation.signal observation,
            EvalProductionObservation.segments observation,
            EvalProductionObservation.configuration observation)
        |> List.map (fun ((signal, segments, configuration), grouped) ->
            let ordered = grouped |> List.sortBy EvalProductionObservation.capturedAt

            { Signal = signal
              Segments = segments
              Configuration = configuration
              ObservationIds = ordered |> List.map EvalProductionObservation.id
              Count = ordered.Length
              FirstObservedAt = ordered |> List.head |> EvalProductionObservation.capturedAt
              LastObservedAt = ordered |> List.last |> EvalProductionObservation.capturedAt })
        |> List.sortByDescending (fun cluster -> cluster.Count)

type EvalProductionCandidate =
    private
        { Observation: EvalProductionObservation
          ProposedBy: AuthorizationIdentity
          ProposedAt: DateTimeOffset }

type EvalProductionCandidateReview =
    { CaseId: string
      Description: string
      ExpectedBehavior: EvalExpectedBehavior
      Labels: Set<string>
      Reviewer: AuthorizationIdentity
      ReviewedAt: DateTimeOffset
      Rationale: string }

[<RequireQualifiedAccess>]
module EvalProductionCandidate =
    let propose authorization proposedAt observation =
        if
            proposedAt < EvalProductionObservation.capturedAt observation
            || proposedAt > EvalProductionObservation.retainUntil observation
        then
            invalidOp "Production observations can become candidates only within their retention window."

        AuthorizationScope.requireOwner (EvalProductionObservation.owner observation) authorization

        { Observation = observation
          ProposedBy = AuthorizationScope.identity authorization
          ProposedAt = proposedAt }

    let observation value = value.Observation

    let review authorization reviewedAt caseId description expectedBehavior labels rationale candidate =
        if
            reviewedAt < candidate.ProposedAt
            || reviewedAt > EvalProductionObservation.retainUntil candidate.Observation
        then
            invalidOp "Production candidates can be reviewed only after proposal and within retention."

        let reviewer = AuthorizationScope.identity authorization

        if reviewer = candidate.ProposedBy then
            invalidArg (nameof authorization) "Production candidates require an independent reviewer."

        let owner = EvalProductionObservation.owner candidate.Observation

        if
            reviewer.TenantId <> owner.TenantId
            || reviewer.GroupId <> owner.GroupId
            || reviewer.WorkspaceId <> owner.WorkspaceId
            || reviewer.SessionId <> owner.SessionId
        then
            invalidArg (nameof authorization) "Production candidate review cannot cross an ownership boundary."

        if String.IsNullOrWhiteSpace rationale || rationale <> rationale.Trim() then
            invalidArg (nameof rationale) "Production candidate reviews require a canonical non-blank rationale."

        let approval =
            { Reviewer = AuthorizationIdentity.storageKey reviewer
              ReviewedAt = reviewedAt
              Rationale = rationale }

        let observation = candidate.Observation

        let sanitization =
            { SanitizedBy = observation.Redactor |> EvalComponentIdentity.name
              SanitizedAt = observation.CapturedAt
              AppliedRules =
                [ observation.Input; observation.Output ]
                @ (observation.Feedback |> Option.toList)
                |> List.collect EvalProductionRedaction.appliedRules
                |> List.distinct
              Approval = approval }

        let feedbackTurns =
            observation.Feedback
            |> Option.map (fun feedback ->
                { Id = "feedback"
                  Role = EvalTurnRole.User
                  Content = EvalProductionRedaction.text feedback })
            |> Option.toList

        let evaluationCase =
            EvalCase.create
                (AuthorizationIdentity.storageKey observation.Owner)
                caseId
                description
                ([ { Id = "input"
                     Role = EvalTurnRole.User
                     Content = EvalProductionRedaction.text observation.Input }
                   { Id = "production-output"
                     Role = EvalTurnRole.Assistant
                     Content = EvalProductionRedaction.text observation.Output } ]
                 @ feedbackTurns)
                expectedBehavior
                labels
                (Map.ofList
                    [ "production-observation-id", observation.Id.ToString("D")
                      "production-execution-id", ExecutionId.serialize observation.ExecutionId
                      "sampling-policy", observation.SamplingPolicyId
                      "consent-record-sha256", observation.ConsentRecordSha256 ])
                (EvalReviewStatus.Approved approval)
                None
                (EvalCaseSource.ProductionDerived sanitization)

        evaluationCase,
        { CaseId = caseId
          Description = description
          ExpectedBehavior = expectedBehavior
          Labels = labels
          Reviewer = reviewer
          ReviewedAt = reviewedAt
          Rationale = rationale }

[<RequireQualifiedAccess>]
type EvalDriftDimension =
    | Model
    | Prompt
    | Retrieval
    | Cost
    | Latency
    | TrafficDistribution

[<RequireQualifiedAccess>]
type EvalDriftDirection =
    | IncreaseIsRegression
    | DecreaseIsRegression
    | AnyChange

[<RequireQualifiedAccess>]
type EvalDriftEscalation =
    | Notify
    | BlockRelease
    | Rollback

type EvalDriftRule =
    private
        { Dimension: EvalDriftDimension
          Direction: EvalDriftDirection
          WarningThreshold: decimal
          CriticalThreshold: decimal
          MinimumSamples: int
          WarningEscalation: EvalDriftEscalation
          CriticalEscalation: EvalDriftEscalation }

[<RequireQualifiedAccess>]
module EvalDriftRule =
    let private escalationRank =
        function
        | EvalDriftEscalation.Notify -> 0
        | EvalDriftEscalation.BlockRelease -> 1
        | EvalDriftEscalation.Rollback -> 2

    let create
        dimension
        direction
        warningThreshold
        criticalThreshold
        minimumSamples
        warningEscalation
        criticalEscalation
        =
        if warningThreshold <= 0m || criticalThreshold < warningThreshold then
            invalidArg (nameof warningThreshold) "Drift thresholds must be positive and ordered."

        if minimumSamples <= 0 then
            invalidArg (nameof minimumSamples) "Drift rules require a positive sample count."

        if escalationRank criticalEscalation < escalationRank warningEscalation then
            invalidArg (nameof criticalEscalation) "Critical drift escalation cannot be weaker than warning escalation."

        { Dimension = dimension
          Direction = direction
          WarningThreshold = warningThreshold
          CriticalThreshold = criticalThreshold
          MinimumSamples = minimumSamples
          WarningEscalation = warningEscalation
          CriticalEscalation = criticalEscalation }

[<RequireQualifiedAccess>]
type EvalDriftEvidence =
    | Scalar of baseline: decimal * current: decimal * baselineSamples: int * currentSamples: int
    | Distribution of baseline: Map<string, decimal> * current: Map<string, decimal> * sampleCount: int

type EvalDriftObservation =
    { Dimension: EvalDriftDimension
      Segment: Map<EvalSegmentDimension, string>
      Evidence: EvalDriftEvidence
      BaselineConfiguration: EvalExecutableConfiguration
      CurrentConfiguration: EvalExecutableConfiguration
      ObservedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
type EvalDriftSeverity =
    | Warning
    | Critical

type EvalDriftAlert =
    { Id: Guid
      Dimension: EvalDriftDimension
      Segment: Map<EvalSegmentDimension, string>
      Magnitude: decimal
      Severity: EvalDriftSeverity
      Escalation: EvalDriftEscalation
      BaselineConfiguration: EvalExecutableConfiguration
      CurrentConfiguration: EvalExecutableConfiguration
      ObservedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module EvalDriftDetection =
    let private validateDistribution fieldName values =
        if Map.isEmpty values || values |> Map.exists (fun _ value -> value < 0m) then
            invalidArg fieldName "Traffic distributions require non-negative segment values."

        let total = values |> Map.values |> Seq.sum

        if abs (total - 1m) > 0.000001m then
            invalidArg fieldName "Traffic distributions must sum to one."

    let private divergence baseline current =
        validateDistribution (nameof baseline) baseline
        validateDistribution (nameof current) current

        let keys =
            Set.union (baseline |> Map.keys |> Set.ofSeq) (current |> Map.keys |> Set.ofSeq)

        keys
        |> Seq.sumBy (fun key ->
            let left = baseline |> Map.tryFind key |> Option.defaultValue 0m
            let right = current |> Map.tryFind key |> Option.defaultValue 0m
            abs (left - right))
        |> fun total -> total / 2m

    let detect (rule: EvalDriftRule) (observation: EvalDriftObservation) =
        if rule.Dimension <> observation.Dimension then
            invalidArg (nameof rule) "Drift rule and observation dimensions must match."

        let magnitude, samples =
            match observation.Evidence with
            | EvalDriftEvidence.Scalar(baseline, current, baselineSamples, currentSamples) ->
                if baseline < 0m || current < 0m || baselineSamples <= 0 || currentSamples <= 0 then
                    invalidArg
                        (nameof observation)
                        "Scalar drift evidence requires non-negative values and positive samples."

                let change =
                    match rule.Direction with
                    | EvalDriftDirection.IncreaseIsRegression -> current - baseline
                    | EvalDriftDirection.DecreaseIsRegression -> baseline - current
                    | EvalDriftDirection.AnyChange -> abs (current - baseline)

                max 0m change, min baselineSamples currentSamples
            | EvalDriftEvidence.Distribution(baseline, current, sampleCount) ->
                if rule.Dimension <> EvalDriftDimension.TrafficDistribution then
                    invalidArg (nameof observation) "Distribution evidence is reserved for traffic drift."

                if sampleCount <= 0 then
                    invalidArg (nameof observation) "Traffic drift requires a positive sample count."

                divergence baseline current, sampleCount

        if samples < rule.MinimumSamples || magnitude < rule.WarningThreshold then
            None
        else
            let severity, escalation =
                if magnitude >= rule.CriticalThreshold then
                    EvalDriftSeverity.Critical, rule.CriticalEscalation
                else
                    EvalDriftSeverity.Warning, rule.WarningEscalation

            Some
                { Id = Guid.NewGuid()
                  Dimension = observation.Dimension
                  Segment = observation.Segment
                  Magnitude = magnitude
                  Severity = severity
                  Escalation = escalation
                  BaselineConfiguration = observation.BaselineConfiguration
                  CurrentConfiguration = observation.CurrentConfiguration
                  ObservedAt = observation.ObservedAt }

[<RequireQualifiedAccess>]
type EvalExperimentMode =
    | Shadow
    | Canary
    | AB

type EvalExperimentVariant =
    { Name: string
      AllocationBasisPoints: int
      Configuration: EvalExecutableConfiguration }

type EvalExperiment =
    private
        { Id: string
          Mode: EvalExperimentMode
          AssignmentSalt: string
          Variants: EvalExperimentVariant list }

type EvalExperimentAssignment =
    { ExperimentId: string
      Owner: AuthorizationIdentity
      CohortSha256: string
      Primary: EvalExperimentVariant
      Shadows: EvalExperimentVariant list }

[<RequireQualifiedAccess>]
module EvalExperiment =
    let create id mode assignmentSalt variants =
        if String.IsNullOrWhiteSpace id || id <> id.Trim() then
            invalidArg (nameof id) "Experiment identity must be canonical and non-blank."

        if String.IsNullOrWhiteSpace assignmentSalt then
            invalidArg (nameof assignmentSalt) "Experiment assignment requires a private salt."

        if List.length variants < 2 then
            invalidArg (nameof variants) "Experiments require at least two variants."

        if variants |> List.countBy _.Name |> List.exists (fun (_, count) -> count > 1) then
            invalidArg (nameof variants) "Experiment variant names must be unique."

        variants
        |> List.iter (fun variant ->
            if
                String.IsNullOrWhiteSpace variant.Name
                || variant.Name <> variant.Name.Trim()
                || variant.AllocationBasisPoints < 0
            then
                invalidArg (nameof variants) "Experiment variants require canonical names and non-negative allocation.")

        let total = variants |> List.sumBy _.AllocationBasisPoints

        match mode with
        | EvalExperimentMode.Shadow when
            variants.Head.AllocationBasisPoints <> 10_000
            || variants.Tail |> List.exists (fun variant -> variant.AllocationBasisPoints <> 0)
            ->
            invalidArg (nameof variants) "Shadow experiments allocate all traffic only to the primary variant."
        | EvalExperimentMode.Shadow -> ()
        | EvalExperimentMode.Canary
        | EvalExperimentMode.AB when total <> 10_000 ->
            invalidArg (nameof variants) "Canary and A/B allocations must total 10,000 basis points."
        | _ -> ()

        { Id = id
          Mode = mode
          AssignmentSalt = assignmentSalt
          Variants = variants }

    let assign authorization executionId experiment =
        let owner = AuthorizationScope.identity authorization

        let cohortSha256 =
            String.concat
                "|"
                [ experiment.AssignmentSalt
                  experiment.Id
                  AuthorizationIdentity.storageKey owner
                  ExecutionId.serialize executionId ]
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData
            |> Convert.ToHexString
            |> _.ToLowerInvariant()

        match experiment.Mode with
        | EvalExperimentMode.Shadow ->
            { ExperimentId = experiment.Id
              Owner = owner
              CohortSha256 = cohortSha256
              Primary = experiment.Variants.Head
              Shadows = experiment.Variants.Tail }
        | EvalExperimentMode.Canary
        | EvalExperimentMode.AB ->
            let bucket =
                cohortSha256.Substring(0, 8)
                |> fun value -> UInt32.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                |> fun value -> int (value % 10_000u)

            let mutable upper = 0

            let selected =
                experiment.Variants
                |> List.find (fun variant ->
                    upper <- upper + variant.AllocationBasisPoints
                    bucket < upper)

            { ExperimentId = experiment.Id
              Owner = owner
              CohortSha256 = cohortSha256
              Primary = selected
              Shadows = [] }

type EvalProductionIncident =
    private
        { Id: Guid
          Alert: EvalDriftAlert
          OpenedAt: DateTimeOffset
          ClosedAt: DateTimeOffset option
          RegressionCaseId: string option
          ClosedBy: AuthorizationIdentity option
          Resolution: string option }

[<RequireQualifiedAccess>]
module EvalProductionIncident =
    let openFromAlert openedAt alert =
        { Id = Guid.NewGuid()
          Alert = alert
          OpenedAt = openedAt
          ClosedAt = None
          RegressionCaseId = None
          ClosedBy = None
          Resolution = None }

    let close authorization closedAt resolution regressionCase incident =
        if incident.ClosedAt.IsSome then
            invalidOp "Production incident is already closed."

        if closedAt < incident.OpenedAt then
            invalidArg (nameof closedAt) "Production incidents cannot close before they open."

        if String.IsNullOrWhiteSpace resolution || resolution <> resolution.Trim() then
            invalidArg (nameof resolution) "Incident resolution must be canonical and non-blank."

        match EvalCase.source regressionCase, EvalCase.reviewStatus regressionCase with
        | EvalCaseSource.ProductionDerived _, EvalReviewStatus.Approved _ -> ()
        | _ -> invalidArg (nameof regressionCase) "Resolved incidents require an approved production regression case."

        { incident with
            ClosedAt = Some closedAt
            RegressionCaseId = Some(EvalCase.id regressionCase)
            ClosedBy = Some(AuthorizationScope.identity authorization)
            Resolution = Some resolution }

    let regressionCaseId value = value.RegressionCaseId
