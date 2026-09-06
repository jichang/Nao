namespace Nao.Agents

[<RequireQualifiedAccess>]
module AgentTool =
    /// Exposes an agent as a tool so its result returns to the caller's planning loop.
    let create name description priority inputSchema outputSchema (agent: Agent) : Tool =
        let input = ToolCodec.create inputSchema Ok Ok
        let output = ToolCodec.create outputSchema Ok Ok

        let operation =
            ToolOperation.create (fun context value ->
                task {
                    let! result = ExecutionRuntime.runAgent context agent value

                    return
                        result
                        |> Result.mapError (fun failure ->
                            match failure.Category with
                            | PlatformErrorCategory.InvalidInput -> ToolExecError.InvalidInput failure.Message
                            | PlatformErrorCategory.PermissionDenied -> ToolExecError.PermissionDenied failure.Message
                            | _ -> ToolExecError.Failed failure.Message)
                })

        Tool.create name description priority [] input output operation
