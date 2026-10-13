namespace Nao.Agents

open System
open System.Threading.Tasks
open Nao.Agents
open Nao.Agents

/// Identity carried by every emitted event. The producer fills these in and never decides
/// where the data lands — routing/persistence is entirely the consumer's choice, so a new
/// storage strategy (per session, per category, per workspace, ...) needs no producer change.
type EventScope =
    {
        /// Owning user (grain key prefix).
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
        /// Execution correlation for events emitted within a harness run.
        Correlation: CorrelationContext
        /// When the event occurred.
        Timestamp: DateTimeOffset
    }

    static member Create
        (
            userId: string,
            sessionId: string,
            conversationId: string,
            workspaceKey: string,
            actionId: string,
            sessionKey: string,
            correlation: CorrelationContext
        ) : EventScope =
        { UserId = userId
          SessionId = sessionId
          ConversationId = conversationId
          WorkspaceKey = workspaceKey
          ActionId = actionId
          SessionKey = sessionKey
          Correlation = correlation
          Timestamp = DateTimeOffset.UtcNow }

    /// An empty scope, used for library/test contexts that run an agent outside a session.
    static member CreateEmpty() : EventScope =
        EventScope.Create("", "", "", "", "", "", CorrelationContext.root ())

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
    {
        Role: string
        Content: string
        Timestamp: DateTimeOffset
        /// Execution identity, correlation, causation, and attempt for this message.
        Correlation: CorrelationContext
        /// Turn this message belongs to.
        TurnId: string
        /// Process steps for an assistant turn (empty for user messages).
        Steps: ConversationStep list
        /// Names of files attached to a user message (empty otherwise).
        Attachments: string list
        /// Artifacts published by agents or tools during this turn.
        Artifacts: Artifact list
    }

/// The exact messages sent to an LLM and the complete result it returned.
type LlmExchange =
    { IdempotencyKey: IdempotencyKey
      Round: int
      Attempt: int
      IsRepair: bool
      Provider: string
      Options: CompletionOptions
      Messages: (string * string) list
      Result: CompletionResult }

[<RequireQualifiedAccess>]
module RecordedReplayFixtures =
    let private uniqueMap kind entries =
        entries
        |> List.fold
            (fun fixtures (key, fixture) ->
                if fixtures |> Map.containsKey key then
                    invalidArg
                        (nameof entries)
                        (sprintf "Duplicate %s replay fixture '%s'." kind (IdempotencyKey.value key))

                fixtures |> Map.add key fixture)
            Map.empty

    let create (providerExchanges: LlmExchange list) (toolExecutions: ExecutionRecord list) =
        let providers =
            providerExchanges
            |> List.map (fun exchange ->
                let fixture: ProviderReplayFixture =
                    { Provider = exchange.Provider
                      Options = exchange.Options
                      Messages = exchange.Messages
                      Result = exchange.Result }

                exchange.IdempotencyKey, ReplayFixture.Recorded fixture)
            |> uniqueMap "provider"

        let tools =
            toolExecutions
            |> List.map (fun execution ->
                let durationMs =
                    execution.Metadata
                    |> Map.tryFind "duration.ms"
                    |> Option.bind (fun value ->
                        match
                            Int64.TryParse(
                                value,
                                Globalization.NumberStyles.Integer,
                                Globalization.CultureInfo.InvariantCulture
                            )
                        with
                        | true, duration -> Some duration
                        | false, _ -> None)
                    |> Option.defaultValue 0L

                let result: ToolInvocationResult =
                    { IdempotencyKey = execution.IdempotencyKey
                      Success = execution.Failure.IsNone
                      Output = execution.Output
                      Error = (execution.Failure |> Option.map _.Message)
                      Failure = execution.Failure
                      DurationMs = durationMs
                      ReplaySafety = execution.ReplaySafety }

                let fixture: ToolReplayFixture =
                    { ToolName = execution.ToolName
                      Input = execution.Input
                      Result = result }

                execution.IdempotencyKey, ReplayFixture.Recorded fixture)
            |> uniqueMap "tool"

        { Providers = providers; Tools = tools }

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
    /// A complete metric observation was recorded.
    | MetricRecorded of MetricRecord
    /// A tool execution was recorded in the journal.
    | ExecutionRecorded of ExecutionRecord
    /// A journalled execution was marked reverted.
    | ExecutionReverted of Guid
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
    /// An agent or tool published an artifact for persistence and frontend rendering.
    | ArtifactPublished of artifact: Artifact
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

    let create handle = { Identity = obj (); Handle = handle }

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

