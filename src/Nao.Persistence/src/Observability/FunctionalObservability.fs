namespace Nao.Persistence

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Nao.Agents

module private ObservabilityRedaction =
    let private redact = SecretRedaction.redact

    let spanStatus =
        function
        | SpanStatus.Error message -> SpanStatus.Error(redact message)
        | status -> status

    let span (span: Span) =
        { span with
            OperationName = redact span.OperationName
            Status = spanStatus span.Status
            Attributes = SecretRedaction.redactMap span.Attributes
            Events =
                span.Events
                |> List.map (fun (event: SpanEvent) ->
                    { event with
                        Name = redact event.Name
                        Attributes = SecretRedaction.redactMap event.Attributes }) }

    let metric (record: MetricRecord) =
        let payload =
            match record.Payload with
            | MetricPayload.ToolCall(toolName, durationMs, success) ->
                MetricPayload.ToolCall(redact toolName, durationMs, success)
            | MetricPayload.Custom point ->
                MetricPayload.Custom
                    { point with
                        Name = redact point.Name
                        Labels = SecretRedaction.redactMap point.Labels }
            | payload -> payload

        { record with Payload = payload }

    let trace (trace: ExecutionTrace) =
        let action =
            function
            | TraceAction.LlmCall model -> TraceAction.LlmCall(redact model)
            | TraceAction.ToolInvocation toolName -> TraceAction.ToolInvocation(redact toolName)
            | TraceAction.AgentDelegation agentName -> TraceAction.AgentDelegation(redact agentName)
            | TraceAction.MemoryAccess operation -> TraceAction.MemoryAccess(redact operation)
            | value -> value

        { trace with
            AgentId = redact trace.AgentId
            Input = redact trace.Input
            Output = SecretRedaction.redactOption trace.Output
            Steps =
                trace.Steps
                |> List.map (fun (step: TraceStep) ->
                    { step with
                        Action = action step.Action
                        Input = redact step.Input
                        Output = redact step.Output })
            Metadata = SecretRedaction.redactMap trace.Metadata }

module private TracerState =
    let create (persist: AuthorizationScope -> Span -> unit) (initial: (AuthorizationScope * Span) seq) : Tracer =
        let spans =
            System.Collections.Concurrent.ConcurrentDictionary<string * SpanId, Span>()

        let ownerKey = AuthorizationScope.identity >> AuthorizationIdentity.storageKey

        for scope, span in initial do
            spans.[(ownerKey scope, span.Id)] <- span

        let owned scope =
            let owner = ownerKey scope

            spans
            |> Seq.choose (fun pair -> if fst pair.Key = owner then Some pair.Value else None)

        let current scope (span: Span) =
            match spans.TryGetValue((ownerKey scope, span.Id)) with
            | true, stored -> stored
            | false, _ ->
                raise (UnauthorizedAccessException "The span does not belong to the supplied authorization scope.")

        let upsert scope (span: Span) =
            let span = ObservabilityRedaction.span span
            persist scope span
            spans.[(ownerKey scope, span.Id)] <- span

        { StartTrace =
            fun scope correlation operationName ->
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

                upsert scope span
                span
          StartSpan =
            fun scope parent operationName ->
                let parent = current scope parent

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

                upsert scope span
                span
          EndSpan =
            fun scope span status ->
                let span = current scope span

                upsert
                    scope
                    { span with
                        EndTime = Some DateTimeOffset.UtcNow
                        Status = status }
          AddEvent =
            fun scope span name attributes ->
                let span = current scope span

                upsert
                    scope
                    { span with
                        Events =
                            span.Events
                            @ [ { Name = name
                                  Timestamp = DateTimeOffset.UtcNow
                                  Attributes = attributes } ] }
          SetAttributes =
            fun scope span attributes ->
                let span = current scope span

                upsert
                    scope
                    { span with
                        Attributes =
                            Map.fold (fun state key value -> Map.add key value state) span.Attributes attributes }
          GetTrace = fun scope traceId -> owned scope |> Seq.filter (fun span -> span.TraceId = traceId) |> Seq.toList
          GetByExecution =
            fun scope executionId ->
                owned scope
                |> Seq.filter (fun span -> span.Correlation.ExecutionId = executionId)
                |> Seq.sortBy _.StartTime
                |> Seq.toList }

module InMemoryTracer =
    let create () =
        TracerState.create (fun _ _ -> ()) Seq.empty

module InMemoryMetricsCollector =
    let create () : MetricsCollector =
        let records =
            System.Collections.Concurrent.ConcurrentDictionary<string * Guid, MetricRecord>()

        let ownerKey = AuthorizationScope.identity >> AuthorizationIdentity.storageKey

        let record scope (metric: MetricRecord) =
            let metric = ObservabilityRedaction.metric metric
            records.[(ownerKey scope, metric.Id)] <- metric

        let owned scope =
            let owner = ownerKey scope

            records
            |> Seq.choose (fun pair -> if fst pair.Key = owner then Some pair.Value else None)
            |> Seq.toArray

        let metrics scope =
            let retained = owned scope

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

        let delete scope (predicate: MetricRecord -> bool) =
            task {
                let owner = ownerKey scope
                let mutable deleted = 0

                for pair in records do
                    if fst pair.Key = owner && predicate pair.Value then
                        match records.TryRemove pair.Key with
                        | true, _ -> deleted <- deleted + 1
                        | false, _ -> ()

                return Ok deleted
            }

        { Record = record
          GetMetrics = metrics
          GetByExecution =
            fun scope executionId ->
                owned scope
                |> Seq.filter (fun metric -> metric.Correlation.ExecutionId = executionId)
                |> Seq.sortBy (fun metric -> metric.Timestamp)
                |> Seq.toList
          EstimateCost =
            fun scope model ->
                let aggregate = metrics scope

                decimal aggregate.TotalInputTokens / 1000m * model.InputCostPer1K
                + decimal aggregate.TotalOutputTokens / 1000m * model.OutputCostPer1K
          DeleteScopeAsync = fun scope -> delete scope (fun _ -> true)
          DeleteExpiredAsync = fun scope before -> delete scope (fun metric -> metric.Timestamp < before) }

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
            System.Collections.Concurrent.ConcurrentDictionary<string * Guid, ExecutionTrace>()

        let ownerKey = AuthorizationScope.identity >> AuthorizationIdentity.storageKey

        let owned scope =
            let owner = ownerKey scope

            traces
            |> Seq.choose (fun pair -> if fst pair.Key = owner then Some pair.Value else None)

        let delete scope (predicate: ExecutionTrace -> bool) =
            let owner = ownerKey scope
            let mutable deleted = 0

            for pair in traces do
                if fst pair.Key = owner && predicate pair.Value then
                    match traces.TryRemove pair.Key with
                    | true, _ -> deleted <- deleted + 1
                    | false, _ -> ()

            Task.FromResult deleted

        let saveAsync scope (trace: ExecutionTrace) =
            let trace = ObservabilityRedaction.trace trace
            traces.[(ownerKey scope, trace.Id)] <- trace
            Task.FromResult()

        let getBaselineAsync scope (agentId: string) (_: string) =
            owned scope
            |> Seq.filter (fun trace -> trace.AgentId = agentId && trace.Success)
            |> Seq.sortByDescending (fun trace -> trace.StartedAt)
            |> Seq.tryHead
            |> Task.FromResult

        let getTracesAsync scope (agentId: string) limit =
            owned scope
            |> Seq.filter (fun trace -> trace.AgentId = agentId)
            |> Seq.sortByDescending (fun trace -> trace.StartedAt)
            |> Seq.truncate limit
            |> Seq.toList
            |> Task.FromResult

        let getByExecutionAsync scope executionId =
            owned scope
            |> Seq.filter (fun trace -> trace.Correlation.ExecutionId = executionId)
            |> Seq.sortByDescending (fun trace -> trace.StartedAt)
            |> Seq.toList
            |> Task.FromResult

        let deleteScopeAsync scope =
            task {
                let! count = delete scope (fun _ -> true)
                return Ok count
            }

        let deleteExpiredAsync scope before =
            task {
                let! count = delete scope (fun trace -> trace.StartedAt < before)
                return Ok count
            }

        { SaveAsync = saveAsync
          GetBaselineAsync = getBaselineAsync
          GetTracesAsync = getTracesAsync
          GetByExecutionAsync = getByExecutionAsync
          DeleteScopeAsync = deleteScopeAsync
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

    /// The host supplies a separate stream for each claim-free authorization partition key.
    let create (openStore: string -> EventStore) =
        let sync = obj ()

        let load scope =
            let key = AuthorizationScope.identity scope |> AuthorizationIdentity.storageKey
            let context = "tracer/" + key
            let store = openStore key

            let spans =
                store.LoadAll() |> List.mapi (fun index line -> decode context (index + 1) line)

            let persist _ span =
                store.Append(
                    FSharpJson.serialize
                        { SchemaVersion = CurrentSchemaVersion
                          Value = span }
                )

            TracerState.create persist (spans |> Seq.map (fun span -> scope, span))

        { StartTrace =
            fun scope correlation operationName ->
                lock sync (fun () -> (load scope).StartTrace scope correlation operationName)
          StartSpan =
            fun scope parent operationName -> lock sync (fun () -> (load scope).StartSpan scope parent operationName)
          EndSpan = fun scope span status -> lock sync (fun () -> (load scope).EndSpan scope span status)
          AddEvent =
            fun scope span name attributes -> lock sync (fun () -> (load scope).AddEvent scope span name attributes)
          SetAttributes =
            fun scope span attributes -> lock sync (fun () -> (load scope).SetAttributes scope span attributes)
          GetTrace = fun scope traceId -> lock sync (fun () -> (load scope).GetTrace scope traceId)
          GetByExecution = fun scope executionId -> lock sync (fun () -> (load scope).GetByExecution scope executionId) }

module Tracers =
    let ado factory =
        PersistentTracer.create (fun key -> EventStore.db factory ("tracer/" + key))

    let file baseDir =
        PersistentTracer.create (fun key -> EventStore.file (System.IO.Path.Combine(baseDir, "tracer", key + ".jsonl")))

[<RequireQualifiedAccess>]
type MetricsEvent =
    | Accepted of MetricRecord
    | DeleteScope
    | DeleteExpired of DateTimeOffset

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

    /// The host supplies a separate stream for each claim-free authorization partition key.
    let create (openStore: string -> EventStore) : MetricsCollector =
        let sync = obj ()

        let load scope =
            let key = AuthorizationScope.identity scope |> AuthorizationIdentity.storageKey
            let context = "metrics/" + key
            let store = openStore key
            let inner = InMemoryMetricsCollector.create ()

            let events =
                store.LoadAll() |> List.mapi (fun index line -> decode context (index + 1) line)

            for event in events do
                match event with
                | MetricsEvent.Accepted metric -> inner.Record scope metric
                | MetricsEvent.DeleteScope -> inner.DeleteScopeAsync scope |> _.GetAwaiter().GetResult() |> ignore
                | MetricsEvent.DeleteExpired before ->
                    inner.DeleteExpiredAsync scope before |> _.GetAwaiter().GetResult() |> ignore

            store, inner

        let read scope operation =
            lock sync (fun () ->
                let _, inner = load scope
                operation inner)

        let mutate scope operation event =
            lock sync (fun () ->
                let store, inner = load scope
                let result = operation inner
                store.Append(FSharpJson.serialize { Version = 1; Event = event })
                result)

        let protect operation =
            task {
                try
                    return operation () |> Ok
                with
                | :? IOException as error -> return Error(TraceOperations.failure error)
                | :? InvalidDataException as error -> return Error(TraceOperations.failure error)
                | :? System.Data.Common.DbException as error -> return Error(TraceOperations.failure error)
                | :? UnauthorizedAccessException as error -> return Error(TraceOperations.failure error)
            }

        let valueOrRaise =
            function
            | Ok value -> value
            | Error failure -> PlatformFailure.raiseException failure

        { Record =
            fun scope metric ->
                let metric = ObservabilityRedaction.metric metric
                mutate scope (fun inner -> inner.Record scope metric) (MetricsEvent.Accepted metric)
          GetMetrics = fun scope -> read scope (fun inner -> inner.GetMetrics scope)
          GetByExecution = fun scope executionId -> read scope (fun inner -> inner.GetByExecution scope executionId)
          EstimateCost = fun scope model -> read scope (fun inner -> inner.EstimateCost scope model)
          DeleteScopeAsync =
            fun scope ->
                protect (fun () ->
                    mutate
                        scope
                        (fun inner -> inner.DeleteScopeAsync scope |> _.GetAwaiter().GetResult() |> valueOrRaise)
                        MetricsEvent.DeleteScope)
          DeleteExpiredAsync =
            fun scope before ->
                protect (fun () ->
                    mutate
                        scope
                        (fun inner ->
                            inner.DeleteExpiredAsync scope before
                            |> _.GetAwaiter().GetResult()
                            |> valueOrRaise)
                        (MetricsEvent.DeleteExpired before)) }

module MetricsCollectors =
    let ado factory =
        PersistentMetricsCollector.create (fun key -> EventStore.db factory ("metrics/" + key))

    let file baseDir =
        PersistentMetricsCollector.create (fun key ->
            EventStore.file (System.IO.Path.Combine(baseDir, "metrics", key + ".jsonl")))

[<RequireQualifiedAccess>]
type TraceStoreEvent =
    | Save of ExecutionTrace
    | DeleteScope
    | DeleteExpired of DateTimeOffset

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

    /// The host supplies a separate stream for each claim-free authorization partition key.
    let create (openStore: string -> EventStore) : TraceStore =
        let sync = obj ()

        let load scope =
            let key = AuthorizationScope.identity scope |> AuthorizationIdentity.storageKey
            let context = "trace-store/" + key
            let store = openStore key
            let inner = InMemoryTraceStore.create ()

            let events =
                store.LoadAll() |> List.mapi (fun index line -> decode context (index + 1) line)

            for event in events do
                match event with
                | TraceStoreEvent.Save trace -> inner.SaveAsync scope trace |> _.GetAwaiter().GetResult()
                | TraceStoreEvent.DeleteScope -> inner.DeleteScopeAsync scope |> _.GetAwaiter().GetResult() |> ignore
                | TraceStoreEvent.DeleteExpired before ->
                    inner.DeleteExpiredAsync scope before |> _.GetAwaiter().GetResult() |> ignore

            store, inner

        let read scope operation =
            lock sync (fun () ->
                let _, inner = load scope
                operation inner)

        let mutate scope operation event =
            lock sync (fun () ->
                let store, inner = load scope
                let result = operation inner

                store.Append(
                    FSharpJson.serialize
                        { SchemaVersion = CurrentSchemaVersion
                          Event = event }
                )

                result)

        let protect operation =
            task {
                try
                    return operation () |> Ok
                with
                | :? IOException as error -> return Error(TraceOperations.failure error)
                | :? InvalidDataException as error -> return Error(TraceOperations.failure error)
                | :? System.Data.Common.DbException as error -> return Error(TraceOperations.failure error)
                | :? UnauthorizedAccessException as error -> return Error(TraceOperations.failure error)
            }

        let valueOrRaise =
            function
            | Ok value -> value
            | Error failure -> PlatformFailure.raiseException failure

        { SaveAsync =
            fun scope trace ->
                let trace = ObservabilityRedaction.trace trace

                task {
                    mutate
                        scope
                        (fun inner -> inner.SaveAsync scope trace |> _.GetAwaiter().GetResult())
                        (TraceStoreEvent.Save trace)
                }
          GetBaselineAsync =
            fun scope agentId pattern ->
                task { return read scope (fun inner -> inner.GetBaselineAsync scope agentId pattern |> _.Result) }
          GetTracesAsync =
            fun scope agentId limit ->
                task { return read scope (fun inner -> inner.GetTracesAsync scope agentId limit |> _.Result) }
          GetByExecutionAsync =
            fun scope executionId ->
                task { return read scope (fun inner -> inner.GetByExecutionAsync scope executionId |> _.Result) }
          DeleteScopeAsync =
            fun scope ->
                protect (fun () ->
                    mutate
                        scope
                        (fun inner -> inner.DeleteScopeAsync scope |> _.GetAwaiter().GetResult() |> valueOrRaise)
                        TraceStoreEvent.DeleteScope)
          DeleteExpiredAsync =
            fun scope before ->
                protect (fun () ->
                    mutate
                        scope
                        (fun inner ->
                            inner.DeleteExpiredAsync scope before
                            |> _.GetAwaiter().GetResult()
                            |> valueOrRaise)
                        (TraceStoreEvent.DeleteExpired before)) }

module TraceStores =
    let ado factory =
        PersistentTraceStore.create (fun key -> EventStore.db factory ("trace-store/" + key))

    let file baseDir =
        PersistentTraceStore.create (fun key ->
            EventStore.file (System.IO.Path.Combine(baseDir, "trace-store", key + ".jsonl")))
