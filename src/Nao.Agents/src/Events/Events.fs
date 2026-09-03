namespace Nao.Agents

open System
open System.Threading.Tasks
open Nao.Agents
open Nao.Agents

/// Identity carried by every emitted event. The producer fills these in and never decides
/// where the data lands — routing/persistence is entirely the consumer's choice, so a new
/// storage strategy (per session, per category, per workspace, ...) needs no producer change.
type EventScope =
    { /// Owning user (grain key prefix).
      UserId: string
    /// Session id (grain key suffix).
      SessionId: string
      /// Active conversation within the session.
      ConversationId: string
      /// Workspace the turn ran against.
      WorkspaceKey: string
      /// The action that produced the event — the turn id.
      ActionId: string
            /// Storage routing key — the full grain key ("userId/sessionId").
      SessionKey: string
      /// When the event occurred.
      Timestamp: DateTimeOffset }

    static member Create
        (userId: string, sessionId: string, conversationId: string, workspaceKey: string,
         actionId: string, sessionKey: string) : EventScope =
        { UserId = userId
          SessionId = sessionId
          ConversationId = conversationId
          WorkspaceKey = workspaceKey
          ActionId = actionId
          SessionKey = sessionKey
          Timestamp = DateTimeOffset.UtcNow }

    /// An empty scope, used for library/test contexts that run an agent outside a session.
    static member Empty : EventScope =
        EventScope.Create("", "", "", "", "", "")

/// One process step (a tool call or sub-agent delegation) of an assistant turn, in a
/// transport-neutral shape so the conversation event stream carries no storage-layer types.
type ConversationStep =
    { Kind: string
      Title: string
      Input: string
      Output: string }

/// A single persisted conversation message in a transport-neutral shape (decoupled from the
/// runtime's storage record so this layer stays dependency-light).
type ConversationMessage =
    { Role: string
      Content: string
      Timestamp: DateTimeOffset
    /// Turn this message belongs to.
      TurnId: string
    /// Process steps for an assistant turn (empty for user messages).
      Steps: ConversationStep list
      /// Names of files attached to a user message (empty otherwise).
      Attachments: string list
      /// Structured data published by tools during this turn.
      Data: AgentContextData list }

/// The exact messages sent to an LLM and the raw response it returned.
type LlmExchange = { Round: int; Attempt: int; IsRepair: bool; Messages: (string * string) list; Response: string }

/// Domain events the system dispatches. Each carries an EventScope plus its payload.
/// Consumers subscribe to the bus and decide how/where to persist, so adding a storage
/// strategy never requires touching the producers. (Grows per phase: feedback, then
/// observability, then conversations.)
type NaoEvent =
    /// A turn finished and produced a recordable TurnRecord (feedback can be analysed
    /// against it later).
    | TurnCompleted of EventScope * TurnRecord
    /// A single observability signal (trace span, metric, journal/trace/audit record) was
    /// produced while a turn ran. The full fine-grained observability stream flows through
    /// the bus so any consumer can persist/forward it.
    | ObservabilityCaptured of EventScope * ObservabilitySignal
    /// A conversation store write (messages appended/saved, conversation or session deleted)
    /// occurred. The transcript stream flows through the bus so any consumer can persist or
    /// forward it without the producer choosing where it lands.
    | ConversationCaptured of EventScope * ConversationSignal
    /// A live, fine-grained step of an in-flight turn (reasoning, tool call, sub-agent
    /// delegation, final answer). The turn's recorder consumes these to build the durable
    /// TurnRecord and stream in-progress steps to a UI; any other consumer may persist them.
    | TurnProgress of EventScope * ProgressSignal
    /// One exact prompt/response exchange with the language model, including repair calls.
    | LlmExchangeRecorded of EventScope * LlmExchange

