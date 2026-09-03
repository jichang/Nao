namespace Nao.Agents

/// Stable platform-level classification for failures crossing public boundaries.
[<RequireQualifiedAccess>]
type PlatformErrorCategory =
    | InvalidInput
    | PermissionDenied
    | NotReady
    | ResourceExhausted
    | TransientDependency
    | PermanentDependency
    | InvalidOutput
    | InternalFailure
    | Cancelled

/// Structured failure representation shared by versioned platform boundaries.
type PlatformFailure =
    { Category: PlatformErrorCategory; Message: string; Retryable: bool; CorrelationId: string option }

[<RequireQualifiedAccess>]
module PlatformFailure =
    let create category message retryable correlationId : PlatformFailure =
        { Category = category; Message = message; Retryable = retryable; CorrelationId = correlationId }