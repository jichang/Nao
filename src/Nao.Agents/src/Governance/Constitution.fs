namespace Nao.Agents

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type RuleCategory =
    | Safety
    | Privacy
    | Behavioral
    | Format
    | Domain of name: string

[<RequireQualifiedAccess>]
type RuleSeverity =
    | Advisory
    | Warning
    | Error
    | Critical

[<RequireQualifiedAccess>]
type ConstitutionRemediation =
    | Observe
    | Redact
    | Repair
    | Reject
    | Escalate
    | Quarantine

type ConstitutionContext =
    { Authorization: AuthorizationScope
      AgentId: string
      Output: string }

[<RequireQualifiedAccess>]
module ConstitutionContext =

    let create authorization agentId output =
        if String.IsNullOrWhiteSpace agentId || agentId <> agentId.Trim() then
            invalidArg
                (nameof agentId)
                "Constitution agent identity must be non-blank and have no surrounding whitespace."

        { Authorization = authorization
          AgentId = agentId
          Output = output }

type DeterministicConstitutionRule =
    private
        { Id: string
          Description: string
          Category: RuleCategory
          Priority: int
          Severity: RuleSeverity
          Scope: PolicyScope
          Remediation: ConstitutionRemediation
          EvaluatorId: string
          EvaluatorVersion: string
          DependencyVersions: Map<string, string>
          Violates: string -> bool }

type ProbabilisticJudgement = { Score: float; Rationale: string }

type ProbabilisticConstitutionRule =
    private
        { Id: string
          Description: string
          Category: RuleCategory
          Priority: int
          Severity: RuleSeverity
          Scope: PolicyScope
          Remediation: ConstitutionRemediation
          EvaluatorId: string
          EvaluatorVersion: string
          Model: string
          PromptVersion: string
          ViolationThreshold: float
          JudgeAsync: string -> CancellationToken -> Task<ProbabilisticJudgement> }

[<RequireQualifiedAccess>]
type ConstitutionRule =
    | Deterministic of DeterministicConstitutionRule
    | Probabilistic of ProbabilisticConstitutionRule

[<RequireQualifiedAccess>]
type ConstitutionEvaluatorEvidence =
    | Deterministic of dependencyVersions: Map<string, string>
    | Probabilistic of model: string * promptVersion: string * threshold: float * score: float * rationaleDigest: string

[<RequireQualifiedAccess>]
type ConstitutionEvaluationOutcome =
    | Passed
    | Violated

type ConstitutionEvaluationEvidence =
    { Id: Guid
      ConstitutionName: string
      ConstitutionVersion: string
      RuleId: string
      RuleCategory: RuleCategory
      RuleSeverity: RuleSeverity
      Scope: PolicyScopeKind
      Remediation: ConstitutionRemediation
      EvaluatorId: string
      EvaluatorVersion: string
      Outcome: ConstitutionEvaluationOutcome
      Evaluator: ConstitutionEvaluatorEvidence
      OutputDigest: string
      EvaluatedAt: DateTimeOffset }

type ConstitutionViolation =
    { RuleId: string
      RuleDescription: string
      Severity: RuleSeverity
      Remediation: ConstitutionRemediation
      Evidence: ConstitutionEvaluationEvidence }

type ConstitutionCheckResult =
    { Passed: bool
      Violations: ConstitutionViolation list
      Evaluations: ConstitutionEvaluationEvidence list }

type ConstitutionRemediationRequest =
    { Authorization: AuthorizationScope
      ExecutionId: ExecutionId
      AgentId: string
      Output: string
      Violations: ConstitutionViolation list
      RequestedAt: DateTimeOffset }

type ConstitutionRemediationBroker =
    private
        { RedactAsync: ConstitutionRemediationRequest -> CancellationToken -> Task<string>
          RepairAsync: ConstitutionRemediationRequest -> CancellationToken -> Task<string>
          EscalateAsync: ConstitutionRemediationRequest -> CancellationToken -> Task
          QuarantineAsync: ConstitutionRemediationRequest -> CancellationToken -> Task }

[<RequireQualifiedAccess>]
module ConstitutionRemediationBroker =

    let create redactAsync repairAsync escalateAsync quarantineAsync =
        { RedactAsync = redactAsync
          RepairAsync = repairAsync
          EscalateAsync = escalateAsync
          QuarantineAsync = quarantineAsync }

    let unavailable =
        let unavailable operation =
            invalidOp $"Constitution remediation '{operation}' requires a host-provided broker."

        create
            (fun _ _ -> Task.FromException<string>(unavailable "redact"))
            (fun _ _ -> Task.FromException<string>(unavailable "repair"))
            (fun _ _ -> Task.FromException(unavailable "escalate"))
            (fun _ _ -> Task.FromException(unavailable "quarantine"))

    let internal redactAsync request cancellationToken broker =
        broker.RedactAsync request cancellationToken

    let internal repairAsync request cancellationToken broker =
        broker.RepairAsync request cancellationToken

    let internal escalateAsync request cancellationToken broker =
        broker.EscalateAsync request cancellationToken

    let internal quarantineAsync request cancellationToken broker =
        broker.QuarantineAsync request cancellationToken

type Constitution =
    private
        { Name: string
          Version: string
          Rules: ConstitutionRule list
          Preamble: string option }

module private ConstitutionValidation =

    let required fieldName value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg fieldName "Value must be non-blank and have no surrounding whitespace."

        value

    let remediation severity remediation =
        match severity, remediation with
        | (RuleSeverity.Error | RuleSeverity.Critical), ConstitutionRemediation.Observe ->
            invalidArg
                (nameof remediation)
                "Error and critical constitution rules must request an enforcing remediation."
        | _ -> ()

[<RequireQualifiedAccess>]
module DeterministicConstitutionRule =

    let create
        id
        description
        category
        priority
        severity
        scope
        remediation
        evaluatorId
        evaluatorVersion
        dependencyVersions
        violates
        =
        let id = ConstitutionValidation.required (nameof id) id
        let description = ConstitutionValidation.required (nameof description) description
        let evaluatorId = ConstitutionValidation.required (nameof evaluatorId) evaluatorId

        let evaluatorVersion =
            ConstitutionValidation.required (nameof evaluatorVersion) evaluatorVersion

        ConstitutionValidation.remediation severity remediation

        for KeyValue(name, version) in dependencyVersions do
            ConstitutionValidation.required (nameof dependencyVersions) name |> ignore
            ConstitutionValidation.required (nameof dependencyVersions) version |> ignore

        { Id = id
          Description = description
          Category = category
          Priority = priority
          Severity = severity
          Scope = scope
          Remediation = remediation
          EvaluatorId = evaluatorId
          EvaluatorVersion = evaluatorVersion
          DependencyVersions = dependencyVersions
          Violates = violates }

[<RequireQualifiedAccess>]
module ProbabilisticConstitutionRule =

    let create
        id
        description
        category
        priority
        severity
        scope
        remediation
        evaluatorId
        evaluatorVersion
        model
        promptVersion
        violationThreshold
        judgeAsync
        =
        let id = ConstitutionValidation.required (nameof id) id
        let description = ConstitutionValidation.required (nameof description) description
        let evaluatorId = ConstitutionValidation.required (nameof evaluatorId) evaluatorId

        let evaluatorVersion =
            ConstitutionValidation.required (nameof evaluatorVersion) evaluatorVersion

        let model = ConstitutionValidation.required (nameof model) model

        let promptVersion =
            ConstitutionValidation.required (nameof promptVersion) promptVersion

        ConstitutionValidation.remediation severity remediation

        if
            Double.IsNaN violationThreshold
            || violationThreshold < 0.0
            || violationThreshold > 1.0
        then
            invalidArg (nameof violationThreshold) "Violation threshold must be between zero and one."

        { Id = id
          Description = description
          Category = category
          Priority = priority
          Severity = severity
          Scope = scope
          Remediation = remediation
          EvaluatorId = evaluatorId
          EvaluatorVersion = evaluatorVersion
          Model = model
          PromptVersion = promptVersion
          ViolationThreshold = violationThreshold
          JudgeAsync = judgeAsync }

[<RequireQualifiedAccess>]
module ConstitutionRule =

    let private fold deterministic probabilistic =
        function
        | ConstitutionRule.Deterministic rule -> deterministic rule
        | ConstitutionRule.Probabilistic rule -> probabilistic rule

    let id rule = fold _.Id _.Id rule
    let description rule = fold _.Description _.Description rule
    let category rule = fold _.Category _.Category rule
    let priority rule = fold _.Priority _.Priority rule
    let severity rule = fold _.Severity _.Severity rule
    let scope rule = fold _.Scope _.Scope rule
    let remediation rule = fold _.Remediation _.Remediation rule
    let evaluatorId rule = fold _.EvaluatorId _.EvaluatorId rule

    let evaluatorVersion rule =
        fold _.EvaluatorVersion _.EvaluatorVersion rule

module Constitution =

    let private digest (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private isBlockingSeverity =
        function
        | RuleSeverity.Error
        | RuleSeverity.Critical -> true
        | RuleSeverity.Advisory
        | RuleSeverity.Warning -> false

    let create name version : Constitution =
        let name = ConstitutionValidation.required (nameof name) name
        let version = ConstitutionValidation.required (nameof version) version

        { Name = name
          Version = version
          Rules = []
          Preamble = None }

    let withPreamble preamble constitution =
        let preamble = ConstitutionValidation.required (nameof preamble) preamble

        { constitution with
            Preamble = Some preamble }

    let addRule (rule: ConstitutionRule) (constitution: Constitution) : Constitution =
        if
            constitution.Rules
            |> List.exists (fun existing -> ConstitutionRule.id existing = ConstitutionRule.id rule)
        then
            invalidArg (nameof rule) $"Constitution rule identity '{ConstitutionRule.id rule}' is duplicated."

        { constitution with
            Rules = constitution.Rules @ [ rule ] |> List.sortByDescending ConstitutionRule.priority }

    let private matchingRules (context: ConstitutionContext) constitution =
        constitution.Rules
        |> List.filter (fun rule ->
            PolicyScope.matches context.Authorization context.AgentId None (ConstitutionRule.scope rule))

    let private evidence constitution (context: ConstitutionContext) rule outcome evaluator =
        { Id = Guid.NewGuid()
          ConstitutionName = constitution.Name
          ConstitutionVersion = constitution.Version
          RuleId = ConstitutionRule.id rule
          RuleCategory = ConstitutionRule.category rule
          RuleSeverity = ConstitutionRule.severity rule
          Scope = rule |> ConstitutionRule.scope |> PolicyScope.kind
          Remediation = ConstitutionRule.remediation rule
          EvaluatorId = ConstitutionRule.evaluatorId rule
          EvaluatorVersion = ConstitutionRule.evaluatorVersion rule
          Outcome = outcome
          Evaluator = evaluator
          OutputDigest = digest context.Output
          EvaluatedAt = DateTimeOffset.UtcNow }

    let private violation rule evidence =
        { RuleId = ConstitutionRule.id rule
          RuleDescription = ConstitutionRule.description rule
          Severity = ConstitutionRule.severity rule
          Remediation = ConstitutionRule.remediation rule
          Evidence = evidence }

    let private result violations evaluations =
        { Passed = List.isEmpty violations
          Violations = violations
          Evaluations = evaluations }

    let checkDeterministic (constitution: Constitution) (context: ConstitutionContext) : ConstitutionCheckResult =
        let violations = ResizeArray<ConstitutionViolation>()
        let evaluations = ResizeArray<ConstitutionEvaluationEvidence>()

        for rule in matchingRules context constitution do
            match rule with
            | ConstitutionRule.Probabilistic _ -> ()
            | ConstitutionRule.Deterministic deterministic ->
                let outcome =
                    if deterministic.Violates context.Output then
                        ConstitutionEvaluationOutcome.Violated
                    else
                        ConstitutionEvaluationOutcome.Passed

                let evaluation =
                    evidence
                        constitution
                        context
                        rule
                        outcome
                        (ConstitutionEvaluatorEvidence.Deterministic deterministic.DependencyVersions)

                evaluations.Add evaluation

                if outcome = ConstitutionEvaluationOutcome.Violated then
                    violations.Add(violation rule evaluation)

        result (List.ofSeq violations) (List.ofSeq evaluations)

    let judgeProbabilisticallyAsync constitution (context: ConstitutionContext) cancellationToken =
        task {
            let violations = ResizeArray<ConstitutionViolation>()
            let evaluations = ResizeArray<ConstitutionEvaluationEvidence>()

            for rule in matchingRules context constitution do
                match rule with
                | ConstitutionRule.Deterministic _ -> ()
                | ConstitutionRule.Probabilistic probabilistic ->
                    let! judgement = probabilistic.JudgeAsync context.Output cancellationToken

                    if Double.IsNaN judgement.Score || judgement.Score < 0.0 || judgement.Score > 1.0 then
                        invalidOp $"Constitution judge '{probabilistic.Id}' returned a score outside zero and one."

                    if String.IsNullOrWhiteSpace judgement.Rationale then
                        invalidOp $"Constitution judge '{probabilistic.Id}' returned no rationale."

                    let outcome =
                        if judgement.Score >= probabilistic.ViolationThreshold then
                            ConstitutionEvaluationOutcome.Violated
                        else
                            ConstitutionEvaluationOutcome.Passed

                    let evaluation =
                        evidence
                            constitution
                            context
                            rule
                            outcome
                            (ConstitutionEvaluatorEvidence.Probabilistic(
                                probabilistic.Model,
                                probabilistic.PromptVersion,
                                probabilistic.ViolationThreshold,
                                judgement.Score,
                                digest judgement.Rationale
                            ))

                    evaluations.Add evaluation

                    if outcome = ConstitutionEvaluationOutcome.Violated then
                        violations.Add(violation rule evaluation)

            return result (List.ofSeq violations) (List.ofSeq evaluations)
        }

    let evaluateAsync constitution context cancellationToken =
        task {
            let deterministic = checkDeterministic constitution context

            if
                deterministic.Violations
                |> List.exists (fun violation -> isBlockingSeverity violation.Severity)
            then
                return deterministic
            else
                let! probabilistic = judgeProbabilisticallyAsync constitution context cancellationToken

                return
                    result
                        (deterministic.Violations @ probabilistic.Violations)
                        (deterministic.Evaluations @ probabilistic.Evaluations)
        }

    let hasBlockingViolations (checkResult: ConstitutionCheckResult) =
        checkResult.Violations
        |> List.exists (fun violation -> isBlockingSeverity violation.Severity)

    let requiredRemediation (violations: ConstitutionViolation list) =
        let rank =
            function
            | ConstitutionRemediation.Observe -> 0
            | ConstitutionRemediation.Redact -> 1
            | ConstitutionRemediation.Repair -> 2
            | ConstitutionRemediation.Reject -> 3
            | ConstitutionRemediation.Escalate -> 4
            | ConstitutionRemediation.Quarantine -> 5

        violations
        |> List.map _.Remediation
        |> List.sortByDescending rank
        |> List.tryHead
        |> Option.defaultValue ConstitutionRemediation.Observe

    let renderForPrompt (constitution: Constitution) =
        let rules =
            constitution.Rules
            |> List.map (fun rule ->
                sprintf
                    "[%A/%A] %s"
                    (ConstitutionRule.severity rule)
                    (ConstitutionRule.remediation rule)
                    (ConstitutionRule.description rule))
            |> String.concat "\n"

        let preamble = constitution.Preamble |> Option.defaultValue ""
        sprintf "# Constitution: %s\n%s\n\n## Rules\n%s" constitution.Name preamble rules

    let noHarmRule =
        DeterministicConstitutionRule.create
            "safety-no-harm"
            "Do not generate content that could cause physical, emotional, or financial harm"
            RuleCategory.Safety
            100
            RuleSeverity.Error
            PolicyScope.platform
            ConstitutionRemediation.Reject
            "built-in.no-harm"
            "1"
            Map.empty
            (fun _ -> false)
        |> ConstitutionRule.Deterministic

    let noPrivateDataRule =
        let emailPattern =
            System.Text.RegularExpressions.Regex(
                @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant
            )

        let phonePattern =
            System.Text.RegularExpressions.Regex(
                @"\b\d{3}[-.]?\d{3}[-.]?\d{4}\b",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant
            )

        DeterministicConstitutionRule.create
            "privacy-no-pii"
            "Do not output personally identifiable information (PII) including emails, phone numbers, or addresses"
            RuleCategory.Privacy
            90
            RuleSeverity.Error
            PolicyScope.platform
            ConstitutionRemediation.Reject
            "built-in.pii-regex"
            "1"
            (Map [ "regex-patterns", "1" ])
            (fun content -> emailPattern.IsMatch(content) || phonePattern.IsMatch(content))
        |> ConstitutionRule.Deterministic
