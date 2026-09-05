namespace Nao.Persistence

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Nao.Agents

module private TracerState =
    let create (persist: Span -> unit) (initial: Span seq) : Tracer =
        let spans = System.Collections.Concurrent.ConcurrentDictionary<SpanId, Span>()

        for span in initial do
            spans.[span.Id] <- span

        let upsert (span: Span) =
            persist span
            spans.[span.Id] <- span

        { StartTrace =
            fun correlation operationName ->
                let span =
                    { Id = SpanId(Guid.NewGuid())
                      Correlation = correlation
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
                      Correlation = parent.Correlation
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
          GetTrace = fun traceId -> spans.Values |> Seq.filter (fun span -> span.TraceId = traceId) |> Seq.toList
          GetByExecution =
            fun executionId ->
                spans.Values
                |> Seq.filter (fun span -> span.Correlation.ExecutionId = executionId)
                |> Seq.sortBy _.StartTime
                |> Seq.toList }

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
          GetByExecution =
            fun executionId ->
                records.Values
                |> Seq.filter (fun metric -> metric.Correlation.ExecutionId = executionId)
                |> Seq.sortBy (fun metric -> metric.Timestamp)
                |> Seq.toList
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

        let getByExecutionAsync executionId =
            traces.Values
            |> Seq.filter (fun trace -> trace.Correlation.ExecutionId = executionId)
            |> Seq.sortByDescending (fun trace -> trace.StartedAt)
            |> Seq.toList
            |> Task.FromResult

        let deleteOwnerAsync (owner: string) =
            TraceOperations.protect owner (fun () -> delete (fun trace -> trace.AgentId = owner))

        let deleteExpiredAsync (owner: string) before =
            TraceOperations.protect owner (fun () ->
                delete (fun trace -> trace.AgentId = owner && trace.StartedAt < before)) in

        { SaveAsync = saveAsync
          GetBaselineAsync = getBaselineAsync
          GetTracesAsync = getTracesAsync
          GetByExecutionAsync = getByExecutionAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }

type TracerDocument =
    { [<JsonPropertyName("schemaVersion")>]
      SchemaVersion: int
      [<JsonPropertyName("value")>]
      Value: Span }

module PersistentTracer =
    [<Literal>]
    let private CurrentSchemaVersion = 1

    let private decode context lineNumber line =
        try
            let document = FSharpJson.deserialize<TracerDocument> line

            if isNull (box document) || document.SchemaVersion <> CurrentSchemaVersion then
                raise (JsonException(sprintf "Expected schema version %d." CurrentSchemaVersion))

            document.Value
        with ex ->
            raise (
                InvalidDataException(
                    sprintf
                        "Tracer stream '%s' is invalid at span %d. Follow docs/migrations before writing."
                        context
                        lineNumber,
                    ex
                )
            )

    let create context (store: EventStore) =
        let loadSpans () =
            store.LoadAll() |> List.mapi (fun index line -> decode context (index + 1) line)

        let persist span =
            loadSpans () |> ignore

            store.Append(
                FSharpJson.serialize
                    { SchemaVersion = CurrentSchemaVersion
                      Value = span }
            )

        loadSpans () |> TracerState.create persist

module Tracers =
    let ado factory =
        PersistentTracer.create "tracer" (EventStore.db factory "tracer")

    let file baseDir =
        let path = System.IO.Path.Combine(baseDir, "tracer.jsonl")
        PersistentTracer.create path (EventStore.file path)

[<RequireQualifiedAccess>]
type MetricsEvent =
    | Accepted of MetricRecord
    | DeleteOwner of string
    | DeleteExpired of string * DateTimeOffset

type MetricsDocument = { Version: int; Event: MetricsEvent }

module PersistentMetricsCollector =
    let private decode context lineNumber line =
        try
            let document = FSharpJson.deserialize<MetricsDocument> line

            if isNull (box document) || document.Version <> 1 then
                raise (JsonException("Expected metrics schema version 1."))

            document.Event
        with ex ->
            raise (
                InvalidDataException(
                    sprintf
                        "Metrics stream '%s' is invalid at event %d. Follow docs/migrations before writing."
                        context
                        lineNumber,
                    ex
                )
            )

    let create context (store: EventStore) : MetricsCollector =
        let inner = InMemoryMetricsCollector.create ()

        let loadEvents () =
            store.LoadAll() |> List.mapi (fun index line -> decode context (index + 1) line)

        for event in loadEvents () do
            match event with
            | MetricsEvent.Accepted metric -> inner.Record metric
            | MetricsEvent.DeleteOwner owner -> inner.DeleteOwnerAsync(owner).GetAwaiter().GetResult() |> ignore
            | MetricsEvent.DeleteExpired(owner, before) ->
                inner.DeleteExpiredAsync owner before
                |> fun operation -> operation.GetAwaiter().GetResult() |> ignore

        let append event =
            store.Append(FSharpJson.serialize { Version = 1; Event = event })

        let persist event operation =
            task {
                loadEvents () |> ignore
                let! result = operation ()

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
                loadEvents () |> ignore
                inner.Record metric
                append (MetricsEvent.Accepted metric)
          GetMetrics = inner.GetMetrics
          GetByExecution = inner.GetByExecution
          EstimateCost = inner.EstimateCost
          DeleteOwnerAsync =
            fun owner -> persist (MetricsEvent.DeleteOwner owner) (fun () -> inner.DeleteOwnerAsync owner)
          DeleteExpiredAsync =
            fun owner before ->
                persist (MetricsEvent.DeleteExpired(owner, before)) (fun () -> inner.DeleteExpiredAsync owner before) }

module MetricsCollectors =
    let ado factory =
        PersistentMetricsCollector.create "metrics" (EventStore.db factory "metrics")

    let file baseDir =
        let path = System.IO.Path.Combine(baseDir, "metrics.jsonl")
        PersistentMetricsCollector.create path (EventStore.file path)

[<RequireQualifiedAccess>]
type TraceStoreEvent =
    | Save of ExecutionTrace
    | DeleteOwner of string
    | DeleteExpired of string * DateTimeOffset

type TraceStoreDocument =
    { [<JsonPropertyName("schemaVersion")>]
      SchemaVersion: int
      [<JsonPropertyName("event")>]
      Event: TraceStoreEvent }

module PersistentTraceStore =
    [<Literal>]
    let private CurrentSchemaVersion = 1

    let private decode context lineNumber line =
        try
            let document = FSharpJson.deserialize<TraceStoreDocument> line

            if isNull (box document) || document.SchemaVersion <> CurrentSchemaVersion then
                raise (JsonException(sprintf "Expected schema version %d." CurrentSchemaVersion))

            document.Event
        with ex ->
            raise (
                InvalidDataException(
                    sprintf
                        "Trace store '%s' is invalid at event %d. Follow docs/migrations before writing."
                        context
                        lineNumber,
                    ex
                )
            )

    let create context (store: EventStore) : TraceStore =
        let inner = InMemoryTraceStore.create ()

        let loadEvents () =
            store.LoadAll() |> List.mapi (fun index line -> decode context (index + 1) line)

        let append event =
            store.Append(
                FSharpJson.serialize
                    { SchemaVersion = CurrentSchemaVersion
                      Event = event }
            )

        for event in loadEvents () do
            match event with
            | TraceStoreEvent.Save trace -> inner.SaveAsync(trace).GetAwaiter().GetResult()
            | TraceStoreEvent.DeleteOwner owner -> inner.DeleteOwnerAsync(owner).GetAwaiter().GetResult() |> ignore
            | TraceStoreEvent.DeleteExpired(owner, before) ->
                inner.DeleteExpiredAsync owner before
                |> fun operation -> operation.GetAwaiter().GetResult() |> ignore

        let persist event operation =
            task {
                loadEvents () |> ignore
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

        let saveAsync trace =
            task {
                loadEvents () |> ignore
                do! inner.SaveAsync trace
                append (TraceStoreEvent.Save trace)
            }

        let deleteOwnerAsync owner =
            persist (TraceStoreEvent.DeleteOwner owner) (inner.DeleteOwnerAsync owner)

        let deleteExpiredAsync owner before =
            persist (TraceStoreEvent.DeleteExpired(owner, before)) (inner.DeleteExpiredAsync owner before) in

        { SaveAsync = saveAsync
          GetBaselineAsync = inner.GetBaselineAsync
          GetTracesAsync = inner.GetTracesAsync
          GetByExecutionAsync = inner.GetByExecutionAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }

module TraceStores =
    let ado factory =
        PersistentTraceStore.create "trace-store" (EventStore.db factory "trace-store")

    let file baseDir =
        let path = System.IO.Path.Combine(baseDir, "trace-store.jsonl")
        PersistentTraceStore.create path (EventStore.file path)
