namespace Nao.Agents

type private AgentBackedTool
    (name: string, description: string, priority: int, inputSchema: string, outputSchema: string, agent: IAgent) =
    inherit TypedTool<string, string>(
        name,
        description,
        priority,
        [],
        ToolParameter.create inputSchema Ok Ok,
        ToolParameter.create outputSchema Ok Ok)

    override _.ExecuteAsync(context, input) =
        task {
            let! output = agent.RunAsync(context, input)
            return Ok output
        }

    interface IRuntimeToolContext with
        member _.SetRuntimeContext bus scope traceContext =
            match agent with
            | :? IRuntimeAgentContext as contextual -> contextual.SetEventContext bus scope
            | _ -> ()

            match agent with
            | :? OrchestratorBase as orchestrator -> orchestrator.TraceContext <- traceContext
            | _ -> ()

[<RequireQualifiedAccess>]
module AgentTool =
    /// Exposes an agent as a tool so its result returns to the caller's planning loop.
    let create name description priority inputSchema outputSchema (agent: IAgent) : ITool =
        AgentBackedTool(name, description, priority, inputSchema, outputSchema, agent)