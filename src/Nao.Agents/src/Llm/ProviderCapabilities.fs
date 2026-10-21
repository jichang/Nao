namespace Nao.Agents

open System

[<RequireQualifiedAccess>]
type ProviderCapability =
    | Chat
    | Streaming
    | ToolCalling
    | StructuredOutput
    | Vision
    | Audio
    | Embeddings
    | UsageReporting

[<RequireQualifiedAccess>]
module ProviderCapability =
    let all =
        [ ProviderCapability.Chat
          ProviderCapability.Streaming
          ProviderCapability.ToolCalling
          ProviderCapability.StructuredOutput
          ProviderCapability.Vision
          ProviderCapability.Audio
          ProviderCapability.Embeddings
          ProviderCapability.UsageReporting ]

[<RequireQualifiedAccess>]
type CapabilitySupport =
    | Supported
    | Unsupported
    | Unknown

[<RequireQualifiedAccess>]
type UsageReportingLevel =
    | None
    | Aggregate
    | Partial
    | Full
    | Unknown

type ProviderCapabilities =
    private
        { Support: Map<ProviderCapability, CapabilitySupport>
          ContextWindowTokens: int option
          UsageReporting: UsageReportingLevel }

[<RequireQualifiedAccess>]
module ProviderCapabilities =
    let create support contextWindowTokens usageReporting =
        let missing =
            ProviderCapability.all
            |> List.filter (fun capability -> not (Map.containsKey capability support))

        if not missing.IsEmpty then
            invalidArg
                (nameof support)
                $"Every provider capability requires an explicit observation. Missing: {missing}."

        if support.Count <> ProviderCapability.all.Length then
            invalidArg (nameof support) "Provider capabilities contain an unsupported capability key."

        contextWindowTokens
        |> Option.iter (fun value ->
            if value <= 0 then
                invalidArg (nameof contextWindowTokens) "Context-window size must be positive when known.")

        match support.[ProviderCapability.UsageReporting], usageReporting with
        | CapabilitySupport.Supported, UsageReportingLevel.None
        | CapabilitySupport.Supported, UsageReportingLevel.Unknown ->
            invalidArg (nameof usageReporting) "Supported usage reporting requires a known non-empty reporting level."
        | CapabilitySupport.Unsupported, UsageReportingLevel.None -> ()
        | CapabilitySupport.Unsupported, _ ->
            invalidArg (nameof usageReporting) "Unsupported usage reporting must use the none level."
        | CapabilitySupport.Unknown, UsageReportingLevel.Unknown -> ()
        | CapabilitySupport.Unknown, _ ->
            invalidArg (nameof usageReporting) "Unknown usage-reporting support requires an unknown level."
        | _ -> ()

        { Support = support
          ContextWindowTokens = contextWindowTokens
          UsageReporting = usageReporting }

    let unknown =
        ProviderCapability.all
        |> List.map (fun capability -> capability, CapabilitySupport.Unknown)
        |> Map
        |> fun support -> create support None UsageReportingLevel.Unknown

    let support capability value = value.Support.[capability]
    let contextWindowTokens value = value.ContextWindowTokens
    let usageReporting value = value.UsageReporting

[<RequireQualifiedAccess>]
type ProviderQualityTier =
    | Economy
    | Standard
    | Premium

[<RequireQualifiedAccess>]
type ProviderLatencyClass =
    | Interactive
    | Throughput
    | Batch

[<RequireQualifiedAccess>]
type ProviderPrivacyClass =
    | LocalOnly
    | ZeroRetention
    | ProviderRetained

[<RequireQualifiedAccess>]
type ProviderDeprecation =
    | Active
    | Deprecated of retirementAt: DateTimeOffset
    | Retired of retiredAt: DateTimeOffset

type ProviderModelPrice =
    private
        { Currency: string
          InputPerMillionTokens: decimal
          OutputPerMillionTokens: decimal }

[<RequireQualifiedAccess>]
module ProviderModelPrice =
    let create currency inputPerMillionTokens outputPerMillionTokens =
        if
            String.IsNullOrWhiteSpace currency
            || currency <> currency.Trim().ToUpperInvariant()
        then
            invalidArg (nameof currency) "Provider price currency must be an uppercase canonical code."

        if inputPerMillionTokens < 0m || outputPerMillionTokens < 0m then
            invalidArg (nameof inputPerMillionTokens) "Provider token prices cannot be negative."

        { Currency = currency
          InputPerMillionTokens = inputPerMillionTokens
          OutputPerMillionTokens = outputPerMillionTokens }

    let currency value = value.Currency
    let inputPerMillionTokens value = value.InputPerMillionTokens
    let outputPerMillionTokens value = value.OutputPerMillionTokens

type ProviderModelMetadata =
    private
        { ModelId: string
          QualityTier: ProviderQualityTier
          LatencyClass: ProviderLatencyClass
          ResidencyRegions: Set<string>
          PrivacyClass: ProviderPrivacyClass
          Price: ProviderModelPrice
          Deprecation: ProviderDeprecation }

[<RequireQualifiedAccess>]
module ProviderModelMetadata =
    let create modelId qualityTier latencyClass residencyRegions privacyClass price deprecation =
        if String.IsNullOrWhiteSpace modelId || modelId <> modelId.Trim() then
            invalidArg (nameof modelId) "Provider model identity must be canonical and non-blank."

        if Set.isEmpty residencyRegions then
            invalidArg (nameof residencyRegions) "Provider model residency must name at least one region."

        residencyRegions
        |> Set.iter (fun region ->
            if String.IsNullOrWhiteSpace region || region <> region.Trim() then
                invalidArg (nameof residencyRegions) "Provider residency regions must be canonical and non-blank.")

        { ModelId = modelId
          QualityTier = qualityTier
          LatencyClass = latencyClass
          ResidencyRegions = residencyRegions
          PrivacyClass = privacyClass
          Price = price
          Deprecation = deprecation }

    let modelId value = value.ModelId
    let qualityTier value = value.QualityTier
    let latencyClass value = value.LatencyClass
    let residencyRegions value = value.ResidencyRegions
    let privacyClass value = value.PrivacyClass
    let price value = value.Price
    let deprecation value = value.Deprecation

[<RequireQualifiedAccess>]
type ProviderEndpointHealth =
    | Healthy
    | Degraded
    | Unavailable

[<RequireQualifiedAccess>]
type ProviderEndpointReadiness =
    | Ready
    | NotReady

[<RequireQualifiedAccess>]
type ProviderQuotaStatus =
    | Available
    | Limited
    | Exhausted
    | Unknown

[<RequireQualifiedAccess>]
type ProviderMaintenanceStatus =
    | Active
    | Draining
    | Maintenance

[<RequireQualifiedAccess>]
type CapabilityObservationSource =
    | Configured
    | Probed

type ProviderCapabilityObservation =
    private
        { EndpointId: string
          Version: string
          ObservedAt: DateTimeOffset
          Source: CapabilityObservationSource
          Capabilities: ProviderCapabilities
          Model: ProviderModelMetadata
          Health: ProviderEndpointHealth
          Readiness: ProviderEndpointReadiness
          Quota: ProviderQuotaStatus
          Maintenance: ProviderMaintenanceStatus }

[<RequireQualifiedAccess>]
module ProviderCapabilityObservation =
    let create endpointId version observedAt source capabilities model health readiness quota maintenance =
        if String.IsNullOrWhiteSpace endpointId || endpointId <> endpointId.Trim() then
            invalidArg (nameof endpointId) "Provider endpoint identity must be canonical and non-blank."

        if String.IsNullOrWhiteSpace version || version <> version.Trim() then
            invalidArg (nameof version) "Capability observation version must be canonical and non-blank."

        { EndpointId = endpointId
          Version = version
          ObservedAt = observedAt
          Source = source
          Capabilities = capabilities
          Model = model
          Health = health
          Readiness = readiness
          Quota = quota
          Maintenance = maintenance }

    let endpointId value = value.EndpointId
    let version value = value.Version
    let observedAt value = value.ObservedAt
    let source value = value.Source
    let capabilities value = value.Capabilities
    let model value = value.Model
    let health value = value.Health
    let readiness value = value.Readiness
    let quota value = value.Quota
    let maintenance value = value.Maintenance

type ProviderRequirements =
    private
        { Capabilities: Set<ProviderCapability>
          MinimumContextWindowTokens: int option
          MinimumUsageReporting: UsageReportingLevel option }

[<RequireQualifiedAccess>]
module ProviderRequirements =
    let create capabilities minimumContextWindowTokens minimumUsageReporting =
        minimumContextWindowTokens
        |> Option.iter (fun value ->
            if value <= 0 then
                invalidArg (nameof minimumContextWindowTokens) "Required context-window size must be positive.")

        minimumUsageReporting
        |> Option.iter (fun value ->
            match value with
            | UsageReportingLevel.None
            | UsageReportingLevel.Unknown ->
                invalidArg
                    (nameof minimumUsageReporting)
                    "Minimum usage reporting must require aggregate, partial, or full reporting."
            | _ -> ())

        { Capabilities = capabilities
          MinimumContextWindowTokens = minimumContextWindowTokens
          MinimumUsageReporting = minimumUsageReporting }

    let capabilities value = value.Capabilities
    let minimumContextWindowTokens value = value.MinimumContextWindowTokens
    let minimumUsageReporting value = value.MinimumUsageReporting

[<RequireQualifiedAccess>]
type ProviderCompatibilityFailure =
    | UnsupportedCapability of ProviderCapability
    | UnknownCapability of ProviderCapability
    | ContextWindowTooSmall of requiredTokens: int * availableTokens: int
    | ContextWindowUnknown of requiredTokens: int
    | UsageReportingInsufficient of requiredLevel: UsageReportingLevel * availableLevel: UsageReportingLevel
    | EndpointUnhealthy of ProviderEndpointHealth
    | EndpointNotReady
    | QuotaUnavailable of ProviderQuotaStatus
    | EndpointDraining
    | ModelRetired

[<RequireQualifiedAccess>]
module ProviderCompatibility =
    let private usageRank =
        function
        | UsageReportingLevel.None -> 0
        | UsageReportingLevel.Aggregate -> 1
        | UsageReportingLevel.Partial -> 2
        | UsageReportingLevel.Full -> 3
        | UsageReportingLevel.Unknown -> -1

    let evaluate requirements observation =
        let capabilities = ProviderCapabilityObservation.capabilities observation

        let capabilityFailures =
            ProviderRequirements.capabilities requirements
            |> Seq.choose (fun capability ->
                match ProviderCapabilities.support capability capabilities with
                | CapabilitySupport.Supported -> None
                | CapabilitySupport.Unsupported -> Some(ProviderCompatibilityFailure.UnsupportedCapability capability)
                | CapabilitySupport.Unknown -> Some(ProviderCompatibilityFailure.UnknownCapability capability))
            |> Seq.toList

        let contextFailures =
            match
                ProviderRequirements.minimumContextWindowTokens requirements,
                ProviderCapabilities.contextWindowTokens capabilities
            with
            | Some required, Some available when available < required ->
                [ ProviderCompatibilityFailure.ContextWindowTooSmall(required, available) ]
            | Some required, None -> [ ProviderCompatibilityFailure.ContextWindowUnknown required ]
            | _ -> []

        let usageFailures =
            match ProviderRequirements.minimumUsageReporting requirements with
            | Some required when usageRank (ProviderCapabilities.usageReporting capabilities) < usageRank required ->
                [ ProviderCompatibilityFailure.UsageReportingInsufficient(
                      required,
                      ProviderCapabilities.usageReporting capabilities
                  ) ]
            | _ -> []

        let operationalFailures =
            [ match ProviderCapabilityObservation.health observation with
              | ProviderEndpointHealth.Healthy -> ()
              | value -> ProviderCompatibilityFailure.EndpointUnhealthy value

              if ProviderCapabilityObservation.readiness observation = ProviderEndpointReadiness.NotReady then
                  ProviderCompatibilityFailure.EndpointNotReady

              match ProviderCapabilityObservation.quota observation with
              | ProviderQuotaStatus.Available -> ()
              | value -> ProviderCompatibilityFailure.QuotaUnavailable value

              match ProviderCapabilityObservation.maintenance observation with
              | ProviderMaintenanceStatus.Active -> ()
              | ProviderMaintenanceStatus.Draining
              | ProviderMaintenanceStatus.Maintenance -> ProviderCompatibilityFailure.EndpointDraining

              match ProviderModelMetadata.deprecation (ProviderCapabilityObservation.model observation) with
              | ProviderDeprecation.Retired _ -> ProviderCompatibilityFailure.ModelRetired
              | _ -> () ]

        capabilityFailures @ contextFailures @ usageFailures @ operationalFailures