/// One fine-grained observability write produced by the agent harness during a turn. These
/// mirror the functional sink records (Tracer / MetricsCollector / ExecutionJournal / TraceStore
/// / AuditLog) so a consumer can route each to whatever store it chooses.
and ObservabilitySignal =
    /// A trace span was started (root trace or child span).
    | SpanStarted of Span
    /// A span was ended with a final status.
    | SpanEnded of Span * SpanStatus
    /// A timestamped event was attached to a span.
    | SpanEventAdded of Span * name: string * attributes: Map<string, string>
    /// Attributes were set on a span.
    | SpanAttributesSet of Span * attributes: Map<string, string>
    /// An LLM call's token counts and latency were recorded.
    | LlmCallRecorded of inputTokens: int * outputTokens: int * latencyMs: int64
    /// A tool invocation's duration and outcome were recorded.
    | ToolCallRecorded of toolName: string * durationMs: int64 * success: bool
    /// A custom metric point was recorded.
    | MetricRecorded of MetricPoint
    /// A tool execution was recorded in the journal.
    | ExecutionRecorded of ExecutionRecord
    /// A journalled execution was marked reverted.
    | ExecutionReverted of ExecutionRecord
    /// An execution trace was saved to the regression trace store.
    | TraceSaved of ExecutionTrace
    /// A governance audit entry was recorded.
    | AuditRecorded of AuditEntry

/// One conversation-store write. Mirrors the store's mutating operations so a consumer can
/// route each to whatever transcript store it chooses.
and ProgressSignal =
    /// A round's assistant output — the orchestrator's reasoning / decision.
    | ReasoningAdded of content: string
    /// A tool is about to be invoked with the given input.
    | ToolInvoked of name: string * input: string
    /// A tool returned a result.
    | ToolCompleted of name: string * result: string
    /// A sub-agent is about to be delegated to with the given input.
    | SubAgentInvoked of name: string * input: string
    /// A sub-agent returned a result.
    | SubAgentCompleted of name: string * result: string
    /// A tool published structured data for persistence and frontend rendering.
    | ToolDataPublished of data: AgentContextData
    /// The turn produced its final answer.
    | AnswerProduced of answer: string

/// One conversation-store write. Mirrors the store's mutating operations so a consumer can
/// route each to whatever transcript store it chooses.
and ConversationSignal =
    /// Messages were appended to a conversation (incremental, append-only).
    | MessagesAppended of conversationName: string * messages: ConversationMessage list
    /// A full conversation was saved (overwrites any prior history).
    | ConversationSaved of conversationName: string * messages: ConversationMessage list
    /// A conversation was deleted.
    | ConversationDeleted of conversationName: string
    /// All of a session's conversations were deleted.
    | SessionConversationsDeleted

/// A subscriber that receives every published event and persists/forwards it.
/// Its opaque identity is stable even when the record value is passed between modules.
type EventConsumer =
    { Identity: obj
      Handle: NaoEvent -> Task }

module EventConsumer =

  let create handle =
    { Identity = obj ()
      Handle = handle }

  let handleAsync event consumer = consumer.Handle event

  let sameIdentity left right =
    obj.ReferenceEquals(left.Identity, right.Identity)

/// The single dispatch service producers publish to. Fans each event out to all
/// subscribed consumers; producers hold only this — never a concrete storage type.
type EventBus =
  private
    { Publish: NaoEvent -> Task
      Add: EventConsumer -> unit
      Remove: EventConsumer -> unit }

/// Composable event bus helpers.
module EventBus =

  let create publishAsync subscribe unsubscribe =
    { Publish = publishAsync
      Add = subscribe
      Remove = unsubscribe }

  let publishAsync event bus = bus.Publish event

  let subscribe consumer bus = bus.Add consumer

  /// Detach the first subscription with the consumer's identity.
  let unsubscribe consumer bus = bus.Remove consumer

    /// A bus that drops every event and has no subscribers (library/test default).
  let none : EventBus =
    create (fun _ -> Task.CompletedTask) ignore ignore
