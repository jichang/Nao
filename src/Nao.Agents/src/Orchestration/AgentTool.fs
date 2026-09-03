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
                    let! result = Agent.runAsync context value agent
                    return Ok result
                })
        Tool.create name description priority [] input output operation