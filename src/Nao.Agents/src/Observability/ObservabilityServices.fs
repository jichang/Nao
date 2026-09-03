namespace Nao.Agents

open System.Collections.Concurrent
open System.Threading.Tasks

/// Internal helpers for building the event scope of an observability signal and publishing
/// it. The per-turn id is supplied explicitly by the bundle that owns these helpers (built
/// per turn by the session grain) so each signal is attributed to the turn that produced it.
module private Observability =

    let buildScope (sessionKey: string) (turnId: string) : EventScope =
        let userId, sessionId =
            match sessionKey.IndexOf('/') with
            | i when i >= 0 -> sessionKey.Substring(0, i), sessionKey.Substring(i + 1)
            | _ -> sessionKey, sessionKey
        EventScope.Create(userId, sessionId, "", "", turnId, sessionKey)

    /// Fire-and-forget publish for the synchronous (unit-returning) sinks. Safe to ignore:
    /// reads always go to the wrapped backing store, and InMemoryEventBus isolates a failing
    /// consumer, so a subscriber can never break the producer's turn.
    let emit (bus: EventBus) (sessionKey: string) (turnId: string) (signal: ObservabilitySignal) =
        EventBus.publishAsync (ObservabilityCaptured(buildScope sessionKey turnId, signal)) bus |> ignore

/// Functional publishing decorators for observability capabilities.
module private Publishing =
  let tracer sessionKey turnId bus (inner: Tracer) : Tracer =
    { StartTrace = fun operationName ->
            let span = inner.StartTrace operationName
            Observability.emit bus sessionKey turnId (SpanStarted span)
            span
      StartSpan = fun parentSpan operationName ->
            let span = inner.StartSpan parentSpan operationName
            Observability.emit bus sessionKey turnId (SpanStarted span)
            span
      EndSpan = fun span status ->
            inner.EndSpan span status
            Observability.emit bus sessionKey turnId (SpanEnded(span, status))
      AddEvent = fun span name attributes ->
            inner.AddEvent span name attributes
            Observability.emit bus sessionKey turnId (SpanEventAdded(span, name, attributes))
      SetAttributes = fun span attributes ->
            inner.SetAttributes span attributes
            Observability.emit bus sessionKey turnId (SpanAttributesSet(span, attributes))
      GetTrace = inner.GetTrace }

  let metrics sessionKey turnId bus (inner: MetricsCollector) : MetricsCollector =
    { RecordLlmCall = fun inputTokens outputTokens latencyMs ->
            inner.RecordLlmCall inputTokens outputTokens latencyMs
            Observability.emit bus sessionKey turnId (LlmCallRecorded(inputTokens, outputTokens, latencyMs))
      RecordToolCall = fun toolName durationMs success ->
            inner.RecordToolCall toolName durationMs success
            Observability.emit bus sessionKey turnId (ToolCallRecorded(toolName, durationMs, success))
      RecordMetric = fun point ->
            inner.RecordMetric point
            Observability.emit bus sessionKey turnId (MetricRecorded point)
      GetMetrics = inner.GetMetrics
      EstimateCost = inner.EstimateCost }

  let journal sessionKey turnId bus (inner: ExecutionJournal) : ExecutionJournal =
    { RecordAsync = fun record ->
            task {
                do! inner.RecordAsync record
                do! EventBus.publishAsync (ObservabilityCaptured(Observability.buildScope sessionKey turnId, ExecutionRecorded record)) bus
            }
            :> Task
      GetHistoryAsync = inner.GetHistoryAsync
      GetRevertibleAsync = inner.GetRevertibleAsync
      MarkRevertedAsync = fun record ->
            task {
                do! inner.MarkRevertedAsync record
                do! EventBus.publishAsync (ObservabilityCaptured(Observability.buildScope sessionKey turnId, ExecutionReverted record)) bus
            }
            :> Task }

  let traceStore sessionKey turnId bus (inner: TraceStore) : TraceStore =
    { SaveAsync = fun trace ->
            task {
                do! inner.SaveAsync trace
                do! EventBus.publishAsync (ObservabilityCaptured(Observability.buildScope sessionKey turnId, TraceSaved trace)) bus
            }
      GetBaselineAsync = inner.GetBaselineAsync
      GetTracesAsync = inner.GetTracesAsync }

  let auditLog sessionKey turnId bus (inner: AuditLog) : AuditLog =
    { RecordAsync = fun entry ->
            task {
                do! inner.RecordAsync entry
                do! EventBus.publishAsync (ObservabilityCaptured(Observability.buildScope sessionKey turnId, AuditRecorded entry)) bus
            }
      QueryAsync = inner.QueryAsync
      QueryByExecutionAsync = inner.QueryByExecutionAsync
      GetDeniedCountAsync = inner.GetDeniedCountAsync }

/// Build a harness-services bundle whose every write is teed to the bus as an ObservabilityCaptured
/// event while reads delegate to the wrapped backing bundle. The grain hands this to the agent
/// harness, so the full observability stream flows through the bus without the producer ever
/// deciding where it is stored.
module PublishingHarnessServices =
    let create sessionKey turnId bus (backing: HarnessServices) : HarnessServices =
                let tracer = backing.Tracer |> Option.map (Publishing.tracer sessionKey turnId bus)
                let metrics = backing.Metrics |> Option.map (Publishing.metrics sessionKey turnId bus)
                let journal = backing.ExecutionJournal |> Option.map (Publishing.journal sessionKey turnId bus)
                let traceStore = backing.TraceStore |> Option.map (Publishing.traceStore sessionKey turnId bus)
                let auditLog = backing.AuditLog |> Option.map (Publishing.auditLog sessionKey turnId bus)
                HarnessServices.create tracer metrics journal traceStore auditLog

/// Functional facade for obtaining per-turn harness services.
type ObservabilityServices =
    { ServicesFor: string -> string -> HarnessServices }

/// Builds the per-turn harness-services bundle handed to the agent harness. Each session's
/// observability lives in its own backing bundle (e.g. sessions/<key>/observability/), built
/// lazily by `backingFactory` and memoised; the returned bundle tees every write to the bus
/// while reads delegate to that backing store. Where the data lands is the store-level swap
/// point (the backing factory), so producers never change.
module ObservabilityServices =
    let create (bus: EventBus) (backingFactory: string -> HarnessServices) =
        let backings = ConcurrentDictionary<string, HarnessServices>()
        let backingFor sessionKey = backings.GetOrAdd(sessionKey, fun key -> backingFactory key)

        { ServicesFor =
            fun sessionKey turnId ->
                PublishingHarnessServices.create sessionKey turnId bus (backingFor sessionKey) }