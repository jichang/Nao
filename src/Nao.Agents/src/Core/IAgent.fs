namespace Nao.Agents

open System.Threading.Tasks

/// Abstract interface for an agent.
/// Agents process user input and can communicate with other agents via messages.
/// Agents are stateless per call: callers thread prior conversation into the input,
/// and continuity is owned by the store/event path.
type IAgent =
    /// Unique identifier for this agent
    abstract member Id: AgentId
    /// Process a user input string and return a response
    abstract member RunAsync: string -> Task<string>
    /// Handle an inter-agent message and optionally reply
    abstract member HandleMessageAsync: AgentMessage -> Task<AgentMessage option>

/// Optional runtime context bridge for agents that execute tools.
/// Hosts use this to supply the per-session file scope and permission callback
/// after a code-defined agent has been registered in a workspace.
type IContextualAgent =
    abstract member SetToolContext: ToolContext -> unit
