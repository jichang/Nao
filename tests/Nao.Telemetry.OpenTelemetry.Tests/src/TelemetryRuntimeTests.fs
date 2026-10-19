namespace Nao.Telemetry.OpenTelemetry.Tests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Telemetry.OpenTelemetry

[<TestClass>]
type TelemetryRuntimeTests() =
    let correlation = CorrelationContext.root ()

    let scope =
        EventScope.Create("tenant", "user", "session", "conversation", "workspace", "turn", "user/session", correlation)

    let metric index =
        let point =
            { Name = "nao.tool.call.duration"
              Value = float index
              Labels = Map [ TelemetryAttribute.ToolName, "search" ] }

        MetricRecorded(MetricRecord.custom correlation DateTimeOffset.UtcNow point)

    let configuration queueCapacity attempts retryDelay shutdownTimeout =
        TelemetryExportConfiguration.create
            "nao-tests"
            "1.0.0"
            Map.empty
            (Map
                [ "local",
                  TelemetryDestination.create
                      "local"
                      None
                      TelemetryTransportSecurity.LocalDevelopment
                      TelemetryAtRestEncryption.VolatileOnly ])
            TelemetryPrivacyConfiguration.local
            queueCapacity
            attempts
            retryDelay
            32
            8
            (TimeSpan.FromMilliseconds 10.0)
            (TimeSpan.FromSeconds 1.0)
            shutdownTimeout

    let sink write =
        { WriteAsync = write
          ForceFlush = fun _ -> true
          Shutdown = fun _ -> true
          ScrapePrometheus = fun () -> "" }

    let privacy policy clock =
        TelemetryPrivacyConfiguration.create
            (fun tenantId ->
                if tenantId = TelemetryTenantPolicy.tenantId policy then
                    Some policy
                else
                    None)
            "test-sampling-salt"
            (fun _ -> Task.CompletedTask)
            clock

    let configurationWithPrivacy privacy attempts =
        TelemetryExportConfiguration.create
            "nao-tests"
            "1.0.0"
            Map.empty
            (Map
                [ "local",
                  TelemetryDestination.create
                      "local"
                      None
                      TelemetryTransportSecurity.LocalDevelopment
                      TelemetryAtRestEncryption.VolatileOnly ])
            privacy
            32
            attempts
            TimeSpan.Zero
            32
            8
            (TimeSpan.FromMilliseconds 10.0)
            (TimeSpan.FromSeconds 1.0)
            (TimeSpan.FromSeconds 2.0)

    let span attributes =
        { Id = SpanId.generate ()
          Correlation = correlation
          TraceId = TraceId.ofCorrelation correlation.CorrelationId
          ParentSpanId = None
          OperationName = TelemetrySpan.Harness
          StartTime = DateTimeOffset.UtcNow
          EndTime = None
          Status = SpanStatus.Ok
          Attributes = attributes
          Events = [] }

    [<TestMethod>]
    member _.``Ingress never waits and drops newest signal when bounded queue is full``() : Task =
        task {
            let entered =
                TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            let release =
                TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            let exporter =
                TelemetryExportRuntime.create
                    (configuration 1 1 TimeSpan.Zero (TimeSpan.FromSeconds 2.0))
                    (sink (fun _ _ _ ->
                        task {
                            entered.TrySetResult() |> ignore
                            do! release.Task
                        }
                        :> Task))

            let consumer = exporter.Consumer
            do! EventConsumer.handleAsync (ObservabilityCaptured(scope, metric 1)) consumer
            do! entered.Task
            do! EventConsumer.handleAsync (ObservabilityCaptured(scope, metric 2)) consumer
            do! EventConsumer.handleAsync (ObservabilityCaptured(scope, metric 3)) consumer

            Assert.AreEqual(2L, exporter.Statistics.Accepted)
            Assert.AreEqual(1L, exporter.Statistics.Dropped)

            release.TrySetResult() |> ignore
            let! result = exporter.ShutdownAsync()
            Assert.AreEqual(TelemetryShutdownResult.Flushed, result)
            Assert.AreEqual(2L, exporter.Statistics.Processed)
        }

    [<TestMethod>]
    member _.``Transient sink failures retry within configured attempt bound``() : Task =
        task {
            let mutable attempts = 0

            let exporter =
                TelemetryExportRuntime.create
                    (configuration 4 3 TimeSpan.Zero (TimeSpan.FromSeconds 2.0))
                    (sink (fun _ _ _ ->
                        task {
                            attempts <- Interlocked.Increment(&attempts)

                            if attempts < 3 then
                                return raise (InvalidOperationException "transient")
                        }
                        :> Task))

            do! EventConsumer.handleAsync (ObservabilityCaptured(scope, metric 1)) exporter.Consumer
            let! result = exporter.ShutdownAsync()

            Assert.AreEqual(TelemetryShutdownResult.Flushed, result)
            Assert.AreEqual(3, attempts)
            Assert.AreEqual(1L, exporter.Statistics.Processed)
            Assert.AreEqual(0L, exporter.Statistics.Failed)
        }

    [<TestMethod>]
    member _.``Shutdown cancels a stuck sink at the configured deadline``() : Task =
        task {
            let entered =
                TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            let exporter =
                TelemetryExportRuntime.create
                    (configuration 1 1 TimeSpan.Zero (TimeSpan.FromMilliseconds 50.0))
                    (sink (fun _ _ cancellationToken ->
                        task {
                            entered.TrySetResult() |> ignore
                            do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        }
                        :> Task))

            do! EventConsumer.handleAsync (ObservabilityCaptured(scope, metric 1)) exporter.Consumer
            do! entered.Task
            let stopwatch = Stopwatch.StartNew()
            let! result = exporter.ShutdownAsync()

            Assert.AreEqual(TelemetryShutdownResult.TimedOut, result)
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds 1.0)
        }

    [<TestMethod>]
    [<DoNotParallelize>]
    member _.``OpenTelemetry bridge preserves Nao trace identity and parent relationship``() : Task =
        task {
            let stopped = ConcurrentQueue<Activity>()
            use listener = new ActivityListener()
            listener.ShouldListenTo <- fun source -> source.Name = "Nao"
            listener.Sample <- fun _ -> ActivitySamplingResult.AllDataAndRecorded
            listener.SampleUsingParentId <- fun _ -> ActivitySamplingResult.AllDataAndRecorded
            listener.ActivityStopped <- fun activity -> stopped.Enqueue activity
            ActivitySource.AddActivityListener listener

            use exporter =
                OpenTelemetryExporter.create (TelemetryExportConfiguration.local "nao-tests")

            let traceId = TraceId.ofCorrelation correlation.CorrelationId

            let root =
                { Id = SpanId.generate ()
                  Correlation = correlation
                  TraceId = traceId
                  ParentSpanId = None
                  OperationName = TelemetrySpan.Harness
                  StartTime = DateTimeOffset.UtcNow
                  EndTime = None
                  Status = SpanStatus.Ok
                  Attributes = Map.empty
                  Events = [] }

            let child =
                { root with
                    Id = SpanId.generate ()
                    ParentSpanId = Some root.Id
                    OperationName = TelemetrySpan.ProviderCall }

            for signal in
                [ SpanStarted root
                  SpanStarted child
                  SpanEnded(child, SpanStatus.Ok)
                  SpanEnded(root, SpanStatus.Ok) ] do
                do! EventConsumer.handleAsync (ObservabilityCaptured(scope, signal)) exporter.Consumer

            let! result = exporter.ShutdownAsync()
            Assert.AreEqual(TelemetryShutdownResult.Flushed, result)

            let spans = stopped.ToArray()
            Assert.AreEqual(2, spans.Length, spans |> Array.map _.DisplayName |> String.concat ", ")

            let exportedRoot =
                spans
                |> Array.find (fun activity -> activity.DisplayName = TelemetrySpan.Harness)

            let exportedChild =
                spans
                |> Array.find (fun activity -> activity.DisplayName = TelemetrySpan.ProviderCall)

            Assert.AreEqual(exportedRoot.TraceId, exportedChild.TraceId)
            Assert.AreEqual(exportedRoot.SpanId, exportedChild.ParentSpanId)

            let expectedTraceId =
                TraceId.serialize traceId |> fun value -> value.Replace("-", "")

            Assert.AreEqual(expectedTraceId, exportedRoot.TraceId.ToString())
        }

    [<TestMethod>]
    member _.``Prometheus scrape exposes canonical counters and histograms``() : Task =
        task {
            use exporter =
                OpenTelemetryExporter.create (TelemetryExportConfiguration.local "nao-tests")

            let record =
                MetricRecord.toolCall correlation DateTimeOffset.UtcNow "search" 25L true

            do! EventConsumer.handleAsync (ObservabilityCaptured(scope, MetricRecorded record)) exporter.Consumer

            let embedding =
                MetricRecord.embeddingCall correlation DateTimeOffset.UtcNow "profile" "batch" 2 12 20L 0.25m 3

            do! EventConsumer.handleAsync (ObservabilityCaptured(scope, MetricRecorded embedding)) exporter.Consumer

            let! result = exporter.ShutdownAsync()
            Assert.AreEqual(TelemetryShutdownResult.Flushed, result)
            Assert.AreEqual(2L, exporter.Statistics.Processed)
            Assert.AreEqual(0L, exporter.PrivacyStatistics.MissingPolicy)
            Assert.AreEqual(0L, exporter.PrivacyStatistics.Expired)
            Assert.AreEqual(0L, exporter.PrivacyStatistics.SampledOut)

            let scrape = exporter.ScrapePrometheus()
            StringAssert.Contains(scrape, "nao_tool_call_count_total")
            StringAssert.Contains(scrape, "nao_tool_call_duration_count")
            StringAssert.Contains(scrape, "nao_tool_name=\"search\"")
            StringAssert.Contains(scrape, "nao_provider_cost_total")
            StringAssert.Contains(scrape, "nao_provider_retry_count_total")
        }

    [<TestMethod>]
    member _.``HTTP protobuf exports traces metrics and correlated logs to OTLP endpoints``() : Task =
        task {
            use reservation = new TcpListener(IPAddress.Loopback, 0)
            reservation.Start()
            let port = (reservation.LocalEndpoint :?> IPEndPoint).Port
            reservation.Stop()

            use listener = new HttpListener()
            listener.Prefixes.Add($"http://127.0.0.1:{port}/")
            listener.Start()
            use stopped = new CancellationTokenSource()
            let requests = ConcurrentDictionary<string, int>()
            let requestBodies = ConcurrentQueue<string>()

            let server =
                task {
                    try
                        while not stopped.IsCancellationRequested do
                            let! context = listener.GetContextAsync().WaitAsync(stopped.Token)

                            let path =
                                match context.Request.Url with
                                | null -> invalidOp "OTLP test request URL is missing."
                                | url -> url.AbsolutePath

                            requests.AddOrUpdate(path, 1, fun _ count -> count + 1) |> ignore
                            use body = new MemoryStream()
                            do! context.Request.InputStream.CopyToAsync(body, stopped.Token)
                            requestBodies.Enqueue(Encoding.UTF8.GetString(body.ToArray()))
                            context.Response.StatusCode <- int HttpStatusCode.OK
                            context.Response.ContentType <- "application/x-protobuf"
                            context.Response.ContentLength64 <- 0L
                            context.Response.Close()
                    with :? OperationCanceledException ->
                        ()
                }

            let endpoints =
                { Traces = Uri $"http://127.0.0.1:{port}/v1/traces"
                  Metrics = Uri $"http://127.0.0.1:{port}/v1/metrics"
                  Logs = Uri $"http://127.0.0.1:{port}/v1/logs" }

            let otlp =
                { Endpoints = endpoints
                  Transport = OtlpTransport.HttpProtobuf
                  Headers = None }

            let exportConfiguration =
                let destination =
                    TelemetryDestination.create
                        "local"
                        (Some otlp)
                        TelemetryTransportSecurity.LocalDevelopment
                        (TelemetryAtRestEncryption.BackendManaged "local-test")

                let policy =
                    TelemetryTenantPolicy.create
                        "tenant"
                        "local"
                        (TimeSpan.FromHours 1.0)
                        TelemetryContentCapture.Redacted
                        1.0

                TelemetryExportConfiguration.create
                    "nao-tests"
                    "1.0.0"
                    Map.empty
                    (Map [ "local", destination ])
                    (privacy policy (fun () -> DateTimeOffset.UtcNow))
                    32
                    1
                    TimeSpan.Zero
                    32
                    8
                    (TimeSpan.FromMilliseconds 10.0)
                    (TimeSpan.FromSeconds 2.0)
                    (TimeSpan.FromSeconds 5.0)

            use exporter = OpenTelemetryExporter.create exportConfiguration

            let root =
                { Id = SpanId.generate ()
                  Correlation = correlation
                  TraceId = TraceId.ofCorrelation correlation.CorrelationId
                  ParentSpanId = None
                  OperationName = TelemetrySpan.Harness
                  StartTime = DateTimeOffset.UtcNow
                  EndTime = None
                  Status = SpanStatus.Ok
                  Attributes = Map.empty
                  Events = [] }

            let attributes =
                Map
                    [ TelemetryAttribute.AgentId, "test-agent"
                      TelemetryAttribute.TerminalState, "succeeded"
                      TelemetryAttribute.ErrorCategory, "none"
                      "custom.payload", "Bearer canary-secret-12345 owner@example.test +1 415-555-2671" ]

            for signal in
                [ SpanStarted root
                  SpanAttributesSet(root, attributes)
                  MetricRecorded(MetricRecord.toolCall correlation DateTimeOffset.UtcNow "search" 2L true)
                  SpanEnded(root, SpanStatus.Ok) ] do
                do! EventConsumer.handleAsync (ObservabilityCaptured(scope, signal)) exporter.Consumer

            let! result = exporter.ShutdownAsync()
            Assert.AreEqual(TelemetryShutdownResult.Flushed, result)

            let deadline = DateTimeOffset.UtcNow.AddSeconds 2.0

            while requests.Count < 3 && DateTimeOffset.UtcNow < deadline do
                do! Task.Delay 10

            CollectionAssert.AreEquivalent([| "/v1/traces"; "/v1/metrics"; "/v1/logs" |], requests.Keys |> Seq.toArray)
            let exportedPayload = String.concat "" requestBodies
            Assert.IsFalse(exportedPayload.Contains("canary-secret-12345"))
            Assert.IsFalse(exportedPayload.Contains("owner@example.test"))
            Assert.IsFalse(exportedPayload.Contains("415-555-2671"))

            stopped.Cancel()
            listener.Stop()
            do! server
        }

    [<TestMethod>]
    member _.``Privacy policy removes canary credentials and synthetic PII without retaining values``() =
        let policy =
            TelemetryTenantPolicy.create "tenant" "eu" (TimeSpan.FromHours 1.0) TelemetryContentCapture.Redacted 1.0

        let input =
            span (
                Map
                    [ TelemetryAttribute.AgentId, "owner@example.test"
                      "custom.payload", "Bearer canary-secret-12345 password=hunter2 call +1 415-555-2671" ]
            )

        match
            TelemetryPrivacy.evaluate (privacy policy (fun () -> DateTimeOffset.UtcNow)) scope (SpanStarted input)
        with
        | TelemetryPrivacy.Decision.Export(region, SpanStarted sanitized, Some evidence) ->
            Assert.AreEqual("eu", region)
            let exported = sanitized.Attributes |> Map.values |> String.concat " "
            Assert.IsFalse(exported.Contains("owner@example.test"))
            Assert.IsFalse(exported.Contains("canary-secret-12345"))
            Assert.IsFalse(exported.Contains("hunter2"))
            Assert.IsFalse(exported.Contains("415-555-2671"))
            CollectionAssert.Contains(evidence.RuleIds |> List.toArray, "personal.email")
            CollectionAssert.Contains(evidence.RuleIds |> List.toArray, "credential.bearer")
            CollectionAssert.Contains(evidence.RuleIds |> List.toArray, "credential.api_key")
            CollectionAssert.Contains(evidence.RuleIds |> List.toArray, "personal.phone")
            Assert.IsTrue(evidence.RemovedFieldCount >= 4)
            let auditText = sprintf "%A" evidence
            Assert.IsFalse(auditText.Contains("canary-secret-12345"))
            Assert.IsFalse(auditText.Contains("owner@example.test"))
        | other -> Assert.Fail($"Expected sanitized export with evidence, received {other}.")

    [<TestMethod>]
    member _.``Disabled content capture removes protected fields but preserves operational metadata``() =
        let policy =
            TelemetryTenantPolicy.create "tenant" "local" (TimeSpan.FromHours 1.0) TelemetryContentCapture.Disabled 1.0

        let input = span (Map [ "custom.payload", "private text"; "input.length", "12" ])

        match
            TelemetryPrivacy.evaluate (privacy policy (fun () -> DateTimeOffset.UtcNow)) scope (SpanStarted input)
        with
        | TelemetryPrivacy.Decision.Export(_, SpanStarted sanitized, Some evidence) ->
            Assert.AreEqual("[CONTENT_DISABLED]", sanitized.Attributes.["custom.payload"])
            Assert.AreEqual("12", sanitized.Attributes.["input.length"])
            CollectionAssert.Contains(evidence.RuleIds |> List.toArray, "content.disabled")
        | other -> Assert.Fail($"Expected content-disabled export, received {other}.")

    [<TestMethod>]
    member _.``Execution trace steps and action identifiers are redacted recursively``() =
        let policy =
            TelemetryTenantPolicy.create "tenant" "local" (TimeSpan.FromHours 1.0) TelemetryContentCapture.Redacted 1.0

        let trace =
            { Id = Guid.NewGuid()
              Correlation = correlation
              AgentId = "agent"
              Input = "owner@example.test"
              Output = None
              Steps =
                [ { StepNumber = 1
                    Action = TraceAction.ToolInvocation "token=canary-secret-12345"
                    Input = "call +1 415-555-2671"
                    Output = "password=hunter2"
                    DurationMs = 1L
                    Timestamp = DateTimeOffset.UtcNow } ]
              StartedAt = DateTimeOffset.UtcNow
              CompletedAt = None
              Success = true
              Metadata = Map.empty }

        match TelemetryPrivacy.evaluate (privacy policy (fun () -> DateTimeOffset.UtcNow)) scope (TraceSaved trace) with
        | TelemetryPrivacy.Decision.Export(_, TraceSaved sanitized, Some evidence) ->
            let serialized = sprintf "%A" sanitized
            Assert.IsFalse(serialized.Contains("owner@example.test"))
            Assert.IsFalse(serialized.Contains("canary-secret-12345"))
            Assert.IsFalse(serialized.Contains("415-555-2671"))
            Assert.IsFalse(serialized.Contains("hunter2"))
            Assert.IsTrue(evidence.RemovedFieldCount >= 4)
        | other -> Assert.Fail($"Expected recursively sanitized trace, received {other}.")

    [<TestMethod>]
    member _.``Retention routing and sampling decisions are tenant and trace consistent``() =
        let now = DateTimeOffset.UtcNow

        let policy =
            TelemetryTenantPolicy.create
                "tenant"
                "eu-west"
                (TimeSpan.FromMinutes 5.0)
                TelemetryContentCapture.Disabled
                0.5

        let configuration = privacy policy (fun () -> now)
        let signal = SpanStarted(span Map.empty)
        let first = TelemetryPrivacy.evaluate configuration scope signal
        let second = TelemetryPrivacy.evaluate configuration scope signal

        Assert.AreEqual(first, second)

        match first with
        | TelemetryPrivacy.Decision.Export(region, _, _) -> Assert.AreEqual("eu-west", region)
        | TelemetryPrivacy.Decision.SampledOut -> ()
        | other -> Assert.Fail($"Unexpected active trace decision {other}.")

        let expiredScope =
            { scope with
                Timestamp = now.AddMinutes -6.0 }

        Assert.AreEqual(TelemetryPrivacy.Decision.Expired, TelemetryPrivacy.evaluate configuration expiredScope signal)

        let unknownScope = { scope with TenantId = "unknown" }

        Assert.AreEqual(
            TelemetryPrivacy.Decision.MissingPolicy,
            TelemetryPrivacy.evaluate configuration unknownScope signal
        )

    [<TestMethod>]
    member _.``Production destinations require TLS and backend at-rest encryption``() =
        let insecure =
            { Traces = Uri "http://collector/v1/traces"
              Metrics = Uri "http://collector/v1/metrics"
              Logs = Uri "http://collector/v1/logs" }

        let otlp =
            { Endpoints = insecure
              Transport = OtlpTransport.HttpProtobuf
              Headers = None }

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            TelemetryDestination.create
                "eu"
                (Some otlp)
                TelemetryTransportSecurity.RequireTls
                (TelemetryAtRestEncryption.BackendManaged "telemetry-key")
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            TelemetryDestination.create
                "local"
                (Some otlp)
                TelemetryTransportSecurity.LocalDevelopment
                TelemetryAtRestEncryption.VolatileOnly
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Privacy counters distinguish every policy disposition``() : Task =
        task {
            let now = DateTimeOffset.UtcNow

            let policies =
                [ TelemetryTenantPolicy.create
                      "active"
                      "local"
                      (TimeSpan.FromHours 1.0)
                      TelemetryContentCapture.Redacted
                      1.0
                  TelemetryTenantPolicy.create
                      "sampled"
                      "local"
                      (TimeSpan.FromHours 1.0)
                      TelemetryContentCapture.Disabled
                      0.0
                  TelemetryTenantPolicy.create
                      "expired"
                      "local"
                      (TimeSpan.FromMinutes 1.0)
                      TelemetryContentCapture.Disabled
                      1.0 ]
                |> List.map (fun policy -> TelemetryTenantPolicy.tenantId policy, policy)
                |> Map

            let privacy =
                TelemetryPrivacyConfiguration.create
                    policies.TryFind
                    "counter-test-salt"
                    (fun _ -> Task.CompletedTask)
                    (fun () -> now)

            use exporter = OpenTelemetryExporter.create (configurationWithPrivacy privacy 1)

            let send tenant timestamp attributes =
                let eventScope =
                    { scope with
                        TenantId = tenant
                        Timestamp = timestamp }

                EventConsumer.handleAsync
                    (ObservabilityCaptured(eventScope, SpanStarted(span attributes)))
                    exporter.Consumer

            do! send "active" now (Map [ "custom.payload", "owner@example.test" ])
            do! send "sampled" now Map.empty
            do! send "expired" (now.AddMinutes -2.0) Map.empty
            do! send "missing" now Map.empty

            let! result = exporter.ShutdownAsync()
            Assert.AreEqual(TelemetryShutdownResult.Flushed, result)
            Assert.AreEqual(1L, exporter.PrivacyStatistics.Redacted)
            Assert.AreEqual(1L, exporter.PrivacyStatistics.SampledOut)
            Assert.AreEqual(1L, exporter.PrivacyStatistics.Expired)
            Assert.AreEqual(1L, exporter.PrivacyStatistics.MissingPolicy)
        }

    [<TestMethod>]
    member _.``Evidence recording failure prevents export and exhausts bounded attempts``() : Task =
        task {
            let policy =
                TelemetryTenantPolicy.create
                    "tenant"
                    "local"
                    (TimeSpan.FromHours 1.0)
                    TelemetryContentCapture.Redacted
                    1.0

            let privacy =
                TelemetryPrivacyConfiguration.create
                    (fun tenantId -> if tenantId = "tenant" then Some policy else None)
                    "evidence-failure-salt"
                    (fun _ -> Task.FromException(InvalidOperationException "evidence unavailable"))
                    (fun () -> DateTimeOffset.UtcNow)

            use exporter = OpenTelemetryExporter.create (configurationWithPrivacy privacy 2)

            do!
                EventConsumer.handleAsync
                    (ObservabilityCaptured(scope, SpanStarted(span (Map [ "custom.payload", "owner@example.test" ]))))
                    exporter.Consumer

            let! result = exporter.ShutdownAsync()
            Assert.AreEqual(TelemetryShutdownResult.Flushed, result)
            Assert.AreEqual(0L, exporter.Statistics.Processed)
            Assert.AreEqual(1L, exporter.Statistics.Failed)
            Assert.AreEqual(0L, exporter.PrivacyStatistics.Redacted)
        }
