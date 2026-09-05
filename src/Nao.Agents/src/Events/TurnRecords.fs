namespace Nao.Agents

open System

/// A single tool invocation captured during a turn.
type ToolCallRecord =
    {
        /// Tool name as invoked.
        Name: string
        /// Input passed to the tool.
        Input: string
        /// Output the tool produced.
        Output: string
    }

/// A single sub-agent delegation captured during a turn.
type SubAgentCallRecord =
    {
        /// Sub-agent name.
        Name: string
        /// Input delegated to the sub-agent.
        Input: string
        /// Result returned by the sub-agent.
        Output: string
    }

/// One step of the process an agent ran during a turn, captured in the order it
/// happened (a tool invocation or a sub-agent delegation). Lets a frontend show the
/// whole process - reasoning trail and tool calls - not just the final answer.
type TurnStep =
    {
        /// "tool" | "agent".
        Kind: string
        /// Display title - typically the tool or sub-agent name.
        Title: string
        /// Input passed to the tool / sub-agent.
        Input: string
        /// Output the tool / sub-agent produced.
        Output: string
    }

/// A complete record of one orchestration turn: the user prompt, the agent and
/// tools that ran, and the final answer. This is the unit feedback is attached to.
type TurnRecord =
    {
        /// Stable identifier for this turn.
        TurnId: string
        /// Execution identity, correlation, causation, and attempt for this turn.
        Correlation: CorrelationContext
        /// Session this turn belongs to.
        SessionId: string
        /// User who initiated the turn.
        UserId: string
        /// Workspace the turn ran against.
        WorkspaceKey: string
        /// Agent that handled the turn.
        AgentName: string
        /// The user's prompt.
        Input: string
        /// The agent's final answer.
        Output: string
        /// Tools invoked during the turn, in order.
        ToolCalls: ToolCallRecord list
        /// Sub-agents delegated to during the turn, in order.
        SubAgentCalls: SubAgentCallRecord list
        /// Artifacts published by agents or tools during the turn.
        Artifacts: Artifact list
        /// When the turn completed.
        CreatedAt: DateTimeOffset
    }

module TurnRecord =
    let empty correlation : TurnRecord =
        { TurnId = ""
          Correlation = correlation
          SessionId = ""
          UserId = ""
          WorkspaceKey = ""
          AgentName = ""
          Input = ""
          Output = ""
          ToolCalls = []
          SubAgentCalls = []
          Artifacts = []
          CreatedAt = DateTimeOffset.MinValue }
