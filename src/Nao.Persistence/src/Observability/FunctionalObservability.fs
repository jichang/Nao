namespace Nao.Persistence

open System
open System.Threading.Tasks
open Nao.Agents

module private TracerState =
    let create (persist: Span -> unit) (initial: Span seq) : Tracer =
        let spans = System.Collections.Concurrent.ConcurrentDictionary<SpanId, Span>()

        for span in initial do
            spans.[span.Id] <- span

        let upsert (span: Span) =
            spans.[span.Id] <- span
            persist span

        { StartTrace =
            fun operationName ->
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
          StartSpan =
            fun parent operationName ->
                let span =
                    { Id = SpanId(Guid.NewGuid())
                      TraceId = parent.TraceId
                      ParentSpanId = Some parent.Id
                      OperationName = operationName
                      StartTime = DateTimeOffset.UtcNow
                      EndTime = None
                      Status = SpanStatus.Ok
                      Attributes = Map.empty
                      Events = [] }

                upsert span
                span
          EndSpan =
            fun span status ->
                upsert
                    { span with
                        EndTime = Some DateTimeOffset.UtcNow
                        Status = status }
          AddEvent =
            fun span name attributes ->
                upsert
                    { span with
                        Events =
                            span.Events
                            @ [ { Name = name
                                  Timestamp = DateTimeOffset.UtcNow
                                  Attributes = attributes } ] }
          SetAttributes =
            fun span attributes ->
                upsert
                    { span with
                        Attributes =
                            Map.fold (fun state key value -> Map.add key value state) span.Attributes attributes }
          GetTrace = fun traceId -> spans.Values |> Seq.filter (fun span -> span.TraceId = traceId) |> Seq.toList }

module InMemoryTracer =
    let create () = TracerState.create ignore Seq.empty

module InMemoryMetricsCollector =
    let create () : MetricsCollector =
        let records =
            System.Collections.Concurrent.ConcurrentDictionary<Guid, MetricRecord>()

        let validateOwner owner =
            if String.IsNullOrWhiteSpace owner then
                invalidArg (nameof owner) "Metric owner cannot be blank."

        let record (metric: MetricRecord) =
            validateOwner metric.Owner
            records.[metric.Id] <- metric

        let owned owner =
            records.Values |> Seq.filter (fun metric -> metric.Owner = owner) |> Seq.toArray

        let metrics owner =
            validateOwner owner
            let retained = owned owner

            let llmCalls =
                retained
                |> Array.choose (fun metric ->
                    match metric.Payload with
                    | MetricPayload.LlmCall(input, output, latency) -> Some(input, output, latency)
                    | _ -> None)

            let toolCalls =
                retained
                |> Array.choose (fun metric ->
                    match metric.Payload with
                    | MetricPayload.ToolCall(_, duration, _) -> Some duration
                    | _ -> None)

            let inputs = llmCalls |> Array.sumBy (fun (input, _, _) -> input)
            let outputs = llmCalls |> Array.sumBy (fun (_, output, _) -> output)
            let sorted = llmCalls |> Array.map (fun (_, _, latency) -> latency) |> Array.sort
            let llmWait = sorted |> Array.sum
            let toolTime = toolCalls |> Array.sum

            let duration =
                if retained.Length < 2 then
                    TimeSpan.Zero
                else
                    let timestamps = retained |> Array.map (fun metric -> metric.Timestamp)
                    Array.max timestamps - Array.min timestamps

            let average =
                if sorted.Length = 0 then
                    0.0
                else
                    sorted |> Array.averageBy float

            let p95 =
                if sorted.Length = 0 then
                    0.0
                else
                    float sorted.[min (int (float sorted.Length * 0.95)) (sorted.Length - 1)]

            let usage: ResourceUsage =
                { LlmCalls = llmCalls.Length
                  TotalTokens = inputs + outputs
                  ToolCalls = toolCalls.Length
                  EstimatedCostUsd = 0m
                  ElapsedTime = duration }

            { Usage = usage
              TotalLlmCalls = llmCalls.Length
              TotalInputTokens = inputs
              TotalOutputTokens = outputs
              TotalCostUsd = 0m
              TotalToolCalls = toolCalls.Length
              AvgLatencyMs = average
              P95LatencyMs = p95
              TotalDuration = duration
              LlmWaitTime = TimeSpan.FromMilliseconds(float llmWait)
              ToolExecutionTime = TimeSpan.FromMilliseconds(float toolTime) }

        let delete owner (predicate: MetricRecord -> bool) =
            task {
                if String.IsNullOrWhiteSpace owner then
                    return
                        Error(
                            PlatformFailure.create
                                PlatformErrorCategory.InvalidInput
                                "Metric owner cannot be blank."
                                false
                                None
                        )
                else
                    let mutable deleted = 0

                    for metric in records.Values do
                        if metric.Owner = owner && predicate metric then
                            match records.TryRemove metric.Id with
                            | true, _ -> deleted <- deleted + 1
                            | false, _ -> ()

                    return Ok deleted
            }

        { Record = record
          GetMetrics = metrics
          EstimateCost =
            fun owner model ->
                let aggregate = metrics owner

                decimal aggregate.TotalInputTokens / 1000m * model.InputCostPer1K
                + decimal aggregate.TotalOutputTokens / 1000m * model.OutputCostPer1K
          DeleteOwnerAsync = fun owner -> delete owner (fun _ -> true)
          DeleteExpiredAsync = fun owner before -> delete owner (fun metric -> metric.Timestamp < before) }

module private TraceOperations =
    let failure = PlatformFailure.fromException PlatformFailureBoundary.Storage None

    let protect owner operation =
        task {
            if String.IsNullOrWhiteSpace owner then
                return
                    Error(
                        PlatformFailure.create
                            PlatformErrorCategory.InvalidInput
                            "Trace owner cannot be blank."
                            false
                            None
                    )
            else
                try
                    let! count = operation ()
                    return Ok count
                with ex ->
                    return Error(failure ex)
        }

module InMemoryTraceStore =
    let create () : TraceStore =
        let traces =
            System.Collections.Concurrent.ConcurrentDictionary<Guid, ExecutionTrace>()

        let delete (predicate: ExecutionTrace -> bool) =
            let mutable deleted = 0

            for trace in traces.Values do
                if predicate trace then
                    match traces.TryRemove trace.Id with
                    | true, _ -> deleted <- deleted + 1
                    | false, _ -> ()

            Task.FromResult deleted

        let saveAsync (trace: ExecutionTrace) =
            traces.[trace.Id] <- trace
            Task.FromResult()

        let getBaselineAsync (agentId: string) (_: string) =
            traces.Values
            |> Seq.filter (fun trace -> trace.AgentId = agentId && trace.Success)
            |> Seq.sortByDescending (fun trace -> trace.StartedAt)
            |> Seq.tryHead
            |> Task.FromResult

        let getTracesAsync (agentId: string) limit =
            traces.Values
            |> Seq.filter (fun trace -> trace.AgentId = agentId)
            |> Seq.sortByDescending (fun trace -> trace.StartedAt)
            |> Seq.truncate limit
            |> Seq.toList
            |> Task.FromResult

        let deleteOwnerAsync (owner: string) =
            TraceOperations.protect owner (fun () -> delete (fun trace -> trace.AgentId = owner))

        let deleteExpiredAsync (owner: string) before =
            TraceOperations.protect owner (fun () ->
                delete (fun trace -> trace.AgentId = owner && trace.StartedAt < before))

        { SaveAsync = saveAsync
          GetBaselineAsync = getBaselineAsync
          GetTracesAsync = getTracesAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }

module PersistentTracer =
    let create (store: EventStore) =
        store.LoadAll()
        |> Seq.map FSharpJson.deserialize<Span>
        |> TracerState.create (FSharpJson.serialize >> store.Append)

module Tracers =
    let ado factory =
        PersistentTracer.create (EventStore.db factory "tracer")

    let file baseDir =
        PersistentTracer.create (EventStore.file (System.IO.Path.Combine(baseDir, "tracer.jsonl")))

