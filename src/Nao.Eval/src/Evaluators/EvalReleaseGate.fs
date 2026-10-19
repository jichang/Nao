namespace Nao.Eval

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Nao.Agents

[<RequireQualifiedAccess>]
type EvalReleaseMetric =
    | Quality
    | Safety
    | SuccessRate
    | PolicyCompliance
    | RetrievalQuality
    | LatencyMilliseconds
    | TokenUse
    | CostUsd

[<RequireQualifiedAccess>]
type EvalMetricDirection =
    | HigherIsBetter
    | LowerIsBetter

[<RequireQualifiedAccess>]
module EvalReleaseMetric =
    let key =
        function
        | EvalReleaseMetric.Quality -> "quality"
        | EvalReleaseMetric.Safety -> "safety"
        | EvalReleaseMetric.SuccessRate -> "success-rate"
        | EvalReleaseMetric.PolicyCompliance -> "policy-compliance"
        | EvalReleaseMetric.RetrievalQuality -> "retrieval-quality"
        | EvalReleaseMetric.LatencyMilliseconds -> "latency-ms"
        | EvalReleaseMetric.TokenUse -> "token-use"
        | EvalReleaseMetric.CostUsd -> "cost-usd"

    let direction =
        function
        | EvalReleaseMetric.Quality
        | EvalReleaseMetric.Safety
        | EvalReleaseMetric.SuccessRate
        | EvalReleaseMetric.PolicyCompliance
        | EvalReleaseMetric.RetrievalQuality -> EvalMetricDirection.HigherIsBetter
        | EvalReleaseMetric.LatencyMilliseconds
        | EvalReleaseMetric.TokenUse
        | EvalReleaseMetric.CostUsd -> EvalMetricDirection.LowerIsBetter

    let isRatio =
        function
        | EvalReleaseMetric.Quality
        | EvalReleaseMetric.Safety
        | EvalReleaseMetric.SuccessRate
        | EvalReleaseMetric.PolicyCompliance
        | EvalReleaseMetric.RetrievalQuality -> true
        | EvalReleaseMetric.LatencyMilliseconds
        | EvalReleaseMetric.TokenUse
        | EvalReleaseMetric.CostUsd -> false

[<RequireQualifiedAccess>]
type EvalSegmentDimension =
    | Scenario
    | Provider
    | Model
    | Language
    | Source
    | TenantPolicy

[<RequireQualifiedAccess>]
module EvalSegmentDimension =
    let key =
        function
        | EvalSegmentDimension.Scenario -> "scenario"
        | EvalSegmentDimension.Provider -> "provider"
        | EvalSegmentDimension.Model -> "model"
        | EvalSegmentDimension.Language -> "language"
        | EvalSegmentDimension.Source -> "source"
        | EvalSegmentDimension.TenantPolicy -> "tenant-policy"

[<RequireQualifiedAccess>]
type EvalMetricScope =
    | Overall
    | Segment of dimension: EvalSegmentDimension * value: string

type EvalReleaseMetricKey =
    private
        { Metric: EvalReleaseMetric
          Scope: EvalMetricScope }

[<RequireQualifiedAccess>]
module EvalReleaseMetricKey =
    let overall metric =
        { Metric = metric
          Scope = EvalMetricScope.Overall }

    let segment dimension value metric =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg (nameof value) "Evaluation segment values must be canonical and non-blank."

        { Metric = metric
          Scope = EvalMetricScope.Segment(dimension, value) }

    let metric value = value.Metric
    let scope value = value.Scope

    let key value =
        match value.Scope with
        | EvalMetricScope.Overall -> sprintf "overall:%s" (EvalReleaseMetric.key value.Metric)
        | EvalMetricScope.Segment(dimension, segment) ->
            sprintf "segment:%s:%s:%s" (EvalSegmentDimension.key dimension) segment (EvalReleaseMetric.key value.Metric)

type EvalMetricMeasurement =
    private
        { Value: decimal
          SampleCount: int
          Successes: int option
          StandardDeviation: decimal option }

[<RequireQualifiedAccess>]
module EvalMetricMeasurement =
    let create value sampleCount successes standardDeviation =
        if sampleCount <= 0 then
            invalidArg (nameof sampleCount) "Metric measurements require a positive sample count."

        if value < 0m then
            invalidArg (nameof value) "Metric measurements cannot be negative."

        successes
        |> Option.iter (fun count ->
            if count < 0 || count > sampleCount then
                invalidArg (nameof successes) "Metric successes must be within the sample count.")

        standardDeviation
        |> Option.iter (fun deviation ->
            if deviation < 0m then
                invalidArg (nameof standardDeviation) "Metric standard deviation cannot be negative.")

        { Value = value
          SampleCount = sampleCount
          Successes = successes
          StandardDeviation = standardDeviation }

    let proportion successes sampleCount =
        create (decimal successes / decimal sampleCount) sampleCount (Some successes) None

    let value measurement = measurement.Value
    let sampleCount measurement = measurement.SampleCount
    let successes measurement = measurement.Successes
    let standardDeviation measurement = measurement.StandardDeviation

[<RequireQualifiedAccess>]
type EvalFailureClass =
    | Correctness
    | Infrastructure

type EvalReleaseFailure =
    { CaseId: string
      Classification: EvalFailureClass
      Reason: string }

type EvalReleaseSnapshot =
    private
        { ReportId: Guid
          DatasetId: Guid
          DatasetRevision: int
          DatasetContentSha256: string
          Configuration: EvalExecutableConfiguration
          Metrics: Map<EvalReleaseMetricKey, EvalMetricMeasurement>
          Failures: EvalReleaseFailure list
          RecordedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module EvalReleaseSnapshot =
    let create reportId datasetId datasetRevision datasetContentSha256 configuration metrics failures recordedAt =
        if reportId = Guid.Empty || datasetId = Guid.Empty then
            invalidArg (nameof reportId) "Release snapshots require report and dataset identities."

        if datasetRevision <= 0 then
            invalidArg (nameof datasetRevision) "Release snapshot dataset revisions must be positive."

        if String.IsNullOrWhiteSpace datasetContentSha256 then
            invalidArg (nameof datasetContentSha256) "Release snapshots require dataset content identity."

        let metricValues = Seq.toList metrics

        if List.isEmpty metricValues then
            invalidArg (nameof metrics) "Release snapshots require at least one metric."

        if metricValues |> List.countBy fst |> List.exists (fun (_, count) -> count > 1) then
            invalidArg (nameof metrics) "Release snapshot metric keys must be unique."

        let metrics = Map.ofList metricValues

        metrics
        |> Map.iter (fun key measurement ->
            if EvalReleaseMetric.isRatio key.Metric && measurement.Value > 1m then
                invalidArg
                    (nameof metrics)
                    "Quality, safety, success, policy, and retrieval metrics cannot exceed one."

            measurement.Successes
            |> Option.iter (fun successes ->
                let observed = decimal successes / decimal measurement.SampleCount

                if observed <> measurement.Value then
                    invalidArg (nameof metrics) "Proportion values must equal successes divided by sample count."))

        let duplicateFailures =
            failures
            |> List.countBy (fun failure -> failure.CaseId, failure.Classification)
            |> List.exists (fun (_, count) -> count > 1)

        if duplicateFailures then
            invalidArg (nameof failures) "Release failure identities must be unique within each classification."

        failures
        |> List.iter (fun failure ->
            if
                String.IsNullOrWhiteSpace failure.CaseId
                || String.IsNullOrWhiteSpace failure.Reason
            then
                invalidArg (nameof failures) "Release failures require case identity and reason.")

        { ReportId = reportId
          DatasetId = datasetId
          DatasetRevision = datasetRevision
          DatasetContentSha256 = datasetContentSha256
          Configuration = configuration
          Metrics = metrics
          Failures = failures
          RecordedAt = recordedAt }

    let reportId value = value.ReportId
    let datasetId value = value.DatasetId
    let datasetRevision value = value.DatasetRevision
    let datasetContentSha256 value = value.DatasetContentSha256
    let configuration value = value.Configuration
    let metrics value = value.Metrics
    let failures value = value.Failures
    let recordedAt value = value.RecordedAt

    let correctnessFailures value =
        value.Failures
        |> List.filter (fun failure -> failure.Classification = EvalFailureClass.Correctness)

    let infrastructureFailures value =
        value.Failures
        |> List.filter (fun failure -> failure.Classification = EvalFailureClass.Infrastructure)

type EvalMetricThreshold =
    private
        { Key: EvalReleaseMetricKey
          AbsoluteThreshold: decimal
          MaximumRegression: decimal
          MinimumSamples: int
          ConfidenceLevel: float }

[<RequireQualifiedAccess>]
module EvalMetricThreshold =
    let create key absoluteThreshold maximumRegression minimumSamples confidenceLevel =
        if absoluteThreshold < 0m || maximumRegression < 0m then
            invalidArg (nameof absoluteThreshold) "Release thresholds cannot be negative."

        if minimumSamples <= 0 then
            invalidArg (nameof minimumSamples) "Release thresholds require a positive sample count."

        if confidenceLevel <= 0.0 || confidenceLevel >= 1.0 then
            invalidArg (nameof confidenceLevel) "Confidence level must be strictly between zero and one."

        if
            [ 0.90; 0.95; 0.99 ]
            |> List.exists (fun supported -> abs (confidenceLevel - supported) < 0.000001)
            |> not
        then
            invalidArg (nameof confidenceLevel) "Supported confidence levels are 0.90, 0.95, and 0.99."

        { Key = key
          AbsoluteThreshold = absoluteThreshold
          MaximumRegression = maximumRegression
          MinimumSamples = minimumSamples
          ConfidenceLevel = confidenceLevel }

    let key value = value.Key
    let absoluteThreshold value = value.AbsoluteThreshold
    let maximumRegression value = value.MaximumRegression
    let minimumSamples value = value.MinimumSamples
    let confidenceLevel value = value.ConfidenceLevel

type EvalReleaseGateConfig =
    private
        { Thresholds: EvalMetricThreshold list
          ConfigurationSha256: string }

[<RequireQualifiedAccess>]
module EvalReleaseGateConfig =
    let create thresholds =
        if List.isEmpty thresholds then
            invalidArg (nameof thresholds) "Release gates require at least one threshold."

        if
            thresholds
            |> List.countBy EvalMetricThreshold.key
            |> List.exists (fun (_, count) -> count > 1)
        then
            invalidArg (nameof thresholds) "Release-gate metric keys must be unique."

        let thresholds =
            thresholds |> List.sortBy (EvalMetricThreshold.key >> EvalReleaseMetricKey.key)

        let canonical = StringBuilder()

        for threshold in thresholds do
            canonical
                .Append(threshold |> EvalMetricThreshold.key |> EvalReleaseMetricKey.key)
                .Append('|')
                .Append((EvalMetricThreshold.absoluteThreshold threshold).ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append((EvalMetricThreshold.maximumRegression threshold).ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(EvalMetricThreshold.minimumSamples threshold)
                .Append('|')
                .Append((EvalMetricThreshold.confidenceLevel threshold).ToString(CultureInfo.InvariantCulture))
                .Append('|')
            |> ignore

        let hash =
            canonical
            |> _.ToString()
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData
            |> Convert.ToHexString
            |> _.ToLowerInvariant()

        { Thresholds = thresholds
          ConfigurationSha256 = hash }

    let thresholds value = value.Thresholds
    let configurationSha256 value = value.ConfigurationSha256

type EvalConfidenceInterval =
    { Lower: decimal
      Upper: decimal
      ConfidenceLevel: float }

[<RequireQualifiedAccess>]
module private EvalStatistics =
    let zScore confidenceLevel =
        if abs (confidenceLevel - 0.90) < 0.000001 then
            1.6448536269514722
        elif abs (confidenceLevel - 0.95) < 0.000001 then
            1.959963984540054
        elif abs (confidenceLevel - 0.99) < 0.000001 then
            2.5758293035489004
        else
            invalidArg (nameof confidenceLevel) "Supported confidence levels are 0.90, 0.95, and 0.99."

    let interval confidenceLevel measurement =
        let z = zScore confidenceLevel
        let n = float measurement.SampleCount

        match measurement.Successes, measurement.StandardDeviation with
        | Some successes, _ ->
            let p = float successes / n
            let denominator = 1.0 + z * z / n
            let centre = (p + z * z / (2.0 * n)) / denominator
            let margin = z * sqrt ((p * (1.0 - p) + z * z / (4.0 * n)) / n) / denominator

            { Lower = decimal (max 0.0 (centre - margin))
              Upper = decimal (min 1.0 (centre + margin))
              ConfidenceLevel = confidenceLevel }
        | None, Some deviation ->
            let margin = decimal z * deviation / decimal (sqrt n)

            { Lower = max 0m (measurement.Value - margin)
              Upper = measurement.Value + margin
              ConfidenceLevel = confidenceLevel }
        | None, None ->
            invalidArg (nameof measurement) "Confidence evaluation requires proportion or dispersion evidence."

[<RequireQualifiedAccess>]
type EvalGateFailure =
    | MissingMetric
    | InsufficientSamples
    | BelowAbsoluteThreshold
    | AboveAbsoluteThreshold
    | RegressedBeyondTolerance

type EvalMetricGateCheck =
    { Key: EvalReleaseMetricKey
      Direction: EvalMetricDirection
      Measurement: EvalMetricMeasurement option
      ConfidenceInterval: EvalConfidenceInterval option
      BaselineValue: decimal option
      Failures: EvalGateFailure list }

type EvalReleaseGateResult =
    { Candidate: EvalReleaseSnapshot
      Baseline: EvalReleaseBaseline option
      ConfigurationSha256: string
      Checks: EvalMetricGateCheck list
      CorrectnessFailures: EvalReleaseFailure list
      InfrastructureFailures: EvalReleaseFailure list }

and EvalReleaseBaseline =
    private
        { SchemaVersion: int
          Id: Guid
          Snapshot: EvalReleaseSnapshot
          GateConfigurationSha256: string
          PreviousBaselineId: Guid option
          RetainedFailedCaseIds: Set<string>
          PromotedBy: AuthorizationIdentity
          Justification: string
          PromotedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module EvalReleaseGateResult =
    let passed value =
        value.InfrastructureFailures.IsEmpty
        && value.CorrectnessFailures.IsEmpty
        && value.Checks |> List.forall (fun check -> check.Failures.IsEmpty)

[<RequireQualifiedAccess>]
module EvalReleaseGate =
    let evaluate
        (config: EvalReleaseGateConfig)
        (baseline: EvalReleaseBaseline option)
        (candidate: EvalReleaseSnapshot)
        =
        baseline
        |> Option.iter (fun value ->
            if value.GateConfigurationSha256 <> config.ConfigurationSha256 then
                invalidArg (nameof baseline) "Release baselines must use the active gate configuration."

            if value.Snapshot.DatasetContentSha256 <> candidate.DatasetContentSha256 then
                invalidArg (nameof baseline) "Release baselines must use the candidate dataset content hash."

            if value.Snapshot.Configuration <> candidate.Configuration then
                invalidArg (nameof baseline) "Release baselines must use the candidate executable configuration.")

        let baselineMetrics = baseline |> Option.map (fun value -> value.Snapshot.Metrics)

        let checks =
            config.Thresholds
            |> List.map (fun (threshold: EvalMetricThreshold) ->
                let key = threshold.Key
                let direction = key.Metric |> EvalReleaseMetric.direction
                let measurement = candidate.Metrics |> Map.tryFind key

                let baselineValue =
                    baselineMetrics |> Option.bind (Map.tryFind key) |> Option.map _.Value

                let interval =
                    measurement |> Option.map (EvalStatistics.interval threshold.ConfidenceLevel)

                let failures =
                    match measurement, interval with
                    | None, _ -> [ EvalGateFailure.MissingMetric ]
                    | Some value, Some confidence ->
                        [ if value.SampleCount < threshold.MinimumSamples then
                              yield EvalGateFailure.InsufficientSamples

                          match direction with
                          | EvalMetricDirection.HigherIsBetter when confidence.Lower < threshold.AbsoluteThreshold ->
                              yield EvalGateFailure.BelowAbsoluteThreshold
                          | EvalMetricDirection.LowerIsBetter when confidence.Upper > threshold.AbsoluteThreshold ->
                              yield EvalGateFailure.AboveAbsoluteThreshold
                          | _ -> ()

                          match direction, baselineValue with
                          | EvalMetricDirection.HigherIsBetter, Some baselineValue when
                              baselineValue - value.Value > threshold.MaximumRegression
                              ->
                              yield EvalGateFailure.RegressedBeyondTolerance
                          | EvalMetricDirection.LowerIsBetter, Some baselineValue when
                              value.Value - baselineValue > threshold.MaximumRegression
                              ->
                              yield EvalGateFailure.RegressedBeyondTolerance
                          | _ -> () ]
                    | Some _, None -> invalidOp "Metric confidence interval was not produced."

                { Key = key
                  Direction = direction
                  Measurement = measurement
                  ConfidenceInterval = interval
                  BaselineValue = baselineValue
                  Failures = failures })

        { Candidate = candidate
          Baseline = baseline
          ConfigurationSha256 = config.ConfigurationSha256
          Checks = checks
          CorrectnessFailures = EvalReleaseSnapshot.correctnessFailures candidate
          InfrastructureFailures = EvalReleaseSnapshot.infrastructureFailures candidate }

[<RequireQualifiedAccess>]
module EvalReleaseBaseline =
    [<Literal>]
    let CurrentSchemaVersion = 1

    let id value = value.Id
    let snapshot value = value.Snapshot
    let previousBaselineId value = value.PreviousBaselineId
    let retainedFailedCaseIds value = value.RetainedFailedCaseIds
    let promotedBy value = value.PromotedBy
    let justification value = value.Justification
    let promotedAt value = value.PromotedAt

[<RequireQualifiedAccess>]
type EvalBaselineReviewDecision =
    | Approved
    | Rejected

type EvalBaselineProposal =
    { Id: Guid
      GateResult: EvalReleaseGateResult
      CreatedBy: AuthorizationIdentity
      CreatedAt: DateTimeOffset
      Review: (AuthorizationIdentity * DateTimeOffset * EvalBaselineReviewDecision * string) option }

[<RequireQualifiedAccess>]
type EvalBaselineAuditAction =
    | Proposed
    | Reviewed of EvalBaselineReviewDecision
    | Promoted
    | RolledBack

type EvalBaselineAuditEntry =
    { Id: Guid
      ProposalId: Guid option
      BaselineId: Guid option
      Actor: AuthorizationIdentity
      Action: EvalBaselineAuditAction
      Reason: string
      RecordedAt: DateTimeOffset }

type EvalBaselineRegistry =
    private
        { ActiveBaselineId: Guid option
          Baselines: Map<Guid, EvalReleaseBaseline>
          Proposals: Map<Guid, EvalBaselineProposal>
          RetainedFailedCaseIds: Set<string>
          Audit: EvalBaselineAuditEntry list }

[<RequireQualifiedAccess>]
module EvalBaselineRegistry =
    let empty =
        { ActiveBaselineId = None
          Baselines = Map.empty
          Proposals = Map.empty
          RetainedFailedCaseIds = Set.empty
          Audit = [] }

    let private requiredReason name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name "Baseline actions require a canonical non-blank reason."

        value

    let private append actor proposalId baselineId action reason recordedAt registry =
        let entry =
            { Id = Guid.NewGuid()
              ProposalId = proposalId
              BaselineId = baselineId
              Actor = actor
              Action = action
              Reason = reason
              RecordedAt = recordedAt }

        { registry with
            Audit = registry.Audit @ [ entry ] }

    let propose authorization reason recordedAt gateResult (registry: EvalBaselineRegistry) =
        let reason = requiredReason (nameof reason) reason
        let actor = AuthorizationScope.identity authorization
        let proposalId = Guid.NewGuid()

        let proposal =
            { Id = proposalId
              GateResult = gateResult
              CreatedBy = actor
              CreatedAt = recordedAt
              Review = None }

        let updated =
            { registry with
                Proposals = registry.Proposals |> Map.add proposalId proposal
                RetainedFailedCaseIds =
                    gateResult.Candidate.Failures
                    |> List.map _.CaseId
                    |> Set.ofList
                    |> Set.union registry.RetainedFailedCaseIds }
            |> append actor (Some proposalId) None EvalBaselineAuditAction.Proposed reason recordedAt

        proposalId, updated

    let review authorization decision reason recordedAt proposalId registry =
        let reason = requiredReason (nameof reason) reason
        let actor = AuthorizationScope.identity authorization

        let proposal =
            registry.Proposals
            |> Map.tryFind proposalId
            |> Option.defaultWith (fun () -> invalidArg (nameof proposalId) "Unknown baseline proposal.")

        if proposal.CreatedBy = actor then
            invalidArg (nameof authorization) "Baseline review requires an independent reviewer."

        if proposal.Review.IsSome then
            invalidOp "Baseline proposal has already been reviewed."

        let reviewed =
            { proposal with
                Review = Some(actor, recordedAt, decision, reason) }

        { registry with
            Proposals = registry.Proposals |> Map.add proposalId reviewed }
        |> append actor (Some proposalId) None (EvalBaselineAuditAction.Reviewed decision) reason recordedAt

    let promote authorization reason recordedAt proposalId registry =
        let reason = requiredReason (nameof reason) reason
        let actor = AuthorizationScope.identity authorization

        let proposal =
            registry.Proposals
            |> Map.tryFind proposalId
            |> Option.defaultWith (fun () -> invalidArg (nameof proposalId) "Unknown baseline proposal.")

        match proposal.Review with
        | Some(_, _, EvalBaselineReviewDecision.Approved, _) -> ()
        | _ -> invalidOp "Only independently approved baseline proposals can be promoted."

        if not (EvalReleaseGateResult.passed proposal.GateResult) then
            invalidOp "Failed release-gate evidence cannot be promoted."

        let previous =
            registry.ActiveBaselineId
            |> Option.bind (fun id -> registry.Baselines |> Map.tryFind id)

        let candidateFailures =
            proposal.GateResult.Candidate.Failures |> List.map _.CaseId |> Set.ofList

        let baseline =
            { SchemaVersion = EvalReleaseBaseline.CurrentSchemaVersion
              Id = Guid.NewGuid()
              Snapshot = proposal.GateResult.Candidate
              GateConfigurationSha256 = proposal.GateResult.ConfigurationSha256
              PreviousBaselineId = previous |> Option.map _.Id
              RetainedFailedCaseIds = Set.union registry.RetainedFailedCaseIds candidateFailures
              PromotedBy = actor
              Justification = reason
              PromotedAt = recordedAt }

        baseline,
        ({ registry with
            ActiveBaselineId = Some baseline.Id
            Baselines = registry.Baselines |> Map.add baseline.Id baseline }
         |> append actor (Some proposalId) (Some baseline.Id) EvalBaselineAuditAction.Promoted reason recordedAt)

    let rollback authorization reason recordedAt targetBaselineId registry =
        let reason = requiredReason (nameof reason) reason
        let actor = AuthorizationScope.identity authorization

        if not (registry.Baselines |> Map.containsKey targetBaselineId) then
            invalidArg (nameof targetBaselineId) "Rollback target must be a retained baseline."

        { registry with
            ActiveBaselineId = Some targetBaselineId }
        |> append actor None (Some targetBaselineId) EvalBaselineAuditAction.RolledBack reason recordedAt

    let active registry =
        registry.ActiveBaselineId
        |> Option.bind (fun id -> registry.Baselines |> Map.tryFind id)

    let baselines registry =
        registry.Baselines |> Map.values |> Seq.toList

    let retainedFailedCaseIds registry = registry.RetainedFailedCaseIds
    let audit registry = registry.Audit

[<RequireQualifiedAccess>]
type EvalSuiteProfile =
    | PullRequestFast
    | ScheduledFull

[<RequireQualifiedAccess>]
module EvalSuiteProfile =
    let testCategory =
        function
        | EvalSuiteProfile.PullRequestFast -> "EvalFast"
        | EvalSuiteProfile.ScheduledFull -> "EvalFull"

[<RequireQualifiedAccess>]
module EvalReleaseArtifact =
    let serialize gateResult =
        JsonSerializer.Serialize(
            {| passed = EvalReleaseGateResult.passed gateResult
               configurationSha256 = gateResult.ConfigurationSha256
               correctnessFailures = gateResult.CorrectnessFailures
               infrastructureFailures = gateResult.InfrastructureFailures
               checks =
                gateResult.Checks
                |> List.map (fun check ->
                    {| metric = EvalReleaseMetricKey.key check.Key
                       direction = string check.Direction
                       value = check.Measurement |> Option.map EvalMetricMeasurement.value
                       samples = check.Measurement |> Option.map EvalMetricMeasurement.sampleCount
                       confidence = check.ConfidenceInterval
                       baseline = check.BaselineValue
                       failures = check.Failures |> List.map string |}) |},
            JsonSerializerOptions(WriteIndented = true)
        )
