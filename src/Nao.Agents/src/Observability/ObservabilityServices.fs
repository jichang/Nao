namespace Nao.Agents

open System.Collections.Concurrent
open System.Threading.Tasks

/// Internal helpers for building the event scope of an observability signal and publishing
/// it. The per-turn id is supplied explicitly by the bundle that owns these helpers (built
/// per turn by the session grain) so each signal is attributed to the turn that produced it.
module private Observability =

    let buildScope (sessionKey: string) (turnId: string) (correlation: CorrelationContext) : EventScope =
        let userId, sessionId =
            match sessionKey.IndexOf('/') with
            | i when i >= 0 -> sessionKey.Substring(0, i), sessionKey.Substring(i + 1)
            | _ -> sessionKey, sessionKey

        EventScope.Create(userId, sessionId, "", "", turnId, sessionKey, correlation)

    /// Fire-and-forget publish for the synchronous (unit-returning) sinks. Safe to ignore:
    /// reads always go to the wrapped backing store, and InMemoryEventBus isolates a failing
    /// consumer, so a subscriber can never break the producer's turn.
    let emit
        (bus: EventBus)
        (sessionKey: string)
        (turnId: string)
        (correlation: CorrelationContext)
        (signal: ObservabilitySignal)
        =
        EventBus.publishAsync (ObservabilityCaptured(buildScope sessionKey turnId correlation, signal)) bus
        |> ignore

/// Functional publishing decorators for observability capabilities.
module private Publishing =
    let tracer sessionKey turnId correlation bus (inner: Tracer) : Tracer =
        { StartTrace =
            fun activeCorrelation operationName ->
                let span = inner.StartTrace activeCorrelation operationName
                Observability.emit bus sessionKey turnId activeCorrelation (SpanStarted span)
                span
          StartSpan =
            fun parentSpan operationName ->
                let span = inner.StartSpan parentSpan operationName
                Observability.emit bus sessionKey turnId span.Correlation (SpanStarted span)
                span
          EndSpan =
            fun span status ->
                inner.EndSpan span status
                Observability.emit bus sessionKey turnId span.Correlation (SpanEnded(span, status))
          AddEvent =
            fun span name attributes ->
                inner.AddEvent span name attributes
                Observability.emit bus sessionKey turnId span.Correlation (SpanEventAdded(span, name, attributes))
          SetAttributes =
            fun span attributes ->
                inner.SetAttributes span attributes
                Observability.emit bus sessionKey turnId span.Correlation (SpanAttributesSet(span, attributes))
          GetTrace = inner.GetTrace
          GetByExecution = inner.GetByExecution }

    let metrics sessionKey turnId correlation bus (inner: MetricsCollector) : MetricsCollector =
        { Record =
            fun record ->
                inner.Record record
                Observability.emit bus sessionKey turnId correlation (MetricRecorded record)
          GetMetrics = inner.GetMetrics
          GetByExecution = inner.GetByExecution
          EstimateCost = inner.EstimateCost
          DeleteOwnerAsync = inner.DeleteOwnerAsync
          DeleteExpiredAsync = inner.DeleteExpiredAsync }

    let journal sessionKey turnId correlation bus (inner: ExecutionJournal) : ExecutionJournal =
        { RecordAsync =
            fun record ->
                task {
                    do! inner.RecordAsync record

                    do!
                        EventBus.publishAsync
                            (ObservabilityCaptured(
                                Observability.buildScope sessionKey turnId correlation,
                                ExecutionRecorded record
                            ))
                            bus
                }
                :> Task
          GetHistoryAsync = inner.GetHistoryAsync
          GetByExecutionAsync = inner.GetByExecutionAsync
          GetRevertibleAsync = inner.GetRevertibleAsync
          MarkRevertedAsync =
            fun recordId ->
                task {
                    do! inner.MarkRevertedAsync recordId

                    do!
                        EventBus.publishAsync
                            (ObservabilityCaptured(
                                Observability.buildScope sessionKey turnId correlation,
                                ExecutionReverted recordId
                            ))
                            bus
                }
                :> Task
          DeleteOwnerAsync = inner.DeleteOwnerAsync
          DeleteExpiredAsync = inner.DeleteExpiredAsync
          Checkpoints = inner.Checkpoints }

    let traceStore sessionKey turnId correlation bus (inner: TraceStore) : TraceStore =
        let saveAsync trace =
            task {
                do! inner.SaveAsync trace

                do!
                    EventBus.publishAsync
                        (ObservabilityCaptured(Observability.buildScope sessionKey turnId correlation, TraceSaved trace))
                        bus
            }

        { SaveAsync = saveAsync
          GetBaselineAsync = inner.GetBaselineAsync
          GetTracesAsync = inner.GetTracesAsync
          GetByExecutionAsync = inner.GetByExecutionAsync
          DeleteOwnerAsync = inner.DeleteOwnerAsync
          DeleteExpiredAsync = inner.DeleteExpiredAsync }

    let auditLog sessionKey turnId correlation bus (inner: AuditLog) : AuditLog =
        let recordAsync entry =
            task {
                do! inner.RecordAsync entry

                do!
                    EventBus.publishAsync
                        (ObservabilityCaptured(
                            Observability.buildScope sessionKey turnId correlation,
                            AuditRecorded entry
                        ))
                        bus
            }

        { RecordAsync = recordAsync
          QueryAsync = inner.QueryAsync
          QueryByExecutionAsync = inner.QueryByExecutionAsync
          GetDeniedCountAsync = inner.GetDeniedCountAsync
          DeleteOwnerAsync = inner.DeleteOwnerAsync
          DeleteExpiredAsync = inner.DeleteExpiredAsync }

/// Build a harness-services bundle whose every write is teed to the bus as an ObservabilityCaptured
/// event while reads delegate to the wrapped backing bundle. The grain hands this to the agent
/// harness, so the full observability stream flows through the bus without the producer ever
/// deciding where it is stored.
module PublishingHarnessServices =
    let create sessionKey turnId correlation bus (backing: HarnessServices) : HarnessServices =
        let tracer =
            backing.Tracer
            |> Option.map (Publishing.tracer sessionKey turnId correlation bus)

        let metrics =
            backing.Metrics
            |> Option.map (Publishing.metrics sessionKey turnId correlation bus)

        let journal =
            backing.ExecutionJournal
            |> Option.map (Publishing.journal sessionKey turnId correlation bus)

        let traceStore =
            backing.TraceStore
            |> Option.map (Publishing.traceStore sessionKey turnId correlation bus)

        let auditLog =
            backing.AuditLog
            |> Option.map (Publishing.auditLog sessionKey turnId correlation bus)

        HarnessServices.create tracer metrics journal traceStore auditLog

/// Functional facade for obtaining per-turn harness services.
type ObservabilityServices =
    { ServicesFor: string -> string -> CorrelationContext -> HarnessServices }

/// Builds the per-turn harness-services bundle handed to the agent harness. Each session's
/// observability lives in its own backing bundle (e.g. sessions/<key>/observability/), built
/// lazily by `backingFactory` and memoised; the returned bundle tees every write to the bus
/// while reads delegate to that backing store. Where the data lands is the store-level swap
/// point (the backing factory), so producers never change.
module ObservabilityServices =
    let create (bus: EventBus) (backingFactory: string -> HarnessServices) =
        let backings = ConcurrentDictionary<string, HarnessServices>()

        let backingFor sessionKey =
            backings.GetOrAdd(sessionKey, fun key -> backingFactory key)

        { ServicesFor =
            fun sessionKey turnId correlation ->
                PublishingHarnessServices.create sessionKey turnId correlation bus (backingFor sessionKey) }
