namespace Nao.Agents

open System
open Nao.Agents

/// Whether the user was satisfied with a turn.
[<RequireQualifiedAccess>]
type FeedbackSentiment =
    | Positive
    | Negative
    | Neutral

/// A single tool invocation captured during a turn.
type ToolCallRecord =
    { /// Tool name as invoked.
      Name: string
      /// Tool version that was used, if known.
      Version: string option
      /// Input passed to the tool.
      Input: string
      /// Output the tool produced.
      Output: string
      /// Where the tool came from (so adjustments can target the source).
      Provenance: ToolProvenance option }

/// A single sub-agent delegation captured during a turn.
type SubAgentCallRecord =
    { /// Sub-agent name.
      Name: string
      /// Input delegated to the sub-agent.
      Input: string
      /// Result returned by the sub-agent.
      Output: string }

/// One step of the process an agent ran during a turn, captured in the order it
/// happened (a tool invocation or a sub-agent delegation). Lets a frontend show the
/// whole process — reasoning trail and tool calls — not just the final answer.
type TurnStep =
    { /// "tool" | "agent".
      Kind: string
      /// Display title — typically the tool or sub-agent name.
      Title: string
      /// Input passed to the tool / sub-agent.
      Input: string
      /// Output the tool / sub-agent produced.
      Output: string }

/// A complete record of one orchestration turn: the user prompt, the agent and
/// tools that ran, and the final answer. This is the unit feedback is attached to.
type TurnRecord =
    { /// Stable identifier for this turn.
      TurnId: string
      /// Session this turn belongs to.
      SessionId: string
      /// User who initiated the turn.
      UserId: string
      /// Workspace the turn ran against.
      WorkspaceKey: string
      /// Agent that handled the turn.
      AgentName: string
      /// Agent version that handled the turn, if pinned.
      AgentVersion: string option
      /// The user's prompt.
      Input: string
      /// The agent's final answer.
      Output: string
      /// Tools invoked during the turn, in order.
      ToolCalls: ToolCallRecord list
      /// Sub-agents delegated to during the turn, in order.
      SubAgentCalls: SubAgentCallRecord list
      /// When the turn completed.
      CreatedAt: DateTimeOffset }

    static member Empty =
        { TurnId = ""
          SessionId = ""
          UserId = ""
          WorkspaceKey = ""
          AgentName = ""
          AgentVersion = None
          Input = ""
          Output = ""
          ToolCalls = []
          SubAgentCalls = []
          CreatedAt = DateTimeOffset.MinValue }

/// User feedback attached to a turn. The signal that drives adjustments.
type Feedback =
    { /// Unique identifier for this feedback entry.
      Id: Guid
      /// Turn this feedback refers to.
      TurnId: string
      /// Session the turn belonged to.
      SessionId: string
      /// User who gave the feedback.
      UserId: string
      /// Positive / negative / neutral.
      Sentiment: FeedbackSentiment
      /// Optional free-text explanation from the user.
      Comment: string option
      /// When the feedback was given.
      CreatedAt: DateTimeOffset
      /// Arbitrary extra context.
      Metadata: Map<string, string> }

/// Where a feedback signal originated. Explicit feedback is an intentional good/bad
/// rating; conversation feedback is inferred heuristically from the chat history;
/// memory feedback is surfaced by the memory system. The source is stored on each
/// `Feedback` in its `Metadata` (see the `FeedbackSource` module) so the cross-session
/// aggregator can weigh and explain its suggestions without breaking existing literals.
module FeedbackSource =
    /// Metadata key under which the source marker is stored on a `Feedback`.
    [<Literal>]
    let Key = "source"

    [<Literal>]
    let Explicit = "explicit"

    [<Literal>]
    let Conversation = "conversation"

    [<Literal>]
    let Memory = "memory"

    /// Read the source marker from a feedback entry (defaults to explicit).
    let ofFeedback (f: Feedback) : string =
        match f.Metadata.TryFind Key with
        | Some v -> v
        | None -> Explicit

    /// Stamp a source marker into a metadata map.
    let stamp (source: string) (metadata: Map<string, string>) : Map<string, string> =
        metadata |> Map.add Key source
