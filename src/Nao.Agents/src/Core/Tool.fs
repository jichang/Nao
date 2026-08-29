namespace Nao.Agents

open System
open System.Text.Json
open System.Threading.Tasks

/// Canonical permission-denial payloads returned through tool execution boundaries.
[<RequireQualifiedAccess>]
module PermissionDenied =
    let private kindAndResource access =
        match access with
        | ResourceAccess.Web(_, url) -> "web", url
        | ResourceAccess.File(_, path) -> "file", path
        | ResourceAccess.ToolCall name -> "tool", name

    let format access hint =
        let kind, resource = kindAndResource access
        let message = sprintf "Permission denied: access to %s was not granted." resource
        match hint with
        | Some value ->
            JsonSerializer.Serialize
                {| error = "permission_denied"; kind = kind; resource = resource; message = message; hint = value |}
        | None ->
            JsonSerializer.Serialize
                {| error = "permission_denied"; kind = kind; resource = resource; message = message |}

type IToolParameter =
    /// Author-supplied transport schema. The runtime performs no type or schema inference.
    abstract member Schema: string

/// Typed transport descriptor for one side of a tool contract.
/// `Schema` is authored documentation; `Encode` and `Decode` are the sole authority for the
/// wire representation.
type ToolParameter<'Value> =
    { Schema: string
      Encode: 'Value -> Result<string, string>
      Decode: string -> Result<'Value, string> }

    interface IToolParameter with
        member this.Schema = this.Schema

/// Erased input and output descriptors exposed during discovery and prompt construction.
/// Typed implementations retain their concrete descriptors through `TypedTool`.
type ToolContract =
    { Input: IToolParameter
      Output: IToolParameter }

/// Errors a concrete tool may return from typed execution.
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
    | Execution
    | OutputContract

/// Runtime failure containing its pipeline stage, caller-facing diagnostic, and whether changing
/// the invocation input may allow a retry to succeed.
type ToolFailure = { Kind: ToolFailureKind; Message: string; Retryable: bool }

/// Encoded result returned through the `ITool` runtime boundary.
type ToolRunResult = Result<string, ToolFailure>

/// Original invocation details supplied when reverting a completed tool call: its encoded input
/// and output, completion time, and host-defined journal metadata.
type RevertContext = { Input: string; Output: string; ExecutedAt: DateTimeOffset; Metadata: Map<string, string> }

[<RequireQualifiedAccess>]
module ToolParameter =
    /// Creates a descriptor from an explicit schema and caller-owned codecs.
    let create schema encode decode =
        { Schema = schema
          Encode = encode
          Decode = decode }

    /// Identity descriptor for tools whose transport and domain value are both plain text.
    let text = create "string" Ok Ok

/// Runtime and discovery boundary for an executable tool.
/// `RunAsync` consumes and returns encoded transport values according to `Contract`.
type ITool =
    /// Stable plain name used for discovery and invocation.
    abstract member Name: string
    /// Human-readable purpose shown to planners and users.
    abstract member Description: string
    /// Selection priority used as a tie-breaker between suitable tools.
    abstract member Priority: int
    /// Explicit input and output schemas advertised by the tool.
    abstract member Contract: ToolContract
    /// Static resource access requested before typed execution begins.
    abstract member Permissions: ResourceAccess list
    /// Whether completed calls may be passed to `RevertAsync`.
    abstract member CanRevert: bool
    /// Decodes input, executes the tool, and encodes output as one runtime operation.
    abstract member RunAsync: context: AgentContext * input: string -> Task<ToolRunResult>
    /// Reverts a previously completed call when `CanRevert` is true.
    abstract member RevertAsync: context: RevertContext -> Task<Result<unit, string>>

type private DecoratedTool
    (tool: ITool,
     wrap: (AgentContext -> string -> Task<ToolRunResult>) -> AgentContext -> string -> Task<ToolRunResult>) =

    interface ITool with
        member _.Name = tool.Name
        member _.Description = tool.Description
        member _.Priority = tool.Priority
        member _.Contract = tool.Contract
        member _.Permissions = tool.Permissions
        member _.CanRevert = tool.CanRevert
        member _.RunAsync(context, input) =
            wrap (fun innerContext innerInput -> tool.RunAsync(innerContext, innerInput)) context input
        member _.RevertAsync(context) = tool.RevertAsync context

[<RequireQualifiedAccess>]
module Tool =
    /// Wraps execution while preserving the tool's identity, contract, and revert behavior.
    let decorate wrap (tool: ITool) : ITool =
        DecoratedTool(tool, wrap)

    /// Renders a tool's metadata and explicit transport schemas for a model prompt.
    let render (tool: ITool) =
        sprintf "  %s (priority %d): %s\n  Input: %s\n  Output: %s"
            tool.Name
            tool.Priority
            tool.Description
            tool.Contract.Input.Schema
            tool.Contract.Output.Schema

/// Base class for tools implemented with explicit typed input and output descriptors.
/// The `ITool.RunAsync` implementation decodes input, requests static permissions, invokes
/// `ExecuteAsync`, maps typed errors, and encodes successful output.
[<AbstractClass>]
type TypedTool<'Input, 'Output>(name: string, description: string, priority: int, permissions: ResourceAccess list, canRevert: bool, input: ToolParameter<'Input>, output: ToolParameter<'Output>) =

    new(name: string, description: string, permissions: ResourceAccess list, input: ToolParameter<'Input>, output: ToolParameter<'Output>) =
        TypedTool<'Input, 'Output>(name, description, 0, permissions, false, input, output)

    new(name: string, description: string, priority: int, permissions: ResourceAccess list, input: ToolParameter<'Input>, output: ToolParameter<'Output>) =
        TypedTool<'Input, 'Output>(name, description, priority, permissions, false, input, output)

    /// Executes business logic after input decoding and static permission checks succeed.
    abstract member ExecuteAsync: context: AgentContext * input: 'Input -> Task<Result<'Output, ToolExecError>>

    /// Reverts a completed invocation. Override together with `canRevert = true`.
    abstract member RevertAsync: context: RevertContext -> Task<Result<unit, string>>
    default _.RevertAsync(_context) = Task.FromResult(Error "Tool does not support revert.")

    interface ITool with
        member _.Name = name
        member _.Description = description
        member _.Priority = priority
        member _.Contract = { Input = input; Output = output }
        member _.Permissions = permissions
        member _.CanRevert = canRevert
        member this.RunAsync(context, rawInput) =
            task {
                try
                    match input.Decode rawInput with
                    | Error reason ->
                        return Error { Kind = ToolFailureKind.InputContract; Message = reason; Retryable = true }
                    | Ok decodedInput ->
                        let mutable denied = None
                        for access in permissions do
                            if denied.IsNone then
                                let! allowed = context.RequestPermission access (sprintf "Tool '%s' requires this access." name) false
                                if not allowed then denied <- Some access
                        match denied with
                        | Some access ->
                            return Error { Kind = ToolFailureKind.PermissionDenied; Message = PermissionDenied.format access None; Retryable = false }
                        | None ->
                            match! this.ExecuteAsync(context, decodedInput) with
                            | Error (ToolExecError.InvalidInput reason) ->
                                return Error { Kind = ToolFailureKind.InputContract; Message = reason; Retryable = true }
                            | Error (ToolExecError.PermissionDenied reason) ->
                                return Error { Kind = ToolFailureKind.PermissionDenied; Message = reason; Retryable = false }
                            | Error (ToolExecError.Failed reason) ->
                                return Error { Kind = ToolFailureKind.Execution; Message = reason; Retryable = false }
                            | Ok result ->
                                match output.Encode result with
                                | Ok encoded -> return Ok encoded
                                | Error reason ->
                                    return Error { Kind = ToolFailureKind.OutputContract; Message = reason; Retryable = false }
                with ex ->
                    return Error { Kind = ToolFailureKind.Execution; Message = ex.Message; Retryable = false }
            }
        member this.RevertAsync(context) = this.RevertAsync(context)