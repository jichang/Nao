namespace Nao.Persistence

open System
open System.Threading.Tasks
open Nao.Agents

// ----------------------------------------------------------------------------
// In-memory reference implementations (moved out of Nao.Agents)
// ----------------------------------------------------------------------------

/// In-memory tracer for testing and local development
type InMemoryTracer() =
    let spans = System.Collections.Concurrent.ConcurrentDictionary<SpanId, Span>()

    interface ITracer with
        member _.StartTrace(operationName: string) =
            let span =
                { Id = SpanId(Guid.NewGuid())
                  TraceId = TraceId(Guid.NewGuid())
                  ParentSpanId = None
                  OperationName = operationName
                  StartTime = DateTimeOffset.UtcNow
                  EndTime = None
                  Status = SpanStatus.Ok
                  Attributes = Map.empty
                  Events = [] }
            spans.[span.Id] <- span
            span

        member _.StartSpan (parentSpan: Span) (operationName: string) =
            let span =
                { Id = SpanId(Guid.NewGuid())
                  TraceId = parentSpan.TraceId
                  ParentSpanId = Some parentSpan.Id
                  OperationName = operationName
                  StartTime = DateTimeOffset.UtcNow
                  EndTime = None
                  Status = SpanStatus.Ok
                  Attributes = Map.empty
                  Events = [] }
            spans.[span.Id] <- span
            span

        member _.EndSpan (span: Span) (status: SpanStatus) =
            let updated = { span with EndTime = Some DateTimeOffset.UtcNow; Status = status }
            spans.[span.Id] <- updated

        member _.AddEvent (span: Span) (name: string) (attributes: Map<string, string>) =
            let event = { Name = name; Timestamp = DateTimeOffset.UtcNow; Attributes = attributes }
            let updated = { span with Events = span.Events @ [ event ] }
            spans.[span.Id] <- updated

        member _.SetAttributes (span: Span) (attrs: Map<string, string>) =
            let updated = { span with Attributes = Map.fold (fun acc k v -> Map.add k v acc) span.Attributes attrs }
            spans.[span.Id] <- updated

        member _.GetTrace(traceId: TraceId) =
            spans.Values
            |> Seq.filter (fun s -> s.TraceId = traceId)
            |> Seq.toList

/// In-memory metrics collector
type InMemoryMetricsCollector() =
    let llmLatencies = ResizeArray<int64>()
    let mutable inputTokens = 0
    let mutable outputTokens = 0
    let mutable llmCalls = 0
    let mutable toolCalls = 0
    let mutable llmWaitMs = 0L
    let mutable toolExecMs = 0L
    let startTime = DateTimeOffset.UtcNow

    interface IMetricsCollector with
        member _.RecordLlmCall (inTokens: int) (outTokens: int) (latencyMs: int64) =
            llmCalls <- llmCalls + 1
            inputTokens <- inputTokens + inTokens
            outputTokens <- outputTokens + outTokens
            llmLatencies.Add(latencyMs)
            llmWaitMs <- llmWaitMs + latencyMs

        member _.RecordToolCall (_toolName: string) (durationMs: int64) (_success: bool) =
            toolCalls <- toolCalls + 1
            toolExecMs <- toolExecMs + durationMs

        member _.RecordMetric(_point: MetricPoint) = ()

        member _.GetMetrics() =
            let sortedLatencies = llmLatencies |> Seq.sort |> Seq.toArray
            let avgLatency =
                if sortedLatencies.Length > 0 then
                    sortedLatencies |> Array.averageBy float
                else 0.0
            let p95Latency =
                if sortedLatencies.Length > 0 then
                    let idx = int (float sortedLatencies.Length * 0.95)
                    float sortedLatencies.[min idx (sortedLatencies.Length - 1)]
                else 0.0

            let duration = DateTimeOffset.UtcNow - startTime
            let usage : ResourceUsage =
                { LlmCalls = llmCalls
                  TotalTokens = inputTokens + outputTokens
                  ToolCalls = toolCalls
                  EstimatedCostUsd = 0m
                  ElapsedTime = duration }

            { Usage = usage
              TotalLlmCalls = llmCalls
              TotalInputTokens = inputTokens
              TotalOutputTokens = outputTokens
              TotalCostUsd = 0m
              TotalToolCalls = toolCalls
              AvgLatencyMs = avgLatency
              P95LatencyMs = p95Latency
              TotalDuration = duration
              LlmWaitTime = TimeSpan.FromMilliseconds(float llmWaitMs)
              ToolExecutionTime = TimeSpan.FromMilliseconds(float toolExecMs) }

        member _.EstimateCost(model: CostModel) =
            let inCost = decimal inputTokens / 1000m * model.InputCostPer1K
            let outCost = decimal outputTokens / 1000m * model.OutputCostPer1K
            inCost + outCost

