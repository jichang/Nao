namespace Nao.Telemetry.OpenTelemetry

open System

[<RequireQualifiedAccess>]
type OtlpTransport =
    | Grpc
    | HttpProtobuf

type OtlpEndpoints =
    { Traces: Uri; Metrics: Uri; Logs: Uri }

type OtlpConfiguration =
    { Endpoints: OtlpEndpoints
      Transport: OtlpTransport
      Headers: string option }

[<RequireQualifiedAccess>]
type TelemetryTransportSecurity =
    | RequireTls
    | LocalDevelopment

[<RequireQualifiedAccess>]
type TelemetryAtRestEncryption =
    | VolatileOnly
    | BackendManaged of keyId: string

type TelemetryDestination =
    private
        { Region: string
          Otlp: OtlpConfiguration option
          TransportSecurity: TelemetryTransportSecurity
          AtRestEncryption: TelemetryAtRestEncryption }

[<RequireQualifiedAccess>]
module TelemetryDestination =
    let create region otlp transportSecurity atRestEncryption =
        if String.IsNullOrWhiteSpace region || region <> region.Trim() then
            invalidArg (nameof region) "Telemetry destination region must be canonical and non-blank."

        match transportSecurity, otlp with
        | TelemetryTransportSecurity.RequireTls, Some value ->
            let endpoints =
                [ value.Endpoints.Traces; value.Endpoints.Metrics; value.Endpoints.Logs ]

            if endpoints |> List.exists (fun endpoint -> endpoint.Scheme <> Uri.UriSchemeHttps) then
                invalidArg (nameof otlp) "TLS telemetry destinations require HTTPS OTLP endpoints."
        | TelemetryTransportSecurity.RequireTls, None ->
            invalidArg (nameof otlp) "A production telemetry destination requires OTLP endpoints."
        | TelemetryTransportSecurity.LocalDevelopment, _ -> ()

        match atRestEncryption, otlp with
        | TelemetryAtRestEncryption.VolatileOnly, Some _ ->
            invalidArg
                (nameof atRestEncryption)
                "An external telemetry destination requires backend-managed at-rest encryption."
        | TelemetryAtRestEncryption.BackendManaged keyId, _ when
            String.IsNullOrWhiteSpace keyId || keyId <> keyId.Trim()
            ->
            invalidArg (nameof atRestEncryption) "At-rest encryption key identity must be canonical and non-blank."
        | _ -> ()

        { Region = region
          Otlp = otlp
          TransportSecurity = transportSecurity
          AtRestEncryption = atRestEncryption }

    let region value = value.Region
    let otlp value = value.Otlp
    let transportSecurity value = value.TransportSecurity
    let atRestEncryption value = value.AtRestEncryption

type TelemetryExportConfiguration =
    private
        { ServiceName: string
          ServiceVersion: string
          ResourceAttributes: Map<string, string>
          Destinations: Map<string, TelemetryDestination>
          Privacy: TelemetryPrivacyConfiguration
          QueueCapacity: int
          MaximumSignalAttempts: int
          InitialRetryDelay: TimeSpan
          ExporterQueueCapacity: int
          ExportBatchSize: int
          ExportScheduleDelay: TimeSpan
          ExportTimeout: TimeSpan
          ShutdownTimeout: TimeSpan }

[<RequireQualifiedAccess>]
module TelemetryExportConfiguration =
    let create
        serviceName
        serviceVersion
        resourceAttributes
        destinations
        privacy
        queueCapacity
        maximumSignalAttempts
        initialRetryDelay
        exporterQueueCapacity
        exportBatchSize
        exportScheduleDelay
        exportTimeout
        shutdownTimeout
        =
        if String.IsNullOrWhiteSpace serviceName || serviceName <> serviceName.Trim() then
            invalidArg (nameof serviceName) "Telemetry service name must be canonical and non-blank."

        if
            String.IsNullOrWhiteSpace serviceVersion
            || serviceVersion <> serviceVersion.Trim()
        then
            invalidArg (nameof serviceVersion) "Telemetry service version must be canonical and non-blank."

        if queueCapacity <= 0 || exporterQueueCapacity <= 0 then
            invalidArg (nameof queueCapacity) "Telemetry queue capacities must be positive."

        if maximumSignalAttempts <= 0 then
            invalidArg (nameof maximumSignalAttempts) "Telemetry signal attempts must be positive."

        if exportBatchSize <= 0 || exportBatchSize > exporterQueueCapacity then
            invalidArg (nameof exportBatchSize) "Export batch size must fit within the exporter queue."

        if
            initialRetryDelay < TimeSpan.Zero
            || exportScheduleDelay < TimeSpan.Zero
            || exportTimeout <= TimeSpan.Zero
            || shutdownTimeout <= TimeSpan.Zero
        then
            invalidArg (nameof shutdownTimeout) "Telemetry durations must be non-negative and deadlines positive."

        resourceAttributes
        |> Map.iter (fun key value ->
            if
                String.IsNullOrWhiteSpace key
                || String.IsNullOrWhiteSpace value
                || key <> key.Trim()
                || value <> value.Trim()
            then
                invalidArg (nameof resourceAttributes) "Resource attributes must be canonical and non-blank.")

        if Map.isEmpty destinations then
            invalidArg (nameof destinations) "At least one telemetry destination is required."

        destinations
        |> Map.iter (fun region destination ->
            if region <> TelemetryDestination.region destination then
                invalidArg (nameof destinations) "Telemetry destination map keys must match their regions."

            TelemetryDestination.otlp destination
            |> Option.iter (fun value ->
                let endpoints =
                    [ value.Endpoints.Traces; value.Endpoints.Metrics; value.Endpoints.Logs ]

                if endpoints |> List.exists (fun endpoint -> not endpoint.IsAbsoluteUri) then
                    invalidArg (nameof destinations) "OTLP endpoints must be absolute URIs."

                value.Headers
                |> Option.iter (fun headers ->
                    if String.IsNullOrWhiteSpace headers || headers <> headers.Trim() then
                        invalidArg (nameof destinations) "OTLP headers must be canonical and non-blank.")))

        { ServiceName = serviceName
          ServiceVersion = serviceVersion
          ResourceAttributes = resourceAttributes
          Destinations = destinations
          Privacy = privacy
          QueueCapacity = queueCapacity
          MaximumSignalAttempts = maximumSignalAttempts
          InitialRetryDelay = initialRetryDelay
          ExporterQueueCapacity = exporterQueueCapacity
          ExportBatchSize = exportBatchSize
          ExportScheduleDelay = exportScheduleDelay
          ExportTimeout = exportTimeout
          ShutdownTimeout = shutdownTimeout }

    let local serviceName =
        create
            serviceName
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
            2048
            3
            (TimeSpan.FromMilliseconds 25.0)
            2048
            512
            (TimeSpan.FromSeconds 5.0)
            (TimeSpan.FromSeconds 10.0)
            (TimeSpan.FromSeconds 15.0)

    let serviceName value = value.ServiceName
    let serviceVersion value = value.ServiceVersion
    let resourceAttributes value = value.ResourceAttributes
    let destinations value = value.Destinations
    let privacy value = value.Privacy
    let queueCapacity value = value.QueueCapacity
    let maximumSignalAttempts value = value.MaximumSignalAttempts
    let initialRetryDelay value = value.InitialRetryDelay
    let exporterQueueCapacity value = value.ExporterQueueCapacity
    let exportBatchSize value = value.ExportBatchSize
    let exportScheduleDelay value = value.ExportScheduleDelay
    let exportTimeout value = value.ExportTimeout
    let shutdownTimeout value = value.ShutdownTimeout
