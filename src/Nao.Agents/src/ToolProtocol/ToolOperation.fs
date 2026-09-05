namespace Nao.Agents

open System
open System.Threading.Tasks

/// Errors a concrete tool operation may return from typed execution.
[<RequireQualifiedAccess>]
type ToolExecError =
    /// The decoded value is structurally valid but semantically unacceptable.
    | InvalidInput of reason: string
    /// Execution requires access that was not granted.
    | PermissionDenied of reason: string
    /// Execution failed for a reason the caller cannot repair by changing input.
    | Failed of reason: string

/// Stage of the tool pipeline that produced a failure.
[<RequireQualifiedAccess>]
type ToolFailureKind =
    | InputContract
    | PermissionDenied
    | NotReady
    | ResourceExhausted
    | Execution
    | OutputContract
    | InternalFailure
    | Cancelled

/// Runtime failure containing its pipeline stage, caller-facing diagnostic, and whether changing
/// the invocation input may allow a retry to succeed.
type ToolFailure =
    { Kind: ToolFailureKind
      Message: string
      Retryable: bool }

    member this.Category =
        match this.Kind with
        | ToolFailureKind.InputContract -> PlatformErrorCategory.InvalidInput
        | ToolFailureKind.PermissionDenied -> PlatformErrorCategory.PermissionDenied
        | ToolFailureKind.NotReady -> PlatformErrorCategory.NotReady
        | ToolFailureKind.ResourceExhausted -> PlatformErrorCategory.ResourceExhausted
        | ToolFailureKind.OutputContract -> PlatformErrorCategory.InvalidOutput
        | ToolFailureKind.InternalFailure -> PlatformErrorCategory.InternalFailure
        | ToolFailureKind.Cancelled -> PlatformErrorCategory.Cancelled
        | ToolFailureKind.Execution when this.Retryable -> PlatformErrorCategory.TransientDependency
        | ToolFailureKind.Execution -> PlatformErrorCategory.PermanentDependency

    member this.ToPlatformFailure(correlationId) =
        PlatformFailure.create this.Category this.Message this.Retryable correlationId

[<RequireQualifiedAccess>]
module ToolFailure =
    let ofPlatformFailure (failure: PlatformFailure) =
        let kind =
            match failure.Category with
            | PlatformErrorCategory.InvalidInput -> ToolFailureKind.InputContract
            | PlatformErrorCategory.PermissionDenied -> ToolFailureKind.PermissionDenied
            | PlatformErrorCategory.NotReady -> ToolFailureKind.NotReady
            | PlatformErrorCategory.ResourceExhausted -> ToolFailureKind.ResourceExhausted
            | PlatformErrorCategory.InvalidOutput -> ToolFailureKind.OutputContract
            | PlatformErrorCategory.InternalFailure -> ToolFailureKind.InternalFailure
            | PlatformErrorCategory.Cancelled -> ToolFailureKind.Cancelled
            | PlatformErrorCategory.TransientDependency
            | PlatformErrorCategory.PermanentDependency -> ToolFailureKind.Execution

        { Kind = kind
          Message = failure.Message
          Retryable = failure.Retryable }

/// Encoded result returned through the executable tool boundary.
type ToolRunResult = Result<string, ToolFailure>

/// Original invocation details supplied when reverting a completed tool call.
type RevertContext =
    { Input: string
      Output: string
      ExecutedAt: DateTimeOffset
      Metadata: Map<string, string> }

/// Typed business operation used to construct an executable tool.
type ToolOperation<'Input, 'Output> =
    { ExecuteAsync: AgentContext -> 'Input -> Task<Result<'Output, ToolExecError>>
      RevertAsync: (RevertContext -> Task<Result<unit, string>>) option }

[<RequireQualifiedAccess>]
module ToolOperation =
    /// Creates an operation without revert support.
    let create executeAsync =
        { ExecuteAsync = executeAsync
          RevertAsync = None }

    /// Adds revert support to an operation.
    let withRevert revertAsync operation =
        { operation with
            RevertAsync = Some revertAsync }