[<RequireQualifiedAccess>]
type MetricsEvent =
    | Accepted of MetricRecord
    | DeleteOwner of string
    | DeleteExpired of string * DateTimeOffset

type MetricsDocument = { Version: int; Event: MetricsEvent }

module PersistentMetricsCollector =
    let create (store: EventStore) : MetricsCollector =
        let inner = InMemoryMetricsCollector.create ()

        for line in store.LoadAll() do
            let document = FSharpJson.deserialize<MetricsDocument> line

            if document.Version <> 1 then
                invalidOp (sprintf "Unsupported metrics document version %d." document.Version)

            match document.Event with
            | MetricsEvent.Accepted metric -> inner.Record metric
            | MetricsEvent.DeleteOwner owner -> inner.DeleteOwnerAsync(owner).GetAwaiter().GetResult() |> ignore
            | MetricsEvent.DeleteExpired(owner, before) ->
                inner.DeleteExpiredAsync owner before
                |> fun operation -> operation.GetAwaiter().GetResult() |> ignore

        let append event =
            store.Append(FSharpJson.serialize { Version = 1; Event = event })

        let persist event operation =
            task {
                let! result = operation

                match result with
                | Error failure -> return Error failure
                | Ok count ->
                    try
                        append event
                        return Ok count
                    with ex ->
                        return Error(TraceOperations.failure ex)
            }

        { Record =
            fun metric ->
                inner.Record metric
                append (MetricsEvent.Accepted metric)
          GetMetrics = inner.GetMetrics
          EstimateCost = inner.EstimateCost
          DeleteOwnerAsync = fun owner -> persist (MetricsEvent.DeleteOwner owner) (inner.DeleteOwnerAsync owner)
          DeleteExpiredAsync =
            fun owner before ->
                persist (MetricsEvent.DeleteExpired(owner, before)) (inner.DeleteExpiredAsync owner before) }

module MetricsCollectors =
    let ado factory =
        PersistentMetricsCollector.create (EventStore.db factory "metrics")

    let file baseDir =
        PersistentMetricsCollector.create (EventStore.file (System.IO.Path.Combine(baseDir, "metrics.jsonl")))

[<RequireQualifiedAccess>]
type TraceStoreEvent =
    | Save of ExecutionTrace
    | DeleteOwner of string
    | DeleteExpired of string * DateTimeOffset

module PersistentTraceStore =
    let create (store: EventStore) : TraceStore =
        let inner = InMemoryTraceStore.create ()

        for line in store.LoadAll() do
            match FSharpJson.deserialize<TraceStoreEvent> line with
            | TraceStoreEvent.Save trace -> inner.SaveAsync(trace).GetAwaiter().GetResult()
            | TraceStoreEvent.DeleteOwner owner -> inner.DeleteOwnerAsync(owner).GetAwaiter().GetResult() |> ignore
            | TraceStoreEvent.DeleteExpired(owner, before) ->
                inner.DeleteExpiredAsync owner before
                |> fun operation -> operation.GetAwaiter().GetResult() |> ignore

        let persist event operation =
            task {
                let! result = operation

                match result with
                | Error failure -> return Error failure
                | Ok count ->
                    try
                        store.Append(FSharpJson.serialize event)
                        return Ok count
                    with ex ->
                        return Error(TraceOperations.failure ex)
            }

        let saveAsync trace =
            task {
                do! inner.SaveAsync trace
                store.Append(FSharpJson.serialize (TraceStoreEvent.Save trace))
            }

        let deleteOwnerAsync owner =
            persist (TraceStoreEvent.DeleteOwner owner) (inner.DeleteOwnerAsync owner)

        let deleteExpiredAsync owner before =
            persist (TraceStoreEvent.DeleteExpired(owner, before)) (inner.DeleteExpiredAsync owner before)

        { SaveAsync = saveAsync
          GetBaselineAsync = inner.GetBaselineAsync
          GetTracesAsync = inner.GetTracesAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }

module TraceStores =
    let ado factory =
        PersistentTraceStore.create (EventStore.db factory "trace-store")

    let file baseDir =
        PersistentTraceStore.create (EventStore.file (System.IO.Path.Combine(baseDir, "trace-store.jsonl")))
