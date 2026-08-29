namespace Nao.Agents

open System.Threading.Tasks

/// Declares the transport representation accepted or returned by an agent.
/// Structured schemas are authored text; the runtime does not infer them from CLR types.
[<RequireQualifiedAccess>]
type AgentParameter =
    /// An unstructured text value.
    | Text
    /// A structured value described by the supplied schema.
    | Structured of schema: string

/// Explicit transport contract advertised by an agent. `Input` describes values accepted by
/// `RunAsync`; `Output` describes values it returns.
type AgentContract = { Input: AgentParameter; Output: AgentParameter }

[<RequireQualifiedAccess>]
module AgentContract =
    /// Contract for agents that accept and return plain text.
    let Text = { Input = AgentParameter.Text; Output = AgentParameter.Text }

/// Abstract interface for an agent.
/// Agents process user input and can communicate with other agents via messages.
/// Agents are stateless per call: callers thread prior conversation into the input,
/// and continuity is owned by the store/event path.
type IAgent =
    /// Unique identifier for this agent
    abstract member Id: string
    /// Short human-readable name for this agent
    abstract member Name: string
    /// Human-readable description of this agent's purpose
    abstract member Description: string
    /// Selection priority used as a tie-breaker between suitable agents
    abstract member Priority: int
    /// Work this agent owns and is responsible for completing
    abstract member Responsibilities: string list
    /// Structured input/output contract for this agent
    abstract member Contract: AgentContract
    /// Process a user input string in the supplied runtime context and return a response
    abstract member RunAsync: context: AgentContext * input: string -> Task<string>
    /// Handle an inter-agent message in the supplied runtime context and optionally reply
    abstract member HandleMessageAsync: context: AgentContext * message: AgentMessage -> Task<AgentMessage option>

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

[<RequireQualifiedAccess>]
module AgentTool =
    /// Exposes an agent as a tool so its result returns to the caller's planning loop.
    let create name description priority inputSchema outputSchema (agent: IAgent) : ITool =
        AgentBackedTool(name, description, priority, inputSchema, outputSchema, agent)

/// Base class for a concrete, context-aware agent with explicit metadata and transport contract.
/// The generic arguments describe the domain types owned by the implementation; this base does
/// not serialize them or infer a contract from them.
[<AbstractClass>]
type TypedContextualAgent<'Input, 'Output>(id: string, name: string, description: string, priority: int, responsibilities: string list, contract: AgentContract) =

    /// Processes the encoded transport input using services scoped to the current run.
    abstract member RunAsync: context: AgentContext * input: string -> Task<string>

    interface IAgent with
        member _.Id = id
        member _.Name = name
        member _.Description = description
        member _.Priority = priority
        member _.Responsibilities = responsibilities
        member _.Contract = contract
        member this.RunAsync(context: AgentContext, input: string) = this.RunAsync(context, input)
        member _.HandleMessageAsync(_context, _message) = Task.FromResult(None)
