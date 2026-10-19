namespace Nao.Agents

open System

[<RequireQualifiedAccess>]
module TelemetrySchema =
    [<Literal>]
    let CurrentVersion = 1

[<RequireQualifiedAccess>]
type TelemetryFieldSensitivity =
    | Operational
    | Identifier
    | PersonalData
    | ProtectedContent
    | Secret

[<RequireQualifiedAccess>]
module TelemetryAttribute =
    [<Literal>]
    let TenantId = "nao.tenant.id"

    [<Literal>]
    let WorkspaceId = "nao.workspace.id"

    [<Literal>]
    let SessionId = "nao.session.id"

    [<Literal>]
    let TurnId = "nao.turn.id"

    [<Literal>]
    let ExecutionId = "nao.execution.id"

    [<Literal>]
    let CorrelationId = "nao.correlation.id"

    [<Literal>]
    let CausationExecutionId = "nao.causation.execution_id"

    [<Literal>]
    let Attempt = "nao.execution.attempt"

    [<Literal>]
    let AgentId = "nao.agent.id"

    [<Literal>]
    let ToolName = "nao.tool.name"

    [<Literal>]
    let ProviderName = "nao.provider.name"

    [<Literal>]
    let ModelName = "nao.model.name"

    [<Literal>]
    let StorageSystem = "nao.storage.system"

    [<Literal>]
    let StorageOperation = "nao.storage.operation"

    [<Literal>]
    let TerminalState = "nao.terminal_state"

    [<Literal>]
    let ErrorCategory = "nao.error.category"

    [<Literal>]
    let OperationalProbe = "nao.health.probe"

    [<Literal>]
    let OperationalComponent = "nao.operational.component"

    [<Literal>]
    let OperationalComponentName = "nao.operational.component.name"

    [<Literal>]
    let HealthState = "nao.health.state"

    [<Literal>]
    let HealthReason = "nao.health.reason"

    [<Literal>]
    let CircuitState = "nao.circuit.state"

    [<Literal>]
    let PolicyCategory = "nao.policy.category"

    [<Literal>]
    let QualityMetric = "nao.quality.metric"

[<RequireQualifiedAccess>]
module TelemetrySpan =
    [<Literal>]
    let Harness = "nao.harness.execute"

    [<Literal>]
    let HarnessStage = "nao.harness.stage"

    [<Literal>]
    let OrchestrationRound = "nao.orchestration.round"

    [<Literal>]
    let ProviderCall = "nao.provider.call"

    [<Literal>]
    let ToolCall = "nao.tool.call"

    [<Literal>]
    let Retrieval = "nao.retrieval.execute"

    [<Literal>]
    let Reasoner = "nao.reasoner.execute"

    [<Literal>]
    let Persistence = "nao.persistence.operation"

[<RequireQualifiedAccess>]
type TelemetryInstrumentKind =
    | Counter
    | Histogram
    | Gauge

type TelemetryInstrument =
    private
        { Name: string
          Kind: TelemetryInstrumentKind
          Unit: string
          AllowedLabels: Set<string> }

