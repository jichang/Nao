namespace Nao.Agents

/// Unified error type for the ETCLOVG harness, covering all failure modes
[<RequireQualifiedAccess>]
type HarnessError =
    /// Agent execution permissions denied
    | PermissionDenied
    /// Policy engine blocked execution
    | PolicyBlocked of violations: string list
    /// Readiness checks failed (prerequisites not met)
    | NotReady of reasons: string list
    /// Lifecycle initialization failed
    | InitializationFailed of message: string
    /// Resource limit exceeded during execution
    | ResourceLimitExceeded of limit: LimitExceeded
    /// Agent output violates constitution rules
    | ConstitutionViolation of ruleIds: string list
    /// Unexpected error during execution
    | ExecutionFailed of message: string

    /// Get a human-readable error message
    member this.Message =
        match this with
        | PermissionDenied -> "Permission denied"
        | PolicyBlocked violations -> sprintf "Blocked by policy: %s" (violations |> String.concat "; ")
        | NotReady reasons -> sprintf "Not ready: %s" (reasons |> String.concat "; ")
        | InitializationFailed msg -> msg
        | ResourceLimitExceeded limit -> sprintf "Resource limit exceeded: %A" limit
        | ConstitutionViolation ruleIds -> sprintf "Output violates constitution: %s" (ruleIds |> String.concat ", ")
        | ExecutionFailed msg -> msg

    member this.Category =
        match this with
        | PermissionDenied
        | PolicyBlocked _ -> PlatformErrorCategory.PermissionDenied
        | NotReady _
        | InitializationFailed _ -> PlatformErrorCategory.NotReady
        | ResourceLimitExceeded _ -> PlatformErrorCategory.ResourceExhausted
        | ConstitutionViolation _ -> PlatformErrorCategory.InvalidOutput
        | ExecutionFailed _ -> PlatformErrorCategory.InternalFailure

    member this.Retryable =
        match this with
        | NotReady _
        | InitializationFailed _ -> true
        | PermissionDenied
        | PolicyBlocked _
        | ResourceLimitExceeded _
        | ConstitutionViolation _
        | ExecutionFailed _ -> false

    member this.ToPlatformFailure(correlationId) =
        PlatformFailure.create this.Category this.Message this.Retryable correlationId

/// Final state of a harness execution.
[<RequireQualifiedAccess>]
type ExecutionTerminalStatus =
    | Succeeded
    | Failed of error: HarnessError
    | Cancelled
    | TimedOut
    | Denied of error: HarnessError
    | LimitExceeded of limit: LimitExceeded
    | Indeterminate of message: string

    member this.ToPlatformFailure(correlationId) =
        match this with
        | Succeeded -> invalidOp "A successful execution has no platform failure."
        | Failed error
        | Denied error -> error.ToPlatformFailure correlationId
        | LimitExceeded limit -> (HarnessError.ResourceLimitExceeded limit).ToPlatformFailure correlationId
        | Cancelled -> PlatformFailure.create PlatformErrorCategory.Cancelled "Execution cancelled" false correlationId
        | TimedOut ->
            PlatformFailure.create PlatformErrorCategory.TransientDependency "Execution timed out" true correlationId
        | Indeterminate message ->
            PlatformFailure.create PlatformErrorCategory.InternalFailure message false correlationId

/// User-visible values produced by an execution.
type ExecutionOutputs =
    { Response: string option
      Artifacts: Artifact list }

/// Verifiable records collected while an execution runs.
type ExecutionEvidence =
    { Trace: ExecutionTrace option
      Metrics: ExecutionMetrics option
      Judgement: JudgementResult option
      Regression: RegressionResult option
      AuditEntries: int }

[<RequireQualifiedAccess>]
type ConstitutionDisposition =
    | NotEvaluated
    | Released
    | Redacted
    | Repaired
    | Rejected
    | Escalated
    | Quarantined

/// Governance outcomes applied to an execution.
type ExecutionPolicyDecisions =
    { PolicyViolations: PolicyViolation list
      PolicyEvaluations: PolicyEvaluationEvidence list
      ConstitutionViolations: ConstitutionViolation list
      ConstitutionDisposition: ConstitutionDisposition }

/// Immutable outcome of one governed harness execution.
type ExecutionResult =
    { Correlation: CorrelationContext
      Status: ExecutionTerminalStatus
      Outputs: ExecutionOutputs
      Usage: ResourceUsage
      Evidence: ExecutionEvidence
      PolicyDecisions: ExecutionPolicyDecisions }

[<RequireQualifiedAccess>]
module ExecutionResult =
    let private redact = SecretRedaction.redact

    let private harnessError =
        function
        | HarnessError.PolicyBlocked violations -> HarnessError.PolicyBlocked(List.map redact violations)
        | HarnessError.NotReady reasons -> HarnessError.NotReady(List.map redact reasons)
        | HarnessError.InitializationFailed message -> HarnessError.InitializationFailed(redact message)
        | HarnessError.ExecutionFailed message -> HarnessError.ExecutionFailed(redact message)
        | error -> error

    let private status =
        function
        | ExecutionTerminalStatus.Failed error -> ExecutionTerminalStatus.Failed(harnessError error)
        | ExecutionTerminalStatus.Denied error -> ExecutionTerminalStatus.Denied(harnessError error)
        | ExecutionTerminalStatus.Indeterminate message -> ExecutionTerminalStatus.Indeterminate(redact message)
        | value -> value

    let private artifact (value: Artifact) =
        { value with
            Kind = redact value.Kind
            ContentType = redact value.ContentType
            Payload = redact value.Payload }

    let private judgement (value: JudgementResult) =
        let verdict =
            match value.Verdict with
            | JudgementVerdict.Inconclusive reason -> JudgementVerdict.Inconclusive(redact reason)
            | other -> other

        { value with
            Verdict = verdict
            Explanation = redact value.Explanation
            Suggestions = List.map redact value.Suggestions
            JudgeName = redact value.JudgeName }

    let private regression (value: RegressionResult) =
        { value with
            Regressions =
                value.Regressions
                |> List.map (fun item ->
                    { item with
                        Description = redact item.Description
                        BaselineValue = redact item.BaselineValue
                        CurrentValue = redact item.CurrentValue }) }

    let private policyViolation (value: PolicyViolation) =
        { value with
            PolicyId = redact value.PolicyId
            PolicyVersion = redact value.PolicyVersion
            Description = redact value.Description
            Message = redact value.Message }

    let private constitutionViolation (value: ConstitutionViolation) =
        { value with
            RuleId = redact value.RuleId
            RuleDescription = redact value.RuleDescription }

    let redactResult result =
        { result with
            Status = status result.Status
            Outputs =
                { Response = SecretRedaction.redactOption result.Outputs.Response
                  Artifacts = result.Outputs.Artifacts |> List.map artifact }
            Evidence =
                { result.Evidence with
                    Trace = result.Evidence.Trace |> Option.map Verification.redact
                    Judgement = result.Evidence.Judgement |> Option.map judgement
                    Regression = result.Evidence.Regression |> Option.map regression }
            PolicyDecisions =
                { result.PolicyDecisions with
                    PolicyViolations = result.PolicyDecisions.PolicyViolations |> List.map policyViolation
                    ConstitutionViolations =
                        result.PolicyDecisions.ConstitutionViolations |> List.map constitutionViolation } }