/// In-memory trace store for testing
type InMemoryTraceStore() =
    let traces = System.Collections.Concurrent.ConcurrentDictionary<Guid, ExecutionTrace>()

    interface ITraceStore with
        member _.SaveAsync(trace: ExecutionTrace) =
            traces.[trace.Id] <- trace
            Task.FromResult()

        member _.GetBaselineAsync (agentId: string) (_taskPattern: string) =
            traces.Values
            |> Seq.filter (fun t -> t.AgentId = agentId && t.Success)
            |> Seq.sortByDescending (fun t -> t.StartedAt)
            |> Seq.tryHead
            |> Task.FromResult

        member _.GetTracesAsync (agentId: string) (limit: int) =
            traces.Values
            |> Seq.filter (fun t -> t.AgentId = agentId)
            |> Seq.sortByDescending (fun t -> t.StartedAt)
            |> Seq.truncate limit
            |> Seq.toList
            |> Task.FromResult

// ----------------------------------------------------------------------------
// Tracer
// ----------------------------------------------------------------------------

/// Event-sourced ITracer. Span identifiers are generated internally, so this is a
/// self-contained implementation (mirroring InMemoryTracer) that persists each
/// span upsert as a full span snapshot and rebuilds the span table on load.
type PersistentTracer(store: IEventStore) =
    let spans = System.Collections.Concurrent.ConcurrentDictionary<SpanId, Span>()

    let upsert (span: Span) =
        spans.[span.Id] <- span
        store.Append(FSharpJson.serialize span)

    do
        for line in store.LoadAll() do
            let span = FSharpJson.deserialize<Span> line
            spans.[span.Id] <- span

    interface ITracer with
        member _.StartTrace(operationName: string) =
            let span =
                { Id = SpanId(Guid.NewGuid())
                  TraceId = TraceId(Guid.NewGuid())
                  ParentSpanId = None
                  OperationName = operationName
                  StartTime = DateTimeOffset.UtcNow
                  EndTime = None
                  Status = SpanStatus.Ok
                  Attributes = Map.empty
                  Events = [] }
            upsert span
            span

        member _.StartSpan (parentSpan: Span) (operationName: string) =
            let span =
                { Id = SpanId(Guid.NewGuid())
                  TraceId = parentSpan.TraceId
                  ParentSpanId = Some parentSpan.Id
                  OperationName = operationName
                  StartTime = DateTimeOffset.UtcNow
                  EndTime = None
                  Status = SpanStatus.Ok
                  Attributes = Map.empty
                  Events = [] }
            upsert span
            span

        member _.EndSpan (span: Span) (status: SpanStatus) =
            upsert { span with EndTime = Some DateTimeOffset.UtcNow; Status = status }

        member _.AddEvent (span: Span) (name: string) (attributes: Map<string, string>) =
            let event = { Name = name; Timestamp = DateTimeOffset.UtcNow; Attributes = attributes }
            upsert { span with Events = span.Events @ [ event ] }

        member _.SetAttributes (span: Span) (attrs: Map<string, string>) =
            upsert { span with Attributes = Map.fold (fun acc k v -> Map.add k v acc) span.Attributes attrs }

        member _.GetTrace(traceId: TraceId) =
            spans.Values |> Seq.filter (fun s -> s.TraceId = traceId) |> Seq.toList

/// Factory helpers for tracer persistence.
module Tracers =
    /// ADO.NET-backed tracer over any provider supplied via the connection factory.
    let ado (factory: IDbConnectionFactory) : ITracer = PersistentTracer(EventStore.db factory "tracer") :> ITracer

    /// FileSystem-backed tracer rooted at the given directory.
    let file (baseDir: string) : ITracer =
        PersistentTracer(EventStore.file (System.IO.Path.Combine(baseDir, "tracer.jsonl"))) :> ITracer

