namespace Nao.Telemetry.OpenTelemetry

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Diagnostics.Metrics
open System.Security.Cryptography
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Nao.Agents
open OpenTelemetry
open OpenTelemetry.Exporter
open OpenTelemetry.Logs
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open OpenTelemetry.Trace

type TelemetryExportStatistics =
    { Accepted: int64
      Dropped: int64
      Processed: int64
      Failed: int64 }

type TelemetryPrivacyStatistics =
    { Redacted: int64
      SampledOut: int64
      Expired: int64
      MissingPolicy: int64 }

[<RequireQualifiedAccess>]
type TelemetryShutdownResult =
    | Flushed
    | TimedOut

type TelemetrySignalSink =
    { WriteAsync: EventScope -> ObservabilitySignal -> CancellationToken -> Task
      ForceFlush: TimeSpan -> bool
      Shutdown: TimeSpan -> bool
      ScrapePrometheus: unit -> string }

type TelemetryExportRuntime
    internal
    (
        configuration: TelemetryExportConfiguration,
        sink: TelemetrySignalSink,
        channel: Channel<EventScope * ObservabilitySignal>,
        pump: Task,
        stop: CancellationTokenSource,
        counters: int64 array
    ) =

    let mutable shutdown = 0

    member _.Consumer =
        EventConsumer.create (fun event ->
            match event with
            | ObservabilityCaptured(scope, signal) when Volatile.Read(&shutdown) = 0 ->
                if channel.Writer.TryWrite((scope, signal)) then
                    Interlocked.Increment(&counters.[0]) |> ignore
                else
                    Interlocked.Increment(&counters.[1]) |> ignore

                Task.CompletedTask
            | _ -> Task.CompletedTask)

    member _.Statistics =
        { Accepted = Interlocked.Read(&counters.[0])
          Dropped = Interlocked.Read(&counters.[1])
          Processed = Interlocked.Read(&counters.[2])
          Failed = Interlocked.Read(&counters.[3]) }

    member _.ScrapePrometheus() = sink.ScrapePrometheus()

    member _.ShutdownAsync(?cancellationToken: CancellationToken) =
        task {
            if Interlocked.Exchange(&shutdown, 1) <> 0 then
                return TelemetryShutdownResult.Flushed
            else
                channel.Writer.TryComplete() |> ignore

                let cancellationToken = defaultArg cancellationToken CancellationToken.None
                let deadline = TelemetryExportConfiguration.shutdownTimeout configuration
                use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                timeout.CancelAfter deadline
                let started = Stopwatch.StartNew()

                try
                    do! pump.WaitAsync(timeout.Token)
                    let flushRemaining = deadline - started.Elapsed
                    let flushed = flushRemaining > TimeSpan.Zero && sink.ForceFlush flushRemaining
                    let shutdownRemaining = deadline - started.Elapsed

                    if flushed && shutdownRemaining > TimeSpan.Zero && sink.Shutdown shutdownRemaining then
                        return TelemetryShutdownResult.Flushed
                    else
                        stop.Cancel()
                        return TelemetryShutdownResult.TimedOut
                with :? OperationCanceledException ->
                    stop.Cancel()
                    return TelemetryShutdownResult.TimedOut
        }

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            ValueTask(
                task {
                    let! _ = this.ShutdownAsync()
                    stop.Dispose()
                }
            )

[<Sealed>]
type OpenTelemetryExporterRuntime
    internal
    (runtime: TelemetryExportRuntime, privacyStatistics: unit -> TelemetryPrivacyStatistics, resources: IDisposable) =
    member _.Consumer = runtime.Consumer
    member _.Statistics = runtime.Statistics
    member _.PrivacyStatistics = privacyStatistics ()
    member _.ScrapePrometheus() = runtime.ScrapePrometheus()

    member _.ShutdownAsync(?cancellationToken: CancellationToken) =
        runtime.ShutdownAsync(?cancellationToken = cancellationToken)

    interface IDisposable with
        member _.Dispose() = resources.Dispose()

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(
                task {
                    let! _ = runtime.ShutdownAsync()
                    resources.Dispose()
                }
            )

