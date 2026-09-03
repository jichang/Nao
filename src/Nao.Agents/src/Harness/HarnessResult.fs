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