[<RequireQualifiedAccess>]
module TelemetryInstrument =
    let private create name kind unit allowedLabels =
        { Name = name
          Kind = kind
          Unit = unit
          AllowedLabels = Set.ofList allowedLabels }

    let definitions =
        [ create
              "nao.execution.count"
              TelemetryInstrumentKind.Counter
              "{execution}"
              [ TelemetryAttribute.AgentId
                TelemetryAttribute.TerminalState
                TelemetryAttribute.ErrorCategory ]
          create
              "nao.execution.duration"
              TelemetryInstrumentKind.Histogram
              "ms"
              [ TelemetryAttribute.AgentId; TelemetryAttribute.TerminalState ]
          create
              "nao.provider.call.count"
              TelemetryInstrumentKind.Counter
              "{call}"
              [ TelemetryAttribute.ProviderName
                TelemetryAttribute.ModelName
                TelemetryAttribute.ErrorCategory ]
          create
              "nao.provider.call.duration"
              TelemetryInstrumentKind.Histogram
              "ms"
              [ TelemetryAttribute.ProviderName; TelemetryAttribute.ModelName ]
          create
              "nao.provider.token.count"
              TelemetryInstrumentKind.Counter
              "{token}"
              [ TelemetryAttribute.ProviderName
                TelemetryAttribute.ModelName
                "nao.token.type" ]
          create
              "nao.tool.call.count"
              TelemetryInstrumentKind.Counter
              "{call}"
              [ TelemetryAttribute.ToolName; TelemetryAttribute.ErrorCategory ]
          create "nao.tool.call.duration" TelemetryInstrumentKind.Histogram "ms" [ TelemetryAttribute.ToolName ]
          create
              "nao.retrieval.duration"
              TelemetryInstrumentKind.Histogram
              "ms"
              [ "nao.retrieval.strategy"; TelemetryAttribute.ErrorCategory ]
          create
              "nao.persistence.operation.duration"
              TelemetryInstrumentKind.Histogram
              "ms"
              [ TelemetryAttribute.StorageSystem
                TelemetryAttribute.StorageOperation
                TelemetryAttribute.ErrorCategory ]
          create "nao.execution.active" TelemetryInstrumentKind.Gauge "{execution}" [ TelemetryAttribute.AgentId ]
          create
              "nao.health.status"
              TelemetryInstrumentKind.Gauge
              "{status}"
              [ TelemetryAttribute.OperationalProbe
                TelemetryAttribute.OperationalComponent
                TelemetryAttribute.OperationalComponentName
                TelemetryAttribute.HealthState ]
          create
              "nao.health.check.duration"
              TelemetryInstrumentKind.Histogram
              "ms"
              [ TelemetryAttribute.OperationalProbe
                TelemetryAttribute.OperationalComponent
                TelemetryAttribute.OperationalComponentName ]
          create
              "nao.saturation.utilization"
              TelemetryInstrumentKind.Gauge
              "1"
              [ TelemetryAttribute.OperationalComponent
                TelemetryAttribute.OperationalComponentName ]
          create
              "nao.saturation.rejected"
              TelemetryInstrumentKind.Gauge
              "{work}"
              [ TelemetryAttribute.OperationalComponent
                TelemetryAttribute.OperationalComponentName ]
          create
              "nao.provider.cost"
              TelemetryInstrumentKind.Counter
              "USD"
              [ TelemetryAttribute.ProviderName; TelemetryAttribute.ModelName ]
          create
              "nao.provider.retry.count"
              TelemetryInstrumentKind.Counter
              "{retry}"
              [ TelemetryAttribute.ProviderName; TelemetryAttribute.ModelName ]
          create
              "nao.circuit.state"
              TelemetryInstrumentKind.Gauge
              "{state}"
              [ TelemetryAttribute.OperationalComponent
                TelemetryAttribute.OperationalComponentName
                TelemetryAttribute.CircuitState ]
          create
              "nao.policy.block.count"
              TelemetryInstrumentKind.Counter
              "{block}"
              [ TelemetryAttribute.PolicyCategory ]
          create
              "nao.retrieval.quality"
              TelemetryInstrumentKind.Gauge
              "1"
              [ "nao.retrieval.strategy"; TelemetryAttribute.QualityMetric ]
          create "nao.evaluation.drift" TelemetryInstrumentKind.Gauge "1" [ TelemetryAttribute.QualityMetric ] ]

    let name value = value.Name
    let kind value = value.Kind
    let unit value = value.Unit
    let allowedLabels value = value.AllowedLabels

    let tryFind name =
        definitions |> List.tryFind (fun definition -> definition.Name = name)

[<RequireQualifiedAccess>]
type TelemetryTerminalState =
    | Succeeded
    | Failed
    | Denied
    | Cancelled
    | TimedOut
    | LimitExceeded
    | Indeterminate

[<RequireQualifiedAccess>]
module TelemetryTerminalState =
    let value =
        function
        | TelemetryTerminalState.Succeeded -> "succeeded"
        | TelemetryTerminalState.Failed -> "failed"
        | TelemetryTerminalState.Denied -> "denied"
        | TelemetryTerminalState.Cancelled -> "cancelled"
        | TelemetryTerminalState.TimedOut -> "timed_out"
        | TelemetryTerminalState.LimitExceeded -> "limit_exceeded"
        | TelemetryTerminalState.Indeterminate -> "indeterminate"

[<RequireQualifiedAccess>]
type TelemetryErrorCategory =
    | None
    | Authentication
    | Authorization
    | Validation
    | Dependency
    | Timeout
    | Cancellation
    | ResourceLimit
    | Conflict
    | Internal

