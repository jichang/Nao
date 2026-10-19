namespace Nao.Telemetry.OpenTelemetry

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
type TelemetryContentCapture =
    | Disabled
    | Redacted

type TelemetryTenantPolicy =
    private
        { TenantId: string
          Region: string
          MaximumExportAge: TimeSpan
          ContentCapture: TelemetryContentCapture
          SamplingRatio: float }

[<RequireQualifiedAccess>]
module TelemetryTenantPolicy =
    let create tenantId region maximumExportAge contentCapture samplingRatio =
        if String.IsNullOrWhiteSpace tenantId || tenantId <> tenantId.Trim() then
            invalidArg (nameof tenantId) "Telemetry tenant identity must be canonical and non-blank."

        if String.IsNullOrWhiteSpace region || region <> region.Trim() then
            invalidArg (nameof region) "Telemetry region must be canonical and non-blank."

        if maximumExportAge <= TimeSpan.Zero then
            invalidArg (nameof maximumExportAge) "Telemetry maximum export age must be positive."

        if
            not (Double.IsFinite samplingRatio)
            || samplingRatio < 0.0
            || samplingRatio > 1.0
        then
            invalidArg (nameof samplingRatio) "Telemetry sampling ratio must be between zero and one."

        { TenantId = tenantId
          Region = region
          MaximumExportAge = maximumExportAge
          ContentCapture = contentCapture
          SamplingRatio = samplingRatio }

    let tenantId value = value.TenantId
    let region value = value.Region
    let maximumExportAge value = value.MaximumExportAge
    let contentCapture value = value.ContentCapture
    let samplingRatio value = value.SamplingRatio

type TelemetryRedactionEvidence =
    { Id: Guid
      Timestamp: DateTimeOffset
      TenantId: string
      ExecutionId: ExecutionId
      CorrelationId: CorrelationId
      Region: string
      RuleIds: string list
      RemovedFieldCount: int
      ContentCapture: TelemetryContentCapture }

type TelemetryPrivacyConfiguration =
    private
        { ResolveTenant: string -> TelemetryTenantPolicy option
          SamplingSalt: string
          RecordEvidenceAsync: TelemetryRedactionEvidence -> Task
          Clock: unit -> DateTimeOffset }

[<RequireQualifiedAccess>]
module TelemetryPrivacyConfiguration =
    let create resolveTenant samplingSalt recordEvidenceAsync clock =
        if isNull (box resolveTenant) then
            nullArg (nameof resolveTenant)

        if String.IsNullOrWhiteSpace samplingSalt || samplingSalt <> samplingSalt.Trim() then
            invalidArg (nameof samplingSalt) "Telemetry sampling salt must be canonical and non-blank."

        if isNull (box recordEvidenceAsync) then
            nullArg (nameof recordEvidenceAsync)

        if isNull (box clock) then
            nullArg (nameof clock)

        { ResolveTenant = resolveTenant
          SamplingSalt = samplingSalt
          RecordEvidenceAsync = recordEvidenceAsync
          Clock = clock }

    let local =
        create
            (fun tenantId ->
                Some(
                    TelemetryTenantPolicy.create
                        tenantId
                        "local"
                        (TimeSpan.FromHours 24.0)
                        TelemetryContentCapture.Disabled
                        1.0
                ))
            "nao-local-telemetry"
            (fun _ -> Task.CompletedTask)
            (fun () -> DateTimeOffset.UtcNow)

[<RequireQualifiedAccess>]
module TelemetryPrivacy =
    [<RequireQualifiedAccess>]
    type Decision =
        | Export of region: string * signal: ObservabilitySignal * evidence: TelemetryRedactionEvidence option
        | SampledOut
        | Expired
        | MissingPolicy

    let private patterns =
        [ "credential.bearer",
          Regex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.Compiled ||| RegexOptions.CultureInvariant)
          "credential.api_key",
          Regex(
              @"(?i)\b(?:api[_-]?key|token|password)\s*[:=]\s*[^\s,;]{4,}",
              RegexOptions.Compiled ||| RegexOptions.CultureInvariant
          )
          "personal.email",
          Regex(
              @"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
              RegexOptions.Compiled ||| RegexOptions.CultureInvariant
          )
          "personal.phone",
          Regex(
              @"(?<!\w)(?:\+?[1-9]\d{0,2}[-.\s]?)?(?:\(?\d{3}\)?[-.\s]?)\d{3}[-.\s]?\d{4}(?!\w)",
              RegexOptions.Compiled ||| RegexOptions.CultureInvariant
          ) ]

    let private sampled salt ratio correlationId =
        if ratio >= 1.0 then
            true
        elif ratio <= 0.0 then
            false
        else
            let bytes =
                $"{salt}:{CorrelationId.serialize correlationId}"
                |> Encoding.UTF8.GetBytes
                |> SHA256.HashData

            let bucket = BitConverter.ToUInt64(bytes, 0)
            float bucket / float UInt64.MaxValue < ratio

    let evaluate configuration (scope: EventScope) signal =
        match configuration.ResolveTenant scope.TenantId with
        | None -> Decision.MissingPolicy
        | Some policy when TelemetryTenantPolicy.tenantId policy <> scope.TenantId -> Decision.MissingPolicy
        | Some policy when configuration.Clock() - scope.Timestamp > TelemetryTenantPolicy.maximumExportAge policy ->
            Decision.Expired
        | Some policy when
            not (
                sampled
                    configuration.SamplingSalt
                    (TelemetryTenantPolicy.samplingRatio policy)
                    scope.Correlation.CorrelationId
            )
            ->
            Decision.SampledOut
        | Some policy ->
            let ruleIds = HashSet<string>(StringComparer.Ordinal)
            let mutable removed = 0

            let sanitize sensitivity (value: string) =
                let mutable sanitized =
                    match sensitivity, TelemetryTenantPolicy.contentCapture policy with
                    | TelemetryFieldSensitivity.ProtectedContent, TelemetryContentCapture.Disabled ->
                        removed <- removed + 1
                        ruleIds.Add "content.disabled" |> ignore
                        "[CONTENT_DISABLED]"
                    | _ -> value

                for ruleId, pattern in patterns do
                    let matches = pattern.Matches sanitized

                    if matches.Count > 0 then
                        removed <- removed + matches.Count
                        ruleIds.Add ruleId |> ignore
                        sanitized <- pattern.Replace(sanitized, "[REDACTED]")

                sanitized

            let sanitizeMap values =
                values
                |> Map.map (fun key value -> sanitize (TelemetryAttributes.sensitivity key) value)

            let sanitizeSpan (span: Span) =
                { span with
                    OperationName =
                        if
                            [ TelemetrySpan.Harness
                              TelemetrySpan.HarnessStage
                              TelemetrySpan.OrchestrationRound
                              TelemetrySpan.ProviderCall
                              TelemetrySpan.ToolCall
                              TelemetrySpan.Retrieval
                              TelemetrySpan.Reasoner
                              TelemetrySpan.Persistence ]
                            |> List.contains span.OperationName
                        then
                            span.OperationName
                        else
                            sanitize TelemetryFieldSensitivity.ProtectedContent span.OperationName
                    Attributes =
                        sanitizeMap span.Attributes
                        |> Map.add TelemetryAttribute.TenantId scope.TenantId
                    Events =
                        span.Events
                        |> List.map (fun event ->
                            { event with
                                Name = sanitize TelemetryFieldSensitivity.Operational event.Name
                                Attributes = sanitizeMap event.Attributes }) }

            let sanitized =
                match signal with
                | SpanStarted span -> SpanStarted(sanitizeSpan span)
                | SpanEnded(span, status) -> SpanEnded(sanitizeSpan span, status)
                | SpanEventAdded(span, name, attributes) ->
                    SpanEventAdded(
                        sanitizeSpan span,
                        sanitize TelemetryFieldSensitivity.Operational name,
                        sanitizeMap attributes
                    )
                | SpanAttributesSet(span, attributes) -> SpanAttributesSet(sanitizeSpan span, sanitizeMap attributes)
                | MetricRecorded record ->
                    let payload =
                        match record.Payload with
                        | MetricPayload.ToolCall(name, duration, success) ->
                            MetricPayload.ToolCall(
                                sanitize TelemetryFieldSensitivity.Identifier name,
                                duration,
                                success
                            )
                        | MetricPayload.EmbeddingCall(profileId,
                                                      batchId,
                                                      inputCount,
                                                      inputTokens,
                                                      latencyMs,
                                                      estimatedCostUsd,
                                                      providerAttempts) ->
                            MetricPayload.EmbeddingCall(
                                sanitize TelemetryFieldSensitivity.Identifier profileId,
                                sanitize TelemetryFieldSensitivity.Identifier batchId,
                                inputCount,
                                inputTokens,
                                latencyMs,
                                estimatedCostUsd,
                                providerAttempts
                            )
                        | MetricPayload.Custom point ->
                            MetricPayload.Custom
                                { point with
                                    Labels =
                                        point.Labels
                                        |> Map.map (fun key value ->
                                            sanitize (TelemetryAttributes.sensitivity key) value) }
                        | value -> value

                    MetricRecorded { record with Payload = payload }
                | AuditRecorded entry ->
                    let action =
                        match entry.Action with
                        | AuditAction.LlmCall model ->
                            AuditAction.LlmCall(sanitize TelemetryFieldSensitivity.Identifier model)
                        | AuditAction.ToolInvocation toolName ->
                            AuditAction.ToolInvocation(sanitize TelemetryFieldSensitivity.Identifier toolName)
                        | AuditAction.AgentDelegation agentName ->
                            AuditAction.AgentDelegation(sanitize TelemetryFieldSensitivity.Identifier agentName)
                        | AuditAction.MemoryWrite key ->
                            AuditAction.MemoryWrite(sanitize TelemetryFieldSensitivity.Identifier key)
                        | AuditAction.MemoryRead key ->
                            AuditAction.MemoryRead(sanitize TelemetryFieldSensitivity.Identifier key)
                        | AuditAction.ResourceAccess(resourceType, resource) ->
                            AuditAction.ResourceAccess(
                                sanitize TelemetryFieldSensitivity.Identifier resourceType,
                                sanitize TelemetryFieldSensitivity.Identifier resource
                            )
                        | AuditAction.PermissionCheck capability ->
                            AuditAction.PermissionCheck(sanitize TelemetryFieldSensitivity.Identifier capability)
                        | AuditAction.PolicyEvaluation policyId ->
                            AuditAction.PolicyEvaluation(sanitize TelemetryFieldSensitivity.Identifier policyId)
                        | AuditAction.ConstitutionEvaluation ruleId ->
                            AuditAction.ConstitutionEvaluation(sanitize TelemetryFieldSensitivity.Identifier ruleId)
                        | AuditAction.LifecycleTransition(fromState, toState) ->
                            AuditAction.LifecycleTransition(
                                sanitize TelemetryFieldSensitivity.Identifier fromState,
                                sanitize TelemetryFieldSensitivity.Identifier toState
                            )

                    AuditRecorded
                        { entry with
                            AgentId = sanitize TelemetryFieldSensitivity.Identifier entry.AgentId
                            Action = action
                            Input = entry.Input |> Option.map (sanitize TelemetryFieldSensitivity.ProtectedContent)
                            Output = entry.Output |> Option.map (sanitize TelemetryFieldSensitivity.ProtectedContent)
                            Metadata = sanitizeMap entry.Metadata }
                | ExecutionRecorded record ->
                    ExecutionRecorded
                        { record with
                            ToolName = sanitize TelemetryFieldSensitivity.Identifier record.ToolName
                            Input = sanitize TelemetryFieldSensitivity.ProtectedContent record.Input
                            Output = sanitize TelemetryFieldSensitivity.ProtectedContent record.Output
                            Metadata = sanitizeMap record.Metadata }
                | TraceSaved trace ->
                    let sanitizeTraceAction =
                        function
                        | TraceAction.LlmCall model ->
                            TraceAction.LlmCall(sanitize TelemetryFieldSensitivity.Identifier model)
                        | TraceAction.EmbeddingCall profileId ->
                            TraceAction.EmbeddingCall(sanitize TelemetryFieldSensitivity.Identifier profileId)
                        | TraceAction.ToolInvocation toolName ->
                            TraceAction.ToolInvocation(sanitize TelemetryFieldSensitivity.Identifier toolName)
                        | TraceAction.AgentDelegation agentName ->
                            TraceAction.AgentDelegation(sanitize TelemetryFieldSensitivity.Identifier agentName)
                        | TraceAction.MemoryAccess operation ->
                            TraceAction.MemoryAccess(sanitize TelemetryFieldSensitivity.Identifier operation)
                        | TraceAction.Thinking -> TraceAction.Thinking
                        | TraceAction.Validation -> TraceAction.Validation

                    TraceSaved
                        { trace with
                            AgentId = sanitize TelemetryFieldSensitivity.Identifier trace.AgentId
                            Input = sanitize TelemetryFieldSensitivity.ProtectedContent trace.Input
                            Output = trace.Output |> Option.map (sanitize TelemetryFieldSensitivity.ProtectedContent)
                            Steps =
                                trace.Steps
                                |> List.map (fun step ->
                                    { step with
                                        Action = sanitizeTraceAction step.Action
                                        Input = sanitize TelemetryFieldSensitivity.ProtectedContent step.Input
                                        Output = sanitize TelemetryFieldSensitivity.ProtectedContent step.Output })
                            Metadata = sanitizeMap trace.Metadata }
                | ExecutionReverted _ -> signal

            let evidence =
                if removed = 0 then
                    None
                else
                    Some
                        { Id = Guid.NewGuid()
                          Timestamp = configuration.Clock()
                          TenantId = scope.TenantId
                          ExecutionId = scope.Correlation.ExecutionId
                          CorrelationId = scope.Correlation.CorrelationId
                          Region = TelemetryTenantPolicy.region policy
                          RuleIds = ruleIds |> Seq.sort |> Seq.toList
                          RemovedFieldCount = removed
                          ContentCapture = TelemetryTenantPolicy.contentCapture policy }

            Decision.Export(TelemetryTenantPolicy.region policy, sanitized, evidence)

    let recordEvidenceAsync configuration evidence =
        configuration.RecordEvidenceAsync evidence
