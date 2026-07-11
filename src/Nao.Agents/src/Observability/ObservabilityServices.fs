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
    let emit (bus: IEventBus) (sessionKey: string) (turnId: string) (signal: ObservabilitySignal) =
        bus.PublishAsync(ObservabilityCaptured(buildScope sessionKey turnId, signal)) |> ignore

/// Tee tracer: writes go to the real backing tracer (so span threading and GetTrace stay
/// correct) and ALSO publish a span signal to the bus. Reads delegate to the backing.
type private PublishingTracer(sessionKey: string, turnId: string, bus: IEventBus, inner: ITracer) =
    interface ITracer with
        member _.StartTrace(operationName) =
            let span = inner.StartTrace operationName
            Observability.emit bus sessionKey turnId (SpanStarted span)
            span
        member _.StartSpan parentSpan operationName =
            let span = inner.StartSpan parentSpan operationName
            Observability.emit bus sessionKey turnId (SpanStarted span)
            span
        member _.EndSpan span status =
            inner.EndSpan span status
            Observability.emit bus sessionKey turnId (SpanEnded(span, status))
        member _.AddEvent span name attributes =
            inner.AddEvent span name attributes
            Observability.emit bus sessionKey turnId (SpanEventAdded(span, name, attributes))
        member _.SetAttributes span attributes =
            inner.SetAttributes span attributes
            Observability.emit bus sessionKey turnId (SpanAttributesSet(span, attributes))
        member _.GetTrace traceId = inner.GetTrace traceId

/// Tee metrics collector: records to the backing collector (so GetMetrics/EstimateCost
/// aggregations stay correct) and publishes a metric signal.
type private PublishingMetrics(sessionKey: string, turnId: string, bus: IEventBus, inner: IMetricsCollector) =
    interface IMetricsCollector with
        member _.RecordLlmCall inputTokens outputTokens latencyMs =
            inner.RecordLlmCall inputTokens outputTokens latencyMs
            Observability.emit bus sessionKey turnId (LlmCallRecorded(inputTokens, outputTokens, latencyMs))
        member _.RecordToolCall toolName durationMs success =
            inner.RecordToolCall toolName durationMs success
            Observability.emit bus sessionKey turnId (ToolCallRecorded(toolName, durationMs, success))
        member _.RecordMetric point =
            inner.RecordMetric point
            Observability.emit bus sessionKey turnId (MetricRecorded point)
        member _.GetMetrics() = inner.GetMetrics()
        member _.EstimateCost costModel = inner.EstimateCost costModel

/// Tee execution journal: persists to the backing journal (so revert reads work) and
/// publishes a record/revert signal after the write completes.
type private PublishingJournal(sessionKey: string, turnId: string, bus: IEventBus, inner: IExecutionJournal) =
    interface IExecutionJournal with
        member _.RecordAsync record =
            task {
                do! inner.RecordAsync record
                do! bus.PublishAsync(ObservabilityCaptured(Observability.buildScope sessionKey turnId, ExecutionRecorded record))
            }
            :> Task
        member _.GetHistoryAsync() = inner.GetHistoryAsync()
        member _.GetRevertibleAsync() = inner.GetRevertibleAsync()
        member _.MarkRevertedAsync record =
            task {
                do! inner.MarkRevertedAsync record
                do! bus.PublishAsync(ObservabilityCaptured(Observability.buildScope sessionKey turnId, ExecutionReverted record))
            }
            :> Task

/// Tee trace store: saves to the backing store (so GetBaselineAsync regression reads work)
/// and publishes a trace-saved signal.
type private PublishingTraceStore(sessionKey: string, turnId: string, bus: IEventBus, inner: ITraceStore) =
    interface ITraceStore with
        member _.SaveAsync trace =
            task {
                do! inner.SaveAsync trace
                do! bus.PublishAsync(ObservabilityCaptured(Observability.buildScope sessionKey turnId, TraceSaved trace))
            }
        member _.GetBaselineAsync agentId taskPattern = inner.GetBaselineAsync agentId taskPattern
        member _.GetTracesAsync agentId limit = inner.GetTracesAsync agentId limit

/// Tee audit log: records to the backing log (so queries work) and publishes an
/// audit-recorded signal.
type private PublishingAuditLog(sessionKey: string, turnId: string, bus: IEventBus, inner: IAuditLog) =
    interface IAuditLog with
        member _.RecordAsync entry =
            task {
                do! inner.RecordAsync entry
                do! bus.PublishAsync(ObservabilityCaptured(Observability.buildScope sessionKey turnId, AuditRecorded entry))
            }
        member _.QueryAsync agentId since = inner.QueryAsync agentId since
        member _.QueryByExecutionAsync executionId = inner.QueryByExecutionAsync executionId
        member _.GetDeniedCountAsync agentId since = inner.GetDeniedCountAsync agentId since

/// An IHarnessServices bundle whose every write is teed to the bus as an ObservabilityCaptured
/// event while reads delegate to the wrapped backing bundle. The grain hands this to the agent
/// harness, so the full observability stream flows through the bus without the producer ever
/// deciding where it is stored.
type PublishingHarnessServices(sessionKey: string, turnId: string, bus: IEventBus, backing: IHarnessServices) =
    let tracer = backing.Tracer |> Option.map (fun t -> PublishingTracer(sessionKey, turnId, bus, t) :> ITracer)
    let metrics = backing.Metrics |> Option.map (fun m -> PublishingMetrics(sessionKey, turnId, bus, m) :> IMetricsCollector)
    let journal = backing.ExecutionJournal |> Option.map (fun j -> PublishingJournal(sessionKey, turnId, bus, j) :> IExecutionJournal)
    let traceStore = backing.TraceStore |> Option.map (fun s -> PublishingTraceStore(sessionKey, turnId, bus, s) :> ITraceStore)
    let auditLog = backing.AuditLog |> Option.map (fun a -> PublishingAuditLog(sessionKey, turnId, bus, a) :> IAuditLog)

    interface IHarnessServices with
        member _.Tracer = tracer
        member _.Metrics = metrics
        member _.ExecutionJournal = journal
        member _.TraceStore = traceStore
        member _.AuditLog = auditLog

/// Builds the per-turn IHarnessServices bundle handed to the agent harness. Each session's
/// observability lives in its own backing bundle (e.g. sessions/<key>/observability/), built
/// lazily by `backingFactory` and memoised; the returned bundle tees every write to the bus
/// while reads delegate to that backing store. Where the data lands is the store-level swap
/// point (the backing factory), so producers never change.
type ObservabilityServices(bus: IEventBus, backingFactory: string -> IHarnessServices) =
    let backings = ConcurrentDictionary<string, IHarnessServices>()
    let backingFor (sessionKey: string) = backings.GetOrAdd(sessionKey, fun k -> backingFactory k)

    /// The per-turn harness-services bundle for a session: writes are teed to the bus, reads
    /// hit the session's backing store. `turnId` stamps each published signal with its turn.
    member _.ServicesFor(sessionKey: string, turnId: string) : IHarnessServices =
        PublishingHarnessServices(sessionKey, turnId, bus, backingFor sessionKey) :> IHarnessServices