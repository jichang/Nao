namespace Nao.Agents

/// An action the agent decides to take after reasoning about user input.
/// The orchestrator parses LLM output into one of these actions.
type AgentAction =
    /// Respond directly to the user with the given text
    | Respond of string
    /// Invoke a tool by name with the given input
    | InvokeTool of toolName: string * input: string
    /// Delegate the task to another agent by name
    | DelegateToAgent of agentName: string * input: string
    /// Internal reasoning step (chain-of-thought) — not shown to user
    | Think of string