module private EventRedaction =
    let text = SecretRedaction.redact
    let optionalText = Option.map text
    let textMap = Map.map (fun _ -> text)

    let artifact (artifact: Artifact) = artifact

    let failure (failure: ToolFailure) =
        { failure with
            Message = text failure.Message }

    let completion (result: CompletionResult) =
        { result with
            Content = text result.Content
            FinishReason = text result.FinishReason }

    let spanStatus =
        function
        | SpanStatus.Error message -> SpanStatus.Error(text message)
        | status -> status

    let span (span: Span) =
        { span with
            OperationName = text span.OperationName
            Status = spanStatus span.Status
            Attributes = textMap span.Attributes
            Events =
                span.Events
                |> List.map (fun (event: SpanEvent) ->
                    { event with
                        Name = text event.Name
                        Attributes = textMap event.Attributes }) }

    let executionRecord (record: ExecutionRecord) =
        { record with
            ToolName = text record.ToolName
            Input = text record.Input
            Output = text record.Output
            Failure = record.Failure |> Option.map failure
            Metadata = textMap record.Metadata }

    let trace (trace: ExecutionTrace) =
        let traceAction =
            function
            | TraceAction.LlmCall model -> TraceAction.LlmCall(text model)
            | TraceAction.EmbeddingCall profileId -> TraceAction.EmbeddingCall(text profileId)
            | TraceAction.ToolInvocation toolName -> TraceAction.ToolInvocation(text toolName)
            | TraceAction.AgentDelegation agentName -> TraceAction.AgentDelegation(text agentName)
            | TraceAction.MemoryAccess operation -> TraceAction.MemoryAccess(text operation)
            | action -> action

        { trace with
            AgentId = text trace.AgentId
            Input = text trace.Input
            Output = optionalText trace.Output
            Steps =
                trace.Steps
                |> List.map (fun (step: TraceStep) ->
                    { step with
                        Action = traceAction step.Action
                        Input = text step.Input
                        Output = text step.Output })
            Metadata = textMap trace.Metadata }

    let audit (entry: AuditEntry) =
        let action =
            match entry.Action with
            | AuditAction.LlmCall model -> AuditAction.LlmCall(text model)
            | AuditAction.ToolInvocation toolName -> AuditAction.ToolInvocation(text toolName)
            | AuditAction.AgentDelegation agentName -> AuditAction.AgentDelegation(text agentName)
            | AuditAction.MemoryWrite key -> AuditAction.MemoryWrite(text key)
            | AuditAction.MemoryRead key -> AuditAction.MemoryRead(text key)
            | AuditAction.ResourceAccess(kind, resource) -> AuditAction.ResourceAccess(text kind, text resource)
            | AuditAction.PermissionCheck capability -> AuditAction.PermissionCheck(text capability)
            | AuditAction.PolicyEvaluation policyId -> AuditAction.PolicyEvaluation(text policyId)
            | AuditAction.ConstitutionEvaluation ruleId -> AuditAction.ConstitutionEvaluation(text ruleId)
            | AuditAction.LifecycleTransition(fromState, toState) ->
                AuditAction.LifecycleTransition(text fromState, text toState)

        { entry with
            AgentId = text entry.AgentId
            Action = action
            Input = optionalText entry.Input
            Output = optionalText entry.Output
            Metadata = textMap entry.Metadata }

    let metric (record: MetricRecord) =
        let payload =
            match record.Payload with
            | MetricPayload.EmbeddingCall(profileId, batchId, inputCount, inputTokens, latencyMs, cost, attempts) ->
                MetricPayload.EmbeddingCall(
                    text profileId,
                    text batchId,
                    inputCount,
                    inputTokens,
                    latencyMs,
                    cost,
                    attempts
                )
            | MetricPayload.ToolCall(toolName, durationMs, success) ->
                MetricPayload.ToolCall(text toolName, durationMs, success)
            | MetricPayload.Custom point ->
                MetricPayload.Custom
                    { point with
                        Name = text point.Name
                        Labels = textMap point.Labels }
            | payload -> payload

        { record with Payload = payload }

    let turnRecord (record: TurnRecord) =
        let toolCall (call: ToolCallRecord) =
            { call with
                Name = text call.Name
                Input = text call.Input
                Output = text call.Output }

        let subAgentCall (call: SubAgentCallRecord) =
            { call with
                Name = text call.Name
                Input = text call.Input
                Output = text call.Output }

        { record with
            AgentName = text record.AgentName
            Input = text record.Input
            Output = text record.Output
            ToolCalls = record.ToolCalls |> List.map toolCall
            SubAgentCalls = record.SubAgentCalls |> List.map subAgentCall
            Artifacts = record.Artifacts |> List.map artifact }

    let conversationMessage (message: ConversationMessage) =
        { message with
            Role = text message.Role
            Content = text message.Content
            Steps =
                message.Steps
                |> List.map (fun (step: ConversationStep) ->
                    { step with
                        Kind = text step.Kind
                        Title = text step.Title
                        Input = text step.Input
                        Output = text step.Output })
            Attachments = message.Attachments |> List.map text
            Artifacts = message.Artifacts |> List.map artifact }

    let observability =
        function
        | SpanStarted value -> SpanStarted(span value)
        | SpanEnded(value, status) -> SpanEnded(span value, spanStatus status)
        | SpanEventAdded(value, name, attributes) -> SpanEventAdded(span value, text name, textMap attributes)
        | SpanAttributesSet(value, attributes) -> SpanAttributesSet(span value, textMap attributes)
        | MetricRecorded value -> MetricRecorded(metric value)
        | ExecutionRecorded value -> ExecutionRecorded(executionRecord value)
        | TraceSaved value -> TraceSaved(trace value)
        | AuditRecorded value -> AuditRecorded(audit value)
        | ExecutionReverted value -> ExecutionReverted value

    let progress =
        function
        | ReasoningAdded content -> ReasoningAdded(text content)
        | ToolInvoked(name, input) -> ToolInvoked(text name, text input)
        | ToolCompleted(name, result) -> ToolCompleted(text name, text result)
        | SubAgentInvoked(name, input) -> SubAgentInvoked(text name, text input)
        | SubAgentCompleted(name, result) -> SubAgentCompleted(text name, text result)
        | ArtifactPublished value -> ArtifactPublished(artifact value)
        | AnswerProduced answer -> AnswerProduced(text answer)

    let conversation =
        function
        | MessagesAppended(name, messages) -> MessagesAppended(text name, messages |> List.map conversationMessage)
        | ConversationSaved(name, messages) -> ConversationSaved(text name, messages |> List.map conversationMessage)
        | ConversationDeleted name -> ConversationDeleted(text name)
        | SessionConversationsDeleted -> SessionConversationsDeleted

    let llmExchange (exchange: LlmExchange) =
        { exchange with
            Provider = text exchange.Provider
            Messages = exchange.Messages |> List.map (fun (role, content) -> text role, text content)
            Result = completion exchange.Result }

    let event =
        function
        | TurnCompleted(scope, record) -> TurnCompleted(scope, turnRecord record)
        | ObservabilityCaptured(scope, signal) -> ObservabilityCaptured(scope, observability signal)
        | ConversationCaptured(scope, signal) -> ConversationCaptured(scope, conversation signal)
        | TurnProgress(scope, signal) -> TurnProgress(scope, progress signal)
        | LlmExchangeRecorded(scope, exchange) -> LlmExchangeRecorded(scope, llmExchange exchange)

/// Composable event bus helpers.
module EventBus =

    let create publishAsync subscribe unsubscribe =
        { Publish = publishAsync
          Add = subscribe
          Remove = unsubscribe }

    let publishAsync event bus = bus.Publish(EventRedaction.event event)

    let subscribe consumer bus = bus.Add consumer

    /// Detach the first subscription with the consumer's identity.
    let unsubscribe consumer bus = bus.Remove consumer

    /// A bus that drops every event and has no subscribers (library/test default).
    let none: EventBus = create (fun _ -> Task.CompletedTask) ignore ignore
