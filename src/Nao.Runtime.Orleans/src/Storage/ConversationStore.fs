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
    [<Id(0u)>]
    member val Kind: string = "" with get, set

    /// Display title — typically the tool or sub-agent name.
    [<Id(1u)>]
    member val Title: string = "" with get, set

    /// Input passed to the tool / sub-agent.
    [<Id(2u)>]
    member val Input: string = "" with get, set

    /// Output the tool / sub-agent produced.
    [<Id(3u)>]
    member val Output: string = "" with get, set

/// Structured data published by a tool during a turn.
[<GenerateSerializer>]
type AgentContextDataRecord() =
    [<Id(0u)>]
    member val Kind: string = "" with get, set

    [<Id(1u)>]
    member val ContentType: string = "" with get, set

    [<Id(2u)>]
    member val Payload: string = "" with get, set

/// A single persisted message in a conversation
type PersistedMessage =
    {
        Role: string
        Content: string
        Timestamp: DateTimeOffset
        /// Turn this message belongs to.
        TurnId: string
        /// Process steps for an assistant turn (empty for user messages).
        Steps: TurnStepRecord[]
        /// Names of files attached to a user message (empty for assistant messages).
        Attachments: string[]
        /// Structured data published by tools during the turn (empty for user messages).
        Data: AgentContextDataRecord[]
    }

/// Metadata about a persisted conversation
type ConversationMeta =
    { SessionId: string
      ConversationName: string
      AgentName: string
      CreatedAt: DateTimeOffset
      LastMessageAt: DateTimeOffset
      MessageCount: int }

/// Pluggable functional capability for external conversation persistence.
/// Factories can store to files, databases, or cloud storage.
/// All methods are organized by session ID for grouping.
type ConversationStore =
    /// Append messages to a conversation (incremental — does not rewrite the whole history)
    {
        AppendAsync: string -> string -> PersistedMessage array -> Task

        /// Save the full conversation (overwrites any existing data for this session+conversation)
        SaveAsync: string -> string -> PersistedMessage array -> Task

        /// Load the full conversation history for a session+conversation
        LoadAsync: string -> string -> Task<PersistedMessage array>

        /// List all conversations for a session
        ListConversationsAsync: string -> Task<ConversationMeta array>

        /// List all session IDs that have stored conversations
        ListSessionsAsync: unit -> Task<string array>

        /// Delete a specific conversation
        DeleteConversationAsync: string -> string -> Task

        /// Delete all data for a session
        DeleteSessionAsync: string -> Task
    }
