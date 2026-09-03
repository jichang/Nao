namespace Nao.Persistence

open System
open System.Threading.Tasks
open Nao.Agents

module private TracerState =
    let create (persist: Span -> unit) (initial: Span seq) : Tracer =
        let spans = System.Collections.Concurrent.ConcurrentDictionary<SpanId, Span>()
        for span in initial do spans.[span.Id] <- span
        let upsert (span: Span) = spans.[span.Id] <- span; persist span
        { StartTrace = fun operationName ->
              let span = { Id = SpanId(Guid.NewGuid()); TraceId = TraceId(Guid.NewGuid()); ParentSpanId = None; OperationName = operationName; StartTime = DateTimeOffset.UtcNow; EndTime = None; Status = SpanStatus.Ok; Attributes = Map.empty; Events = [] }
              upsert span; span
          StartSpan = fun parent operationName ->
              let span = { Id = SpanId(Guid.NewGuid()); TraceId = parent.TraceId; ParentSpanId = Some parent.Id; OperationName = operationName; StartTime = DateTimeOffset.UtcNow; EndTime = None; Status = SpanStatus.Ok; Attributes = Map.empty; Events = [] }
              upsert span; span
          EndSpan = fun span status -> upsert { span with EndTime = Some DateTimeOffset.UtcNow; Status = status }
          AddEvent = fun span name attributes -> upsert { span with Events = span.Events @ [ { Name = name; Timestamp = DateTimeOffset.UtcNow; Attributes = attributes } ] }
          SetAttributes = fun span attributes -> upsert { span with Attributes = Map.fold (fun state key value -> Map.add key value state) span.Attributes attributes }
          GetTrace = fun traceId -> spans.Values |> Seq.filter (fun span -> span.TraceId = traceId) |> Seq.toList }

module InMemoryTracer =
    let create () = TracerState.create ignore Seq.empty

module InMemoryMetricsCollector =
    let create () : MetricsCollector =
        let latencies = ResizeArray<int64>()
        let mutable inputs, outputs, llmCalls, toolCalls = 0, 0, 0, 0
        let mutable llmWait, toolTime = 0L, 0L
        let started = DateTimeOffset.UtcNow
        let llm input output latency = inputs <- inputs + input; outputs <- outputs + output; llmCalls <- llmCalls + 1; latencies.Add latency; llmWait <- llmWait + latency
        let tool (_: string) duration (_: bool) = toolCalls <- toolCalls + 1; toolTime <- toolTime + duration
        let metrics () =
            let sorted = latencies |> Seq.sort |> Seq.toArray
            let duration = DateTimeOffset.UtcNow - started
            let average = if sorted.Length = 0 then 0.0 else sorted |> Array.averageBy float
            let p95 = if sorted.Length = 0 then 0.0 else float sorted.[min (int (float sorted.Length * 0.95)) (sorted.Length - 1)]
            let usage = { LlmCalls = llmCalls; TotalTokens = inputs + outputs; ToolCalls = toolCalls; EstimatedCostUsd = 0m; ElapsedTime = duration }
            { Usage = usage; TotalLlmCalls = llmCalls; TotalInputTokens = inputs; TotalOutputTokens = outputs; TotalCostUsd = 0m; TotalToolCalls = toolCalls; AvgLatencyMs = average; P95LatencyMs = p95; TotalDuration = duration; LlmWaitTime = TimeSpan.FromMilliseconds(float llmWait); ToolExecutionTime = TimeSpan.FromMilliseconds(float toolTime) }
        { RecordLlmCall = llm; RecordToolCall = tool; RecordMetric = ignore; GetMetrics = metrics; EstimateCost = fun model -> decimal inputs / 1000m * model.InputCostPer1K + decimal outputs / 1000m * model.OutputCostPer1K }

module InMemoryTraceStore =
    let create () : TraceStore =
        let traces = System.Collections.Concurrent.ConcurrentDictionary<Guid, ExecutionTrace>()
        { SaveAsync = fun trace -> traces.[trace.Id] <- trace; Task.FromResult()
          GetBaselineAsync = fun agentId _ -> traces.Values |> Seq.filter (fun trace -> trace.AgentId = agentId && trace.Success) |> Seq.sortByDescending (fun trace -> trace.StartedAt) |> Seq.tryHead |> Task.FromResult
          GetTracesAsync = fun agentId limit -> traces.Values |> Seq.filter (fun trace -> trace.AgentId = agentId) |> Seq.sortByDescending (fun trace -> trace.StartedAt) |> Seq.truncate limit |> Seq.toList |> Task.FromResult }

module PersistentTracer =
    let create (store: EventStore) = store.LoadAll() |> Seq.map FSharpJson.deserialize<Span> |> TracerState.create (FSharpJson.serialize >> store.Append)
module Tracers =
    let ado factory = PersistentTracer.create (EventStore.db factory "tracer")
    let file baseDir = PersistentTracer.create (EventStore.file (System.IO.Path.Combine(baseDir, "tracer.jsonl")))

[<RequireQualifiedAccess>]
type MetricsEvent = LlmCall of int * int * int64 | ToolCall of string * int64 * bool | Metric of MetricPoint
module PersistentMetricsCollector =
    let create (store: EventStore) : MetricsCollector =
        let inner = InMemoryMetricsCollector.create ()
        for line in store.LoadAll() do
            match FSharpJson.deserialize<MetricsEvent> line with
            | MetricsEvent.LlmCall(i, o, l) -> inner.RecordLlmCall i o l
            | MetricsEvent.ToolCall(n, d, s) -> inner.RecordToolCall n d s
            | MetricsEvent.Metric p -> inner.RecordMetric p
        { RecordLlmCall = fun i o l -> inner.RecordLlmCall i o l; store.Append(FSharpJson.serialize (MetricsEvent.LlmCall(i, o, l)))
          RecordToolCall = fun n d s -> inner.RecordToolCall n d s; store.Append(FSharpJson.serialize (MetricsEvent.ToolCall(n, d, s)))
          RecordMetric = fun p -> inner.RecordMetric p; store.Append(FSharpJson.serialize (MetricsEvent.Metric p))
          GetMetrics = inner.GetMetrics; EstimateCost = inner.EstimateCost }
module MetricsCollectors =
    let ado factory = PersistentMetricsCollector.create (EventStore.db factory "metrics")
    let file baseDir = PersistentMetricsCollector.create (EventStore.file (System.IO.Path.Combine(baseDir, "metrics.jsonl")))

[<RequireQualifiedAccess>]
type TraceStoreEvent = Save of ExecutionTrace
module PersistentTraceStore =
    let create (store: EventStore) : TraceStore =
        let inner = InMemoryTraceStore.create ()
        for line in store.LoadAll() do match FSharpJson.deserialize<TraceStoreEvent> line with TraceStoreEvent.Save trace -> inner.SaveAsync(trace).GetAwaiter().GetResult()
        { SaveAsync = fun trace ->
              task {
                  do! inner.SaveAsync trace
                  store.Append(FSharpJson.serialize (TraceStoreEvent.Save trace))
              }
          GetBaselineAsync = inner.GetBaselineAsync
          GetTracesAsync = inner.GetTracesAsync }
module TraceStores =
    let ado factory = PersistentTraceStore.create (EventStore.db factory "trace-store")
    let file baseDir = PersistentTraceStore.create (EventStore.file (System.IO.Path.Combine(baseDir, "trace-store.jsonl")))