// ----------------------------------------------------------------------------
// Metrics collector
// ----------------------------------------------------------------------------

/// Mutating events for metrics persistence.
[<RequireQualifiedAccess>]
type MetricsEvent =
    | LlmCall of inputTokens: int * outputTokens: int * latencyMs: int64
    | ToolCall of toolName: string * durationMs: int64 * success: bool
    | Metric of MetricPoint

/// Event-sourced IMetricsCollector.
type PersistentMetricsCollector(store: IEventStore) =
    let inner = InMemoryMetricsCollector() :> IMetricsCollector

    do
        for line in store.LoadAll() do
            match FSharpJson.deserialize<MetricsEvent> line with
            | MetricsEvent.LlmCall(i, o, l) -> inner.RecordLlmCall i o l
            | MetricsEvent.ToolCall(n, d, s) -> inner.RecordToolCall n d s
            | MetricsEvent.Metric p -> inner.RecordMetric p

    interface IMetricsCollector with
        member _.RecordLlmCall (inputTokens: int) (outputTokens: int) (latencyMs: int64) =
            inner.RecordLlmCall inputTokens outputTokens latencyMs
            store.Append(FSharpJson.serialize (MetricsEvent.LlmCall(inputTokens, outputTokens, latencyMs)))

        member _.RecordToolCall (toolName: string) (durationMs: int64) (success: bool) =
            inner.RecordToolCall toolName durationMs success
            store.Append(FSharpJson.serialize (MetricsEvent.ToolCall(toolName, durationMs, success)))

        member _.RecordMetric(point: MetricPoint) =
            inner.RecordMetric point
            store.Append(FSharpJson.serialize (MetricsEvent.Metric point))

        member _.GetMetrics() = inner.GetMetrics()

        member _.EstimateCost(model: CostModel) = inner.EstimateCost model

/// Factory helpers for metrics collector persistence.
module MetricsCollectors =
    /// ADO.NET-backed metrics collector over any provider supplied via the connection factory.
    let ado (factory: IDbConnectionFactory) : IMetricsCollector =
        PersistentMetricsCollector(EventStore.db factory "metrics") :> IMetricsCollector

    /// FileSystem-backed metrics collector rooted at the given directory.
    let file (baseDir: string) : IMetricsCollector =
        PersistentMetricsCollector(EventStore.file (System.IO.Path.Combine(baseDir, "metrics.jsonl"))) :> IMetricsCollector

// ----------------------------------------------------------------------------
// Trace store (regression baselines)
// ----------------------------------------------------------------------------

/// Mutating events for trace-store persistence.
[<RequireQualifiedAccess>]
type TraceStoreEvent = Save of ExecutionTrace

/// Event-sourced ITraceStore.
type PersistentTraceStore(store: IEventStore) =
    let inner = InMemoryTraceStore() :> ITraceStore

    do
        for line in store.LoadAll() do
            match FSharpJson.deserialize<TraceStoreEvent> line with
            | TraceStoreEvent.Save t -> inner.SaveAsync(t).GetAwaiter().GetResult()

    interface ITraceStore with
        member _.SaveAsync(trace: ExecutionTrace) =
            task {
                do! inner.SaveAsync trace
                store.Append(FSharpJson.serialize (TraceStoreEvent.Save trace))
            }

        member _.GetBaselineAsync (agentId: string) (taskPattern: string) =
            inner.GetBaselineAsync agentId taskPattern

        member _.GetTracesAsync (agentId: string) (limit: int) = inner.GetTracesAsync agentId limit

/// Factory helpers for trace store persistence.
module TraceStores =
    /// ADO.NET-backed trace store over any provider supplied via the connection factory.
    let ado (factory: IDbConnectionFactory) : ITraceStore =
        PersistentTraceStore(EventStore.db factory "trace-store") :> ITraceStore

    /// FileSystem-backed trace store rooted at the given directory.
    let file (baseDir: string) : ITraceStore =
        PersistentTraceStore(EventStore.file (System.IO.Path.Combine(baseDir, "trace-store.jsonl"))) :> ITraceStore
