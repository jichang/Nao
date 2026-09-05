namespace Nao.Agents

/// An action the agent decides to take after reasoning about user input.
/// The orchestrator parses LLM output into one of these actions.
type AgentAction =
    /// Respond directly to the user with the given text
    | Respond of string
    /// Invoke a tool by its identifier with the given input
    | InvokeTool of toolId: string * input: string
    /// Delegate the task to another agent by its opaque runtime identifier.
    | DelegateToAgent of agentId: string * input: string
    /// Ask the user for information required to continue. This ends the current turn;
    /// the user's answer arrives as the next turn with conversation history attached.
    | RequestUserInput of prompt: string
    /// Internal reasoning step (chain-of-thought) — not shown to user
    | Think of string
