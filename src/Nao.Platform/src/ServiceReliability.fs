namespace Nao.Platform

open System

[<RequireQualifiedAccess>]
type ServiceOperation =
    | AgentExecution
    | KnowledgeQuery
    | KnowledgeIngestion
    | AdministrativeMutation
    | RecoveryRestore

[<RequireQualifiedAccess>]
type ServiceIndicator =
    | Availability
    | Latency
    | Durability
    | Quality
    | Safety
    | Cost

[<RequireQualifiedAccess>]
type ReliabilityFailureDomain =
    | Platform
    | Provider
    | Policy
    | Storage
    | CustomerInput

[<RequireQualifiedAccess>]
type ServiceThreshold =
    | Minimum of float
    | Maximum of float

[<RequireQualifiedAccess>]
type SloExclusionCategory =
    | PlannedMaintenance
    | CustomerCancellation
    | InvalidCustomerInput

type SloExclusionPolicy =
    private
        { Id: string
          AllowedCategories: Set<SloExclusionCategory>
          Justification: string }

[<RequireQualifiedAccess>]
module SloExclusionPolicy =
    let create id allowedCategories justification =
        if String.IsNullOrWhiteSpace id || id <> id.Trim() then
            invalidArg (nameof id) "SLO exclusion policy identity must be canonical and non-blank."

        if Set.isEmpty allowedCategories then
            invalidArg (nameof allowedCategories) "SLO exclusion policy must name at least one category."

        if String.IsNullOrWhiteSpace justification || justification <> justification.Trim() then
            invalidArg (nameof justification) "SLO exclusion justification must be canonical and non-blank."

        { Id = id
          AllowedCategories = allowedCategories
          Justification = justification }

    let id value = value.Id
    let allowedCategories value = value.AllowedCategories
    let justification value = value.Justification

type ServiceLevelObjective =
    private
        { Id: string
          Profile: DeploymentProfile
          Operation: ServiceOperation
          Indicator: ServiceIndicator
          Threshold: ServiceThreshold
          ComplianceTarget: float
          Window: TimeSpan
          ExclusionPolicy: SloExclusionPolicy option }

[<RequireQualifiedAccess>]
module ServiceLevelObjective =
    let create id profile operation indicator threshold complianceTarget window exclusionPolicy =
        if String.IsNullOrWhiteSpace id || id <> id.Trim() then
            invalidArg (nameof id) "Service-level objective identity must be canonical and non-blank."

        if complianceTarget <= 0.0 || complianceTarget > 1.0 then
            invalidArg (nameof complianceTarget) "SLO compliance target must be greater than zero and at most one."

        if window <= TimeSpan.Zero then
            invalidArg (nameof window) "SLO window must be positive."

        match threshold with
        | ServiceThreshold.Minimum value
        | ServiceThreshold.Maximum value when Double.IsNaN value || Double.IsInfinity value ->
            invalidArg (nameof threshold) "SLO threshold must be finite."
        | _ -> ()

        { Id = id
          Profile = profile
          Operation = operation
          Indicator = indicator
          Threshold = threshold
          ComplianceTarget = complianceTarget
          Window = window
          ExclusionPolicy = exclusionPolicy }

    let id value = value.Id
    let profile value = value.Profile
    let operation value = value.Operation
    let indicator value = value.Indicator
    let threshold value = value.Threshold
    let complianceTarget value = value.ComplianceTarget
    let window value = value.Window
    let exclusionPolicy value = value.ExclusionPolicy
    let errorBudgetFraction value = 1.0 - value.ComplianceTarget

[<RequireQualifiedAccess>]
type ReliabilitySampleDisposition =
    | Eligible
    | Excluded of category: SloExclusionCategory * policyId: string * reason: string

type ReliabilitySample =
    private
        { ObjectiveId: string
          ObservedAt: DateTimeOffset
          Value: float
          FailureDomain: ReliabilityFailureDomain option
          Disposition: ReliabilitySampleDisposition }