[<RequireQualifiedAccess>]
module TelemetryExportRuntime =
    let create configuration sink =
        let options =
            BoundedChannelOptions(TelemetryExportConfiguration.queueCapacity configuration)

        options.SingleReader <- true
        options.SingleWriter <- false
        options.FullMode <- BoundedChannelFullMode.Wait
        options.AllowSynchronousContinuations <- false
        let channel = Channel.CreateBounded<EventScope * ObservabilitySignal>(options)
        let stop = new CancellationTokenSource()
        let counters = Array.zeroCreate<int64> 4

        let pump =
            task {
                let mutable reading = true

                while reading do
                    let! available = channel.Reader.WaitToReadAsync(stop.Token).AsTask()
                    reading <- available
                    let mutable item = Unchecked.defaultof<EventScope * ObservabilitySignal>

                    while available && channel.Reader.TryRead(&item) do
                        let scope, signal = item
                        let mutable attempt = 1
                        let mutable delivered = false

                        while not delivered
                              && attempt <= TelemetryExportConfiguration.maximumSignalAttempts configuration do
                            try
                                do! sink.WriteAsync scope signal stop.Token
                                delivered <- true
                                Interlocked.Increment(&counters.[2]) |> ignore
                            with
                            | :? OperationCanceledException when stop.IsCancellationRequested ->
                                attempt <- TelemetryExportConfiguration.maximumSignalAttempts configuration + 1
                            | _ when attempt < TelemetryExportConfiguration.maximumSignalAttempts configuration ->
                                let multiplier = pown 2 (attempt - 1)

                                let delay =
                                    TelemetryExportConfiguration.initialRetryDelay configuration * float multiplier

                                do! Task.Delay(delay, stop.Token)
                                attempt <- attempt + 1
                            | _ ->
                                Interlocked.Increment(&counters.[3]) |> ignore
                                attempt <- TelemetryExportConfiguration.maximumSignalAttempts configuration + 1
            }
            :> Task

        TelemetryExportRuntime(configuration, sink, channel, pump, stop, counters)

module private OpenTelemetryConversion =
    let protocol =
        function
        | OtlpTransport.Grpc -> OtlpExportProtocol.Grpc
        | OtlpTransport.HttpProtobuf -> OtlpExportProtocol.HttpProtobuf

    let activityTraceId traceId =
        TraceId.serialize traceId
        |> fun value -> value.Replace("-", "")
        |> fun value -> ActivityTraceId.CreateFromString(value.AsSpan())

    let activitySpanId spanId =
        let bytes = SpanId.value spanId |> _.ToByteArray()
        let hash = SHA256.HashData bytes
        ActivitySpanId.CreateFromBytes(hash.AsSpan(0, 8))

    let tags attributes =
        attributes
        |> Map.toSeq
        |> Seq.map (fun (key, value) -> KeyValuePair<string, obj>(key, value :> obj))

    let labelTags labels =
        labels
        |> Map.toArray
        |> Array.map (fun (key, value) -> KeyValuePair<string, obj>(key, value :> obj))

    let status =
        function
        | SpanStatus.Ok -> ActivityStatusCode.Ok, null
        | SpanStatus.Cancelled -> ActivityStatusCode.Error, "cancelled"
        | SpanStatus.Error category -> ActivityStatusCode.Error, TelemetryErrorCategory.value category

module private Instrumentation =
    [<Literal>]
    let ActivitySourceName = "Nao"

    [<Literal>]
    let MeterName = "Nao"

type private OpenTelemetrySignalSink(configuration: TelemetryExportConfiguration, destination: TelemetryDestination) =
    let source = new ActivitySource(Instrumentation.ActivitySourceName)
    let meter = new Meter(Instrumentation.MeterName)
    let registry = PrometheusRegistry()
    let activities = ConcurrentDictionary<SpanId, Activity>()
    let executionActivities = ConcurrentDictionary<ExecutionId, Activity>()
    let providerActivities = ConcurrentDictionary<ExecutionId, Activity>()
    let mutable logProcessor: BatchLogRecordExportProcessor option = None

    let resource =
        TelemetryExportConfiguration.resourceAttributes configuration
        |> Seq.map (fun pair -> KeyValuePair<string, obj>(pair.Key, pair.Value :> obj))
        |> fun attributes ->
            ResourceBuilder
                .CreateDefault()
                .AddService(
                    TelemetryExportConfiguration.serviceName configuration,
                    serviceVersion = TelemetryExportConfiguration.serviceVersion configuration
                )
                .AddAttributes(
                    Seq.append
                        attributes
                        [ KeyValuePair<string, obj>("cloud.region", TelemetryDestination.region destination :> obj) ]
                )

    let configure (options: OtlpExporterOptions) endpoint (otlp: OtlpConfiguration) =
        options.Endpoint <- endpoint
        options.Protocol <- OpenTelemetryConversion.protocol otlp.Transport
        options.TimeoutMilliseconds <- int (TelemetryExportConfiguration.exportTimeout configuration).TotalMilliseconds
        otlp.Headers |> Option.iter (fun headers -> options.Headers <- headers)

    let traceProvider =
        let builder =
            Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource).AddSource(Instrumentation.ActivitySourceName)

        TelemetryDestination.otlp destination
        |> Option.iter (fun otlp ->
            let options = OtlpExporterOptions()
            configure options otlp.Endpoints.Traces otlp
            let exporter = new OtlpTraceExporter(options)

            let processor =
                new BatchActivityExportProcessor(
                    exporter,
                    TelemetryExportConfiguration.exporterQueueCapacity configuration,
                    int (TelemetryExportConfiguration.exportScheduleDelay configuration).TotalMilliseconds,
                    int (TelemetryExportConfiguration.exportTimeout configuration).TotalMilliseconds,
                    TelemetryExportConfiguration.exportBatchSize configuration
                )

            builder.AddProcessor(processor) |> ignore)

        builder.Build()

    let counters = ConcurrentDictionary<string, Counter<double>>()
    let histograms = ConcurrentDictionary<string, Histogram<double>>()
    let gaugeValues = ConcurrentDictionary<string * Map<string, string>, double>()
    let gauges = ConcurrentDictionary<string, ObservableGauge<double>>()

    let meterProvider =
        let builder =
            Sdk.CreateMeterProviderBuilder().SetResourceBuilder(resource).AddMeter(Instrumentation.MeterName)

        TelemetryDestination.otlp destination
        |> Option.iter (fun otlp ->
            let options = OtlpExporterOptions()
            configure options otlp.Endpoints.Metrics otlp
            let exporter = new OtlpMetricExporter(options)

            let reader =
                new PeriodicExportingMetricReader(
                    exporter,
                    int (TelemetryExportConfiguration.exportScheduleDelay configuration).TotalMilliseconds,
                    int (TelemetryExportConfiguration.exportTimeout configuration).TotalMilliseconds
                )

            builder.AddReader(reader) |> ignore)

        builder.Build()

    let loggerFactory =
        LoggerFactory.Create(fun builder ->
            builder.SetMinimumLevel(LogLevel.Information) |> ignore

            builder.AddOpenTelemetry(fun options ->
                options.SetResourceBuilder(resource) |> ignore
                options.IncludeScopes <- true
                options.IncludeFormattedMessage <- true
                options.ParseStateValues <- true

                TelemetryDestination.otlp destination
                |> Option.iter (fun otlp ->
                    let exporterOptions = OtlpExporterOptions()
                    configure exporterOptions otlp.Endpoints.Logs otlp
                    let exporter = new OtlpLogExporter(exporterOptions)

                    let processor =
                        new BatchLogRecordExportProcessor(
                            exporter,
                            TelemetryExportConfiguration.exporterQueueCapacity configuration,
                            int (TelemetryExportConfiguration.exportScheduleDelay configuration).TotalMilliseconds,
                            int (TelemetryExportConfiguration.exportTimeout configuration).TotalMilliseconds,
                            TelemetryExportConfiguration.exportBatchSize configuration
                        )

                    logProcessor <- Some processor
                    options.AddProcessor(processor) |> ignore))
            |> ignore)

    let logger = loggerFactory.CreateLogger("Nao.Telemetry")

    let emitMetric name labels value =
        let instrument =
            TelemetryInstrument.tryFind name
            |> Option.defaultWith (fun () -> invalidOp $"Unknown canonical telemetry instrument '{name}'.")

        TelemetryAttributes.validateMetricLabels instrument labels |> ignore
        let tags = OpenTelemetryConversion.labelTags labels

        match TelemetryInstrument.kind instrument with
        | TelemetryInstrumentKind.Counter ->
            counters
                .GetOrAdd(name, fun _ -> meter.CreateCounter<double>(name, unit = TelemetryInstrument.unit instrument))
                .Add(value, tags)
        | TelemetryInstrumentKind.Histogram ->
            histograms
                .GetOrAdd(
                    name,
                    fun _ -> meter.CreateHistogram<double>(name, unit = TelemetryInstrument.unit instrument)
                )
                .Record(value, tags)
        | TelemetryInstrumentKind.Gauge ->
            gauges.GetOrAdd(
                name,
                fun _ ->
                    meter.CreateObservableGauge<double>(
                        name,
                        (fun () ->
                            gaugeValues
                            |> Seq.choose (fun pair ->
                                let instrumentName, labels = pair.Key

                                if instrumentName = name then
                                    Some(Measurement<double>(pair.Value, OpenTelemetryConversion.labelTags labels))
                                else
                                    None)),
                        unit = TelemetryInstrument.unit instrument
                    )
            )
            |> ignore

            gaugeValues.[(name, labels)] <- value

        registry.Record(instrument, labels, value)

    let writeLog (scope: EventScope) signalType =
        LoggerExtensions.LogInformation(
            logger,
            "Nao telemetry signal {SignalType} for tenant {TenantId}, execution {ExecutionId}, correlation {CorrelationId}, and turn {TurnId}",
            [| signalType :> obj
               scope.TenantId :> obj
               ExecutionId.serialize scope.Correlation.ExecutionId :> obj
               CorrelationId.serialize scope.Correlation.CorrelationId :> obj
               scope.ActionId :> obj |]
        )

    let log (scope: EventScope) signalType =
        match executionActivities.TryGetValue scope.Correlation.ExecutionId with
        | true, activity ->
            let current = Activity.Current

            try
                Activity.Current <- activity
                writeLog scope signalType
            finally
                Activity.Current <- current
        | false, _ -> writeLog scope signalType

    let withActivity spanId action =
        match activities.TryGetValue spanId with
        | true, activity ->
            let current = Activity.Current

            try
                Activity.Current <- activity
                action activity
            finally
                Activity.Current <- current
        | false, _ -> ()

    let startSpan (span: Span) =
        let traceId = OpenTelemetryConversion.activityTraceId span.TraceId

        let parentSpanId =
            span.ParentSpanId
            |> Option.map (fun parentId ->
                match activities.TryGetValue parentId with
                | true, parent -> parent.SpanId
                | false, _ -> OpenTelemetryConversion.activitySpanId parentId)
            |> Option.defaultValue (ActivitySpanId.CreateRandom())

        let parent = ActivityContext(traceId, parentSpanId, ActivityTraceFlags.Recorded)
        let current = Activity.Current

        try
            Activity.Current <- null

            let activity =
                source.StartActivity(
                    span.OperationName,
                    ActivityKind.Internal,
                    parent,
                    OpenTelemetryConversion.tags span.Attributes,
                    startTime = span.StartTime
                )

            if not (isNull activity) then
                activities.[span.Id] <- activity

                if span.ParentSpanId.IsNone then
                    executionActivities.[span.Correlation.ExecutionId] <- activity

                if span.OperationName = TelemetrySpan.ProviderCall then
                    providerActivities.[span.Correlation.ExecutionId] <- activity
        finally
            Activity.Current <- current

    let endSpan (span: Span) status =
        withActivity span.Id (fun activity ->
            let code, description = OpenTelemetryConversion.status status
            activity.SetStatus(code, description) |> ignore

            if span.ParentSpanId.IsNone then
                let tag name fallback =
                    match activity.GetTagItem name with
                    | :? string as value when not (String.IsNullOrWhiteSpace value) -> value
                    | _ -> fallback

                let terminal =
                    tag
                        TelemetryAttribute.TerminalState
                        (match status with
                         | SpanStatus.Ok -> TelemetryTerminalState.value TelemetryTerminalState.Succeeded
                         | SpanStatus.Cancelled -> TelemetryTerminalState.value TelemetryTerminalState.Cancelled
                         | SpanStatus.Error _ -> TelemetryTerminalState.value TelemetryTerminalState.Failed)

                let error =
                    tag
                        TelemetryAttribute.ErrorCategory
                        (match status with
                         | SpanStatus.Error category -> TelemetryErrorCategory.value category
                         | _ -> TelemetryErrorCategory.value TelemetryErrorCategory.None)

                let agent = tag TelemetryAttribute.AgentId "unknown"

                emitMetric
                    "nao.execution.count"
                    (Map
                        [ TelemetryAttribute.AgentId, agent
                          TelemetryAttribute.TerminalState, terminal
                          TelemetryAttribute.ErrorCategory, error ])
                    1.0

                emitMetric
                    "nao.execution.duration"
                    (Map
                        [ TelemetryAttribute.AgentId, agent
                          TelemetryAttribute.TerminalState, terminal ])
                    (max 0.0 (DateTimeOffset.UtcNow - span.StartTime).TotalMilliseconds)

            activity.Stop()
            let mutable removed = Unchecked.defaultof<Activity>
            activities.TryRemove(span.Id, &removed) |> ignore

            if span.ParentSpanId.IsNone then
                let mutable root = Unchecked.defaultof<Activity>
                executionActivities.TryRemove(span.Correlation.ExecutionId, &root) |> ignore

            if span.OperationName = TelemetrySpan.ProviderCall then
                let mutable provider = Unchecked.defaultof<Activity>
                providerActivities.TryRemove(span.Correlation.ExecutionId, &provider) |> ignore)

    let logSpan scope (span: Span) signalType =
        withActivity span.Id (fun _ -> writeLog scope signalType)

    let recordMetric (metricRecord: MetricRecord) =
        match metricRecord.Payload with
        | MetricPayload.LlmCall(tokens, latencyMs) ->
            let providerLabels =
                match providerActivities.TryGetValue metricRecord.Correlation.ExecutionId with
                | true, activity ->
                    [ TelemetryAttribute.ProviderName; TelemetryAttribute.ModelName ]
                    |> List.choose (fun name ->
                        match activity.GetTagItem name with
                        | :? string as value when not (String.IsNullOrWhiteSpace value) -> Some(name, value)
                        | _ -> None)
                    |> Map.ofList
                | false, _ -> Map.empty

            emitMetric "nao.provider.call.count" providerLabels 1.0
            emitMetric "nao.provider.call.duration" providerLabels (float latencyMs)

            match tokens with
            | ProviderTokenUsage.Split(inputTokens, outputTokens) ->
                emitMetric
                    "nao.provider.token.count"
                    (providerLabels |> Map.add "nao.token.type" "input")
                    (float inputTokens)

                emitMetric
                    "nao.provider.token.count"
                    (providerLabels |> Map.add "nao.token.type" "output")
                    (float outputTokens)
            | ProviderTokenUsage.Aggregate totalTokens ->
                emitMetric
                    "nao.provider.token.count"
                    (providerLabels |> Map.add "nao.token.type" "aggregate")
                    (float totalTokens)
            | ProviderTokenUsage.Unavailable -> ()
        | MetricPayload.ToolCall(toolName, durationMs, success) ->
            let error =
                if success then
                    TelemetryErrorCategory.None
                else
                    TelemetryErrorCategory.Dependency

            emitMetric
                "nao.tool.call.count"
                (Map
                    [ TelemetryAttribute.ToolName, toolName
                      TelemetryAttribute.ErrorCategory, TelemetryErrorCategory.value error ])
                1.0

            emitMetric "nao.tool.call.duration" (Map [ TelemetryAttribute.ToolName, toolName ]) (float durationMs)
        | MetricPayload.Custom point -> emitMetric point.Name point.Labels point.Value
        | MetricPayload.EmbeddingCall(_, _, _, _, _, estimatedCostUsd, providerAttempts) ->
            emitMetric "nao.provider.cost" Map.empty (float estimatedCostUsd)

            if providerAttempts > 1 then
                emitMetric "nao.provider.retry.count" Map.empty (float (providerAttempts - 1))

    interface IDisposable with
        member _.Dispose() =
            loggerFactory.Dispose()
            traceProvider.Dispose()
            meterProvider.Dispose()
            source.Dispose()
            meter.Dispose()

    member _.Sink =
        { WriteAsync =
            fun scope signal _ ->
                match signal with
                | SpanStarted span ->
                    startSpan span
                    logSpan scope span "span.started"
                | SpanAttributesSet(span, attributes) ->
                    withActivity span.Id (fun activity ->
                        attributes |> Map.iter (fun key value -> activity.SetTag(key, value) |> ignore))
                | SpanEventAdded(span, name, attributes) ->
                    withActivity span.Id (fun activity ->
                        activity.AddEvent(
                            ActivityEvent(name, tags = ActivityTagsCollection(OpenTelemetryConversion.tags attributes))
                        )
                        |> ignore)
                | SpanEnded(span, status) ->
                    logSpan scope span "span.ended"
                    endSpan span status
                | MetricRecorded record ->
                    recordMetric record
                    log scope "metric.recorded"
                | _ -> log scope "recorded"

                Task.CompletedTask
          ForceFlush =
            fun timeout ->
                let stopwatch = Stopwatch.StartNew()

                let flush operation =
                    let remaining = timeout - stopwatch.Elapsed
                    remaining > TimeSpan.Zero && operation (max 1 (int remaining.TotalMilliseconds))

                let traces = flush (fun milliseconds -> traceProvider.ForceFlush milliseconds)
                let metrics = flush (fun milliseconds -> meterProvider.ForceFlush milliseconds)

                let logs =
                    logProcessor
                    |> Option.map (fun processor -> flush (fun milliseconds -> processor.ForceFlush milliseconds))
                    |> Option.defaultValue true

                traces && metrics && logs
          Shutdown =
            fun timeout ->
                logProcessor
                |> Option.map (fun processor -> processor.Shutdown(max 1 (int timeout.TotalMilliseconds)))
                |> Option.defaultValue true
          ScrapePrometheus = registry.Render }

[<RequireQualifiedAccess>]
module OpenTelemetryExporter =
    let create configuration =
        let regional =
            TelemetryExportConfiguration.destinations configuration
            |> Map.map (fun _ destination -> new OpenTelemetrySignalSink(configuration, destination))

        let privacy = TelemetryExportConfiguration.privacy configuration
        let privacyCounters = Array.zeroCreate<int64> 4

        let applyToAll timeout operation =
            let stopwatch = Stopwatch.StartNew()

            regional
            |> Map.toSeq
            |> Seq.forall (fun (_, sink) ->
                let remaining = timeout - stopwatch.Elapsed
                remaining > TimeSpan.Zero && operation sink remaining)

        let sink =
            { WriteAsync =
                fun scope signal cancellationToken ->
                    task {
                        match TelemetryPrivacy.evaluate privacy scope signal with
                        | TelemetryPrivacy.Decision.SampledOut -> Interlocked.Increment(&privacyCounters.[1]) |> ignore
                        | TelemetryPrivacy.Decision.Expired -> Interlocked.Increment(&privacyCounters.[2]) |> ignore
                        | TelemetryPrivacy.Decision.MissingPolicy ->
                            Interlocked.Increment(&privacyCounters.[3]) |> ignore
                        | TelemetryPrivacy.Decision.Export(region, sanitized, evidence) ->
                            let destination =
                                regional
                                |> Map.tryFind region
                                |> Option.defaultWith (fun () ->
                                    invalidOp $"No telemetry destination is configured for region '{region}'.")

                            match evidence with
                            | Some value ->
                                do! TelemetryPrivacy.recordEvidenceAsync privacy value
                                Interlocked.Increment(&privacyCounters.[0]) |> ignore
                            | None -> ()

                            do! destination.Sink.WriteAsync scope sanitized cancellationToken
                    }
                    :> Task
              ForceFlush =
                fun timeout -> applyToAll timeout (fun regionalSink remaining -> regionalSink.Sink.ForceFlush remaining)
              Shutdown =
                fun timeout -> applyToAll timeout (fun regionalSink remaining -> regionalSink.Sink.Shutdown remaining)
              ScrapePrometheus =
                fun () ->
                    regional
                    |> Map.toSeq
                    |> Seq.map (fun (_, value) -> value.Sink.ScrapePrometheus())
                    |> String.concat "" }

        let runtime = TelemetryExportRuntime.create configuration sink

        let statistics () =
            { Redacted = Interlocked.Read(&privacyCounters.[0])
              SampledOut = Interlocked.Read(&privacyCounters.[1])
              Expired = Interlocked.Read(&privacyCounters.[2])
              MissingPolicy = Interlocked.Read(&privacyCounters.[3]) }

        let resources =
            { new IDisposable with
                member _.Dispose() =
                    regional |> Map.iter (fun _ value -> (value :> IDisposable).Dispose()) }

        new OpenTelemetryExporterRuntime(runtime, statistics, resources)
