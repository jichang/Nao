namespace Nao.Runtime.Orleans

open System
open System.Threading.Tasks
open Nao.Agents
open Orleans

/// A single step in the process an agent ran to produce a turn's answer
/// (a tool invocation or a sub-agent delegation). Surfaced to the frontend so the
/// whole process — not just the final answer — is visible.
[<GenerateSerializer>]
type TurnStepRecord() =
    /// "tool" | "agent" (extendable).
    [<Id(0u)>] member val Kind: string = "" with get, set
    /// Display title — typically the tool or sub-agent name.
    [<Id(1u)>] member val Title: string = "" with get, set
    /// Input passed to the tool / sub-agent.
    [<Id(2u)>] member val Input: string = "" with get, set
    /// Output the tool / sub-agent produced.
    [<Id(3u)>] member val Output: string = "" with get, set

/// Structured data published by a tool during a turn.
[<GenerateSerializer>]
type AgentContextDataRecord() =
    [<Id(0u)>] member val Kind: string = "" with get, set
    [<Id(1u)>] member val ContentType: string = "" with get, set
    [<Id(2u)>] member val Payload: string = "" with get, set

/// A single persisted message in a conversation
type PersistedMessage =
    { Role: string
      Content: string
      Timestamp: DateTimeOffset
    /// Turn this message belongs to.
      TurnId: string
    /// Process steps for an assistant turn (empty for user messages).
      Steps: TurnStepRecord[]
    /// Names of files attached to a user message (empty for assistant messages).
      Attachments: string[]
    /// Structured data published by tools during the turn (empty for user messages).
      Data: AgentContextDataRecord[] }

/// Metadata about a persisted conversation
type ConversationMeta =
    { SessionId: string
      ConversationName: string
      AgentName: string
      CreatedAt: DateTimeOffset
      LastMessageAt: DateTimeOffset
      MessageCount: int }

/// Pluggable interface for external conversation persistence.
/// Implementations can store to files, databases, or cloud storage.
/// All methods are organized by session ID for grouping.
type IConversationStore =
    /// Append messages to a conversation (incremental — does not rewrite the whole history)
    abstract member AppendAsync: sessionId: string -> conversationName: string -> messages: PersistedMessage array -> Task

    /// Save the full conversation (overwrites any existing data for this session+conversation)
    abstract member SaveAsync: sessionId: string -> conversationName: string -> messages: PersistedMessage array -> Task

    /// Load the full conversation history for a session+conversation
    abstract member LoadAsync: sessionId: string -> conversationName: string -> Task<PersistedMessage array>

    /// List all conversations for a session
    abstract member ListConversationsAsync: sessionId: string -> Task<ConversationMeta array>

    /// List all session IDs that have stored conversations
    abstract member ListSessionsAsync: unit -> Task<string array>

    /// Delete a specific conversation
    abstract member DeleteConversationAsync: sessionId: string -> conversationName: string -> Task

    /// Delete all data for a session
    abstract member DeleteSessionAsync: sessionId: string -> Task
