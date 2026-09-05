namespace Nao.Agents

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
                {| error = "permission_denied"
                   kind = kind
                   resource = resource
                   message = message
                   hint = value |}
        | None ->
            JsonSerializer.Serialize
                {| error = "permission_denied"
                   kind = kind
                   resource = resource
                   message = message |}

/// Executable, discoverable tool value.
type Tool =
    { Name: string
      Description: string
      Priority: int
      Schema: ToolSchema
      Permissions: ResourceAccess list
      RunAsync: AgentContext -> string -> Task<ToolRunResult>
      RevertAsync: (RevertContext -> Task<Result<unit, string>>) option }

[<RequireQualifiedAccess>]
module Tool =
    /// Builds an executable tool from typed codecs and a typed business operation.
    let create
        name
        description
        priority
        permissions
        (input: ToolCodec<'Input>)
        (output: ToolCodec<'Output>)
        (operation: ToolOperation<'Input, 'Output>)
        : Tool =
        let runAsync context rawInput =
            task {
                try
                    match input.Decode rawInput with
                    | Error reason ->
                        return
                            Error
                                { Kind = ToolFailureKind.InputContract
                                  Message = reason
                                  Retryable = true }
                    | Ok decodedInput ->
                        let mutable denied = None

                        for access in permissions do
                            if denied.IsNone then
                                let! allowed =
                                    context.RequestPermission
                                        access
                                        (sprintf "Tool '%s' requires this access." name)
                                        false

                                if not allowed then
                                    denied <- Some access

                        match denied with
                        | Some access ->
                            return
                                Error
                                    { Kind = ToolFailureKind.PermissionDenied
                                      Message = PermissionDenied.format access None
                                      Retryable = false }
                        | None ->
                            match! operation.ExecuteAsync context decodedInput with
                            | Error(ToolExecError.InvalidInput reason) ->
                                return
                                    Error
                                        { Kind = ToolFailureKind.InputContract
                                          Message = reason
                                          Retryable = true }
                            | Error(ToolExecError.PermissionDenied reason) ->
                                return
                                    Error
                                        { Kind = ToolFailureKind.PermissionDenied
                                          Message = reason
                                          Retryable = false }
                            | Error(ToolExecError.Failed reason) ->
                                return
                                    Error
                                        { Kind = ToolFailureKind.Execution
                                          Message = reason
                                          Retryable = false }
                            | Ok result ->
                                match output.Encode result with
                                | Ok encoded -> return Ok encoded
                                | Error reason ->
                                    return
                                        Error
                                            { Kind = ToolFailureKind.OutputContract
                                              Message = reason
                                              Retryable = false }
                with ex ->
                    return
                        PlatformFailure.fromException PlatformFailureBoundary.Tool None ex
                        |> ToolFailure.ofPlatformFailure
                        |> Error
            }

        { Name = name
          Description = description
          Priority = priority
          Schema = ToolSchema.create input.Schema output.Schema
          Permissions = permissions
          RunAsync = runAsync
          RevertAsync = operation.RevertAsync }

    /// Wraps execution while preserving the tool's identity, schema, permissions, and revert behavior.
    let decorate wrap tool =
        { tool with
            RunAsync = wrap tool.RunAsync }

    /// Whether completed calls may be reverted.
    let canRevert tool = tool.RevertAsync.IsSome

    /// Reverts a completed invocation, or returns the canonical unsupported result.
    let revertAsync context tool =
        match tool.RevertAsync with
        | Some revert -> revert context
        | None -> Task.FromResult(Error "Tool does not support revert.")

    /// Renders a tool's metadata and explicit transport schemas for a model prompt.
    let render tool =
        sprintf
            "  %s (priority %d): %s\n  Input: %s\n  Output: %s"
            tool.Name
            tool.Priority
            tool.Description
            tool.Schema.Input
            tool.Schema.Output