[<RequireQualifiedAccess>]
module ReliabilitySample =
    let create objectiveId observedAt value failureDomain disposition =
        if String.IsNullOrWhiteSpace objectiveId || objectiveId <> objectiveId.Trim() then
            invalidArg (nameof objectiveId) "Reliability sample objective identity must be canonical and non-blank."

        if Double.IsNaN value || Double.IsInfinity value then
            invalidArg (nameof value) "Reliability sample value must be finite."

        match disposition with
        | ReliabilitySampleDisposition.Excluded(_, policyId, reason) ->
            if String.IsNullOrWhiteSpace policyId || policyId <> policyId.Trim() then
                invalidArg (nameof disposition) "Excluded samples require a canonical policy identity."

            if String.IsNullOrWhiteSpace reason || reason <> reason.Trim() then
                invalidArg (nameof disposition) "Excluded samples require a canonical reason."
        | ReliabilitySampleDisposition.Eligible -> ()

        { ObjectiveId = objectiveId
          ObservedAt = observedAt
          Value = value
          FailureDomain = failureDomain
          Disposition = disposition }

type ServiceLevelReport =
    private
        { ObjectiveId: string
          WindowStart: DateTimeOffset
          WindowEnd: DateTimeOffset
          EligibleCount: int
          CompliantCount: int
          ViolationCount: int
          ExcludedCount: int
          Compliance: float option
          ErrorBudget: float
          ErrorBudgetConsumed: float option
          ViolationsByDomain: Map<ReliabilityFailureDomain, int> }

[<RequireQualifiedAccess>]
module ServiceLevelReport =
    let objectiveId value = value.ObjectiveId
    let eligibleCount value = value.EligibleCount
    let compliantCount value = value.CompliantCount
    let violationCount value = value.ViolationCount
    let excludedCount value = value.ExcludedCount
    let compliance value = value.Compliance
    let errorBudget value = value.ErrorBudget
    let errorBudgetConsumed value = value.ErrorBudgetConsumed
    let violationsByDomain value = value.ViolationsByDomain

    let private isCompliant threshold value =
        match threshold with
        | ServiceThreshold.Minimum minimum -> value >= minimum
        | ServiceThreshold.Maximum maximum -> value <= maximum

    let evaluate (objective: ServiceLevelObjective) (windowEnd: DateTimeOffset) (samples: ReliabilitySample list) =
        let windowStart = windowEnd - ServiceLevelObjective.window objective

        let relevant =
            samples
            |> List.filter (fun sample ->
                sample.ObjectiveId = ServiceLevelObjective.id objective
                && sample.ObservedAt >= windowStart
                && sample.ObservedAt <= windowEnd)

        let exclusionPolicy = ServiceLevelObjective.exclusionPolicy objective

        for sample in relevant do
            match sample.Disposition, exclusionPolicy with
            | ReliabilitySampleDisposition.Eligible, _ -> ()
            | ReliabilitySampleDisposition.Excluded(category, policyId, _), Some policy when
                policyId = SloExclusionPolicy.id policy
                && Set.contains category (SloExclusionPolicy.allowedCategories policy)
                ->
                ()
            | ReliabilitySampleDisposition.Excluded _, _ ->
                invalidArg (nameof samples) "Reliability sample uses an unapproved SLO exclusion."

        let eligible =
            relevant
            |> List.filter (fun sample -> sample.Disposition = ReliabilitySampleDisposition.Eligible)

        let violations =
            eligible
            |> List.filter (fun sample -> not (isCompliant (ServiceLevelObjective.threshold objective) sample.Value))

        let eligibleCount = List.length eligible
        let violationCount = List.length violations
        let compliantCount = eligibleCount - violationCount

        let compliance =
            if eligibleCount = 0 then
                None
            else
                Some(float compliantCount / float eligibleCount)

        let errorBudget =
            ServiceLevelObjective.errorBudgetFraction objective * float eligibleCount

        let errorBudgetConsumed =
            if eligibleCount = 0 then
                None
            elif errorBudget = 0.0 then
                Some(if violationCount = 0 then 0.0 else Double.PositiveInfinity)
            else
                Some(float violationCount / errorBudget)

        if violations |> List.exists (fun sample -> Option.isNone sample.FailureDomain) then
            invalidArg (nameof samples) "Every SLO violation must identify its failure domain."

        let violationsByDomain =
            violations
            |> List.choose (fun sample -> sample.FailureDomain)
            |> List.countBy id
            |> Map.ofList

        { ObjectiveId = ServiceLevelObjective.id objective
          WindowStart = windowStart
          WindowEnd = windowEnd
          EligibleCount = eligibleCount
          CompliantCount = compliantCount
          ViolationCount = violationCount
          ExcludedCount = relevant.Length - eligibleCount
          Compliance = compliance
          ErrorBudget = errorBudget
          ErrorBudgetConsumed = errorBudgetConsumed
          ViolationsByDomain = violationsByDomain }

[<RequireQualifiedAccess>]
type ReliabilityAlertSeverity =
    | Warning
    | Critical

type ReliabilityAlertRule =
    private
        { Id: string
          Severity: ReliabilityAlertSeverity
          MinimumViolations: int
          ErrorBudgetConsumedThreshold: float
          DiagnosticsReference: string
          RunbookReference: string
          RollbackProcedure: string option }

[<RequireQualifiedAccess>]
module ReliabilityAlertRule =
    let create
        id
        severity
        minimumViolations
        errorBudgetConsumedThreshold
        diagnosticsReference
        runbookReference
        rollbackProcedure
        =
        let canonical argumentName value =
            if String.IsNullOrWhiteSpace value || value <> value.Trim() then
                invalidArg argumentName "Alert references and identities must be canonical and non-blank."

            value

        canonical (nameof id) id |> ignore
        canonical (nameof diagnosticsReference) diagnosticsReference |> ignore
        canonical (nameof runbookReference) runbookReference |> ignore

        if minimumViolations <= 0 then
            invalidArg (nameof minimumViolations) "Alert rules require at least one user-visible violation."

        if errorBudgetConsumedThreshold <= 0.0 then
            invalidArg
                (nameof errorBudgetConsumedThreshold)
                "Alert error-budget consumption threshold must be positive."

        rollbackProcedure
        |> Option.iter (canonical (nameof rollbackProcedure) >> ignore)

        { Id = id
          Severity = severity
          MinimumViolations = minimumViolations
          ErrorBudgetConsumedThreshold = errorBudgetConsumedThreshold
          DiagnosticsReference = diagnosticsReference
          RunbookReference = runbookReference
          RollbackProcedure = rollbackProcedure }

type ReliabilityAlert =
    private
        { RuleId: string
          Severity: ReliabilityAlertSeverity
          ObjectiveId: string
          ViolationCount: int
          ErrorBudgetConsumed: float
          DominantFailureDomain: ReliabilityFailureDomain option
          DiagnosticsReference: string
          RunbookReference: string
          RollbackProcedure: string option }

[<RequireQualifiedAccess>]
module ReliabilityAlert =
    let ruleId value = value.RuleId
    let severity value = value.Severity
    let objectiveId value = value.ObjectiveId
    let violationCount value = value.ViolationCount
    let dominantFailureDomain value = value.DominantFailureDomain
    let diagnosticsReference value = value.DiagnosticsReference
    let runbookReference value = value.RunbookReference
    let rollbackProcedure value = value.RollbackProcedure

    let evaluate rule report =
        if
            ServiceLevelReport.violationCount report < rule.MinimumViolations
            || (ServiceLevelReport.errorBudgetConsumed report
                |> Option.forall (fun consumed -> consumed < rule.ErrorBudgetConsumedThreshold))
        then
            None
        else
            let dominantDomain =
                ServiceLevelReport.violationsByDomain report
                |> Map.toList
                |> List.sortBy (fun (domain, count) -> -count, domain)
                |> List.tryHead
                |> Option.map fst

            Some
                { RuleId = rule.Id
                  Severity = rule.Severity
                  ObjectiveId = ServiceLevelReport.objectiveId report
                  ViolationCount = ServiceLevelReport.violationCount report
                  ErrorBudgetConsumed = ServiceLevelReport.errorBudgetConsumed report |> Option.get
                  DominantFailureDomain = dominantDomain
                  DiagnosticsReference = rule.DiagnosticsReference
                  RunbookReference = rule.RunbookReference
                  RollbackProcedure = rule.RollbackProcedure }

type IncidentReviewRecord =
    private
        { Id: string
          OccurredAt: DateTimeOffset
          FailureDomain: ReliabilityFailureDomain
          Summary: string
          CorrectiveActions: string list
          RegressionTests: string list }

[<RequireQualifiedAccess>]
module IncidentReviewRecord =
    let create id occurredAt failureDomain summary correctiveActions regressionTests =
        let canonical argumentName value =
            if String.IsNullOrWhiteSpace value || value <> value.Trim() then
                invalidArg argumentName "Incident review values must be canonical and non-blank."

            value

        canonical (nameof id) id |> ignore
        canonical (nameof summary) summary |> ignore

        if List.isEmpty correctiveActions then
            invalidArg (nameof correctiveActions) "Incident reviews require at least one corrective action."

        if List.isEmpty regressionTests then
            invalidArg (nameof regressionTests) "Incident reviews require at least one regression test."

        correctiveActions |> List.iter (canonical (nameof correctiveActions) >> ignore)
        regressionTests |> List.iter (canonical (nameof regressionTests) >> ignore)

        { Id = id
          OccurredAt = occurredAt
          FailureDomain = failureDomain
          Summary = summary
          CorrectiveActions = correctiveActions |> List.distinct
          RegressionTests = regressionTests |> List.distinct }

    let failureDomain value = value.FailureDomain
    let regressionTests value = value.RegressionTests

[<RequireQualifiedAccess>]
module ServiceObjectiveCatalog =
    let private profileTargets =
        function
        | DeploymentProfile.LocalDevelopment -> 0.90, TimeSpan.FromDays 1.0
        | DeploymentProfile.SingleNodeProduction -> 0.99, TimeSpan.FromDays 28.0
        | DeploymentProfile.ClusteredProduction -> 0.999, TimeSpan.FromDays 28.0
        | DeploymentProfile.IsolatedWorker -> 0.99, TimeSpan.FromDays 28.0

    let private threshold operation indicator =
        match indicator with
        | ServiceIndicator.Availability -> ServiceThreshold.Minimum 1.0
        | ServiceIndicator.Latency ->
            match operation with
            | ServiceOperation.AdministrativeMutation -> ServiceThreshold.Maximum 5000.0
            | ServiceOperation.KnowledgeIngestion
            | ServiceOperation.RecoveryRestore -> ServiceThreshold.Maximum 300000.0
            | _ -> ServiceThreshold.Maximum 30000.0
        | ServiceIndicator.Durability -> ServiceThreshold.Minimum 1.0
        | ServiceIndicator.Quality -> ServiceThreshold.Minimum 0.95
        | ServiceIndicator.Safety -> ServiceThreshold.Minimum 1.0
        | ServiceIndicator.Cost -> ServiceThreshold.Maximum 1.0

    let objectives profile operation exclusionPolicy =
        let target, window = profileTargets profile

        [ ServiceIndicator.Availability
          ServiceIndicator.Latency
          ServiceIndicator.Durability
          ServiceIndicator.Quality
          ServiceIndicator.Safety
          ServiceIndicator.Cost ]
        |> List.map (fun indicator ->
            let id =
                $"{profile.ToString().ToLowerInvariant()}.{operation.ToString().ToLowerInvariant()}.{indicator.ToString().ToLowerInvariant()}"

            ServiceLevelObjective.create
                id
                profile
                operation
                indicator
                (threshold operation indicator)
                target
                window
                exclusionPolicy)
