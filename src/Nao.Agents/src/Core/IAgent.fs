namespace Nao.Agents

open System.Threading.Tasks

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
    /// Selection priority used as a tie-breaker after capability suitability
    abstract member Priority: int
    /// Concrete things this agent can do for a caller
    abstract member Capabilities: string list
    /// Work this agent owns and is responsible for completing
    abstract member Responsibilities: string list
    /// Structured input/output contract for this agent
    abstract member Signature: ToolSignature
    /// Process a user input string and return a response
    abstract member RunAsync: string -> Task<string>
    /// Handle an inter-agent message and optionally reply
    abstract member HandleMessageAsync: AgentMessage -> Task<AgentMessage option>

/// Optional runtime context bridge for agents that execute tools.
/// Hosts use this to supply the per-session file scope and permission callback
/// after a code-defined agent has been registered in a workspace.
type IContextualAgent =
    abstract member SetToolContext: ToolContext -> unit