[<RequireQualifiedAccess>]
module TelemetryErrorCategory =
    let value =
        function
        | TelemetryErrorCategory.None -> "none"
        | TelemetryErrorCategory.Authentication -> "authentication"
        | TelemetryErrorCategory.Authorization -> "authorization"
        | TelemetryErrorCategory.Validation -> "validation"
        | TelemetryErrorCategory.Dependency -> "dependency"
        | TelemetryErrorCategory.Timeout -> "timeout"
        | TelemetryErrorCategory.Cancellation -> "cancellation"
        | TelemetryErrorCategory.ResourceLimit -> "resource_limit"
        | TelemetryErrorCategory.Conflict -> "conflict"
        | TelemetryErrorCategory.Internal -> "internal"

    let ofPlatform =
        function
        | PlatformErrorCategory.InvalidInput
        | PlatformErrorCategory.InvalidOutput -> TelemetryErrorCategory.Validation
        | PlatformErrorCategory.PermissionDenied -> TelemetryErrorCategory.Authorization
        | PlatformErrorCategory.NotReady
        | PlatformErrorCategory.TransientDependency
        | PlatformErrorCategory.PermanentDependency -> TelemetryErrorCategory.Dependency
        | PlatformErrorCategory.ResourceExhausted -> TelemetryErrorCategory.ResourceLimit
        | PlatformErrorCategory.Cancelled -> TelemetryErrorCategory.Cancellation
        | PlatformErrorCategory.InternalFailure -> TelemetryErrorCategory.Internal

[<RequireQualifiedAccess>]
module TelemetryAttributes =
    let sensitivity attributeName =
        match attributeName with
        | TelemetryAttribute.TenantId
        | TelemetryAttribute.WorkspaceId
        | TelemetryAttribute.SessionId
        | TelemetryAttribute.TurnId
        | TelemetryAttribute.ExecutionId
        | TelemetryAttribute.CorrelationId
        | TelemetryAttribute.CausationExecutionId -> TelemetryFieldSensitivity.Identifier
        | TelemetryAttribute.AgentId
        | TelemetryAttribute.ToolName
        | TelemetryAttribute.ProviderName
        | TelemetryAttribute.ModelName
        | TelemetryAttribute.StorageSystem
        | TelemetryAttribute.StorageOperation
        | TelemetryAttribute.OperationalComponentName -> TelemetryFieldSensitivity.Identifier
        | TelemetryAttribute.OperationalProbe
        | TelemetryAttribute.OperationalComponent
        | TelemetryAttribute.HealthState
        | TelemetryAttribute.HealthReason
        | TelemetryAttribute.CircuitState
        | TelemetryAttribute.PolicyCategory
        | TelemetryAttribute.QualityMetric
        | TelemetryAttribute.TerminalState
        | TelemetryAttribute.ErrorCategory
        | TelemetryAttribute.Attempt
        | "nao.token.type"
        | "nao.orchestration.round"
        | "nao.provider.message_count"
        | "input.length"
        | "output.length"
        | "result.length"
        | "latency.ms" -> TelemetryFieldSensitivity.Operational
        | _ -> TelemetryFieldSensitivity.ProtectedContent

    let execution authorization turnId agentId (correlation: CorrelationContext) =
        let identity = AuthorizationScope.identity authorization

        [ TelemetryAttribute.TenantId, TenantId.value identity.TenantId
          TelemetryAttribute.WorkspaceId, WorkspaceId.value identity.WorkspaceId
          TelemetryAttribute.TurnId, TurnId.value turnId
          TelemetryAttribute.ExecutionId, ExecutionId.serialize correlation.ExecutionId
          TelemetryAttribute.CorrelationId, CorrelationId.serialize correlation.CorrelationId
          TelemetryAttribute.Attempt, string correlation.Attempt
          TelemetryAttribute.AgentId, agentId ]
        @ (identity.SessionId
           |> Option.map (SessionId.value >> fun value -> TelemetryAttribute.SessionId, value)
           |> Option.toList)
        @ (correlation.CausationId
           |> Option.map (
               ExecutionId.serialize
               >> fun value -> TelemetryAttribute.CausationExecutionId, value
           )
           |> Option.toList)
        |> Map.ofList

    let validateMetricLabels instrument labels =
        let unexpected =
            labels
            |> Map.keys
            |> Seq.filter (fun key -> not (TelemetryInstrument.allowedLabels instrument |> Set.contains key))
            |> Seq.toList

        if not unexpected.IsEmpty then
            invalidArg
                (nameof labels)
                (sprintf "Metric labels are not part of the bounded schema: %s." (String.concat ", " unexpected))

        labels
        |> Map.iter (fun key value ->
            if String.IsNullOrWhiteSpace value || value.Length > 128 then
                invalidArg (nameof labels) (sprintf "Metric label '%s' is blank or exceeds 128 characters." key))

        labels
