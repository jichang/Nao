namespace Nao.Agents

open System
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type OperationalProbe =
    | Liveness
    | Readiness
    | Startup
    | Dependency

[<RequireQualifiedAccess>]
type OperationalComponent =
    | Host
    | Provider
    | Storage
    | Queue
    | KnowledgeIndex
    | Reasoner

[<RequireQualifiedAccess>]
type OperationalHealthState =
    | Healthy
    | Degraded
    | Unhealthy

[<RequireQualifiedAccess>]
type OperationalHealthReason =
    | None
    | StartupPending
    | DependencyUnavailable
    | CapacityExhausted
    | Misconfigured
    | TimedOut

type OperationalHealth =
    private
        { Probe: OperationalProbe
          Component: OperationalComponent
          Name: string
          State: OperationalHealthState
          Reason: OperationalHealthReason
          ObservedAt: DateTimeOffset
          Latency: TimeSpan }

[<RequireQualifiedAccess>]
module OperationalHealth =
    let create probe componentKind name state reason observedAt latency =
        if String.IsNullOrWhiteSpace name || name <> name.Trim() then
            invalidArg (nameof name) "Operational component name must be canonical and non-blank."

        if latency < TimeSpan.Zero then
            invalidArg (nameof latency) "Operational health latency cannot be negative."

        match state, reason with
        | OperationalHealthState.Healthy, OperationalHealthReason.None -> ()
        | OperationalHealthState.Healthy, _ ->
            invalidArg (nameof reason) "Healthy operational checks cannot carry a failure reason."
        | _, OperationalHealthReason.None ->
            invalidArg (nameof reason) "Non-healthy operational checks require a bounded reason."
        | _ -> ()

        { Probe = probe
          Component = componentKind
          Name = name
          State = state
          Reason = reason
          ObservedAt = observedAt
          Latency = latency }

    let probe value = value.Probe
    let componentKind value = value.Component
    let name value = value.Name
    let state value = value.State
    let reason value = value.Reason
    let observedAt value = value.ObservedAt
    let latency value = value.Latency

[<RequireQualifiedAccess>]
module OperationalProbe =
    let value =
        function
        | OperationalProbe.Liveness -> "liveness"
        | OperationalProbe.Readiness -> "readiness"
        | OperationalProbe.Startup -> "startup"
        | OperationalProbe.Dependency -> "dependency"

[<RequireQualifiedAccess>]
module OperationalComponent =
    let value =
        function
        | OperationalComponent.Host -> "host"
        | OperationalComponent.Provider -> "provider"
        | OperationalComponent.Storage -> "storage"
        | OperationalComponent.Queue -> "queue"
        | OperationalComponent.KnowledgeIndex -> "knowledge_index"
        | OperationalComponent.Reasoner -> "reasoner"

[<RequireQualifiedAccess>]
module OperationalHealthState =
    let value =
        function
        | OperationalHealthState.Healthy -> "healthy"
        | OperationalHealthState.Degraded -> "degraded"
        | OperationalHealthState.Unhealthy -> "unhealthy"

[<RequireQualifiedAccess>]
module OperationalHealthReason =
    let value =
        function
        | OperationalHealthReason.None -> "none"
        | OperationalHealthReason.StartupPending -> "startup_pending"
        | OperationalHealthReason.DependencyUnavailable -> "dependency_unavailable"
        | OperationalHealthReason.CapacityExhausted -> "capacity_exhausted"
        | OperationalHealthReason.Misconfigured -> "misconfigured"
        | OperationalHealthReason.TimedOut -> "timed_out"

type OperationalHealthCheck =
    private
        { Probe: OperationalProbe
          Component: OperationalComponent
          Name: string
          CheckAsync: CancellationToken -> Task<OperationalHealth> }

[<RequireQualifiedAccess>]
module OperationalHealthCheck =
    let create probe componentKind name checkAsync =
        if String.IsNullOrWhiteSpace name || name <> name.Trim() then
            invalidArg (nameof name) "Operational health-check name must be canonical and non-blank."

        if isNull (box checkAsync) then
            nullArg (nameof checkAsync)

        { Probe = probe
          Component = componentKind
          Name = name
          CheckAsync = checkAsync }

    let probe value = value.Probe
    let componentKind value = value.Component
    let name value = value.Name

    let checkAsync cancellationToken value = value.CheckAsync cancellationToken

type OperationalHealthPlan =
    private
        { Liveness: OperationalHealthCheck
          Startup: OperationalHealthCheck
          Readiness: OperationalHealthCheck
          Dependencies: OperationalHealthCheck list }

[<RequireQualifiedAccess>]
module OperationalHealthPlan =
    let create liveness startup readiness dependencies =
        let requireHostProbe argumentName expected (check: OperationalHealthCheck) =
            if
                OperationalHealthCheck.probe check <> expected
                || OperationalHealthCheck.componentKind check <> OperationalComponent.Host
            then
                invalidArg argumentName $"The {argumentName} check must be a host {expected} probe."

        requireHostProbe (nameof liveness) OperationalProbe.Liveness liveness
        requireHostProbe (nameof startup) OperationalProbe.Startup startup
        requireHostProbe (nameof readiness) OperationalProbe.Readiness readiness

        dependencies
        |> Seq.iter (fun check ->
            if OperationalHealthCheck.probe check <> OperationalProbe.Dependency then
                invalidArg (nameof dependencies) "Every dependency check must use the dependency probe."

            if OperationalHealthCheck.componentKind check = OperationalComponent.Host then
                invalidArg (nameof dependencies) "Host checks cannot be registered as dependency checks.")

        let duplicateDependency =
            dependencies
            |> Seq.countBy (fun check -> OperationalHealthCheck.componentKind check, OperationalHealthCheck.name check)
            |> Seq.tryFind (fun (_, count) -> count > 1)

        if duplicateDependency.IsSome then
            invalidArg (nameof dependencies) "A health plan cannot contain duplicate dependency checks."

        { Liveness = liveness
          Startup = startup
          Readiness = readiness
          Dependencies = List.ofSeq dependencies }

    let checkAsync cancellationToken plan =
        task {
            let checks = [ plan.Liveness; plan.Startup; plan.Readiness ] @ plan.Dependencies

            let! observations =
                checks
                |> List.map (OperationalHealthCheck.checkAsync cancellationToken)
                |> Task.WhenAll

            return List.ofArray observations
        }

[<RequireQualifiedAccess>]
type SaturationComponent =
    | ProviderLimit
    | ExecutionWorkers
    | OrleansGrains
    | StoragePool
    | TelemetryQueue

type SaturationSignal =
    private
        { Component: SaturationComponent
          Name: string
          Current: int64
          Capacity: int64
          Rejected: int64
          ObservedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module SaturationSignal =
    let create componentKind name current capacity rejected observedAt =
        if String.IsNullOrWhiteSpace name || name <> name.Trim() then
            invalidArg (nameof name) "Saturation component name must be canonical and non-blank."

        if current < 0L then
            invalidArg (nameof current) "Saturation usage cannot be negative."

        if capacity <= 0L then
            invalidArg (nameof capacity) "Saturation capacity must be positive."

        if rejected < 0L then
            invalidArg (nameof rejected) "Saturation rejection count cannot be negative."

        { Component = componentKind
          Name = name
          Current = current
          Capacity = capacity
          Rejected = rejected
          ObservedAt = observedAt }

    let componentKind value = value.Component
    let name value = value.Name
    let current value = value.Current
    let capacity value = value.Capacity
    let rejected value = value.Rejected
    let observedAt value = value.ObservedAt

    let utilization value =
        float value.Current / float value.Capacity

[<RequireQualifiedAccess>]
module SaturationComponent =
    let value =
        function
        | SaturationComponent.ProviderLimit -> "provider_limit"
        | SaturationComponent.ExecutionWorkers -> "execution_workers"
        | SaturationComponent.OrleansGrains -> "orleans_grains"
        | SaturationComponent.StoragePool -> "storage_pool"
        | SaturationComponent.TelemetryQueue -> "telemetry_queue"

[<RequireQualifiedAccess>]
module OperationalTelemetry =
    let private point name value labels : MetricPoint =
        { Name = name
          Value = value
          Labels = labels }

    let health correlation (health: OperationalHealth) =
        let baseLabels =
            Map
                [ TelemetryAttribute.OperationalProbe, OperationalProbe.value (OperationalHealth.probe health)
                  TelemetryAttribute.OperationalComponent,
                  OperationalComponent.value (OperationalHealth.componentKind health)
                  TelemetryAttribute.OperationalComponentName, OperationalHealth.name health ]

        [ OperationalHealthState.Healthy
          OperationalHealthState.Degraded
          OperationalHealthState.Unhealthy ]
        |> List.map (fun state ->
            point
                "nao.health.status"
                (if state = OperationalHealth.state health then 1.0 else 0.0)
                (baseLabels
                 |> Map.add TelemetryAttribute.HealthState (OperationalHealthState.value state)))
        |> fun statuses ->
            statuses
            @ [ point "nao.health.check.duration" (OperationalHealth.latency health).TotalMilliseconds baseLabels ]
        |> List.map (MetricRecord.custom correlation (OperationalHealth.observedAt health))

    let saturation correlation (signal: SaturationSignal) =
        let labels =
            Map
                [ TelemetryAttribute.OperationalComponent,
                  SaturationComponent.value (SaturationSignal.componentKind signal)
                  TelemetryAttribute.OperationalComponentName, SaturationSignal.name signal ]

        [ point "nao.saturation.utilization" (SaturationSignal.utilization signal) labels
          point "nao.saturation.rejected" (float (SaturationSignal.rejected signal)) labels ]
        |> List.map (MetricRecord.custom correlation (SaturationSignal.observedAt signal))

type DiagnosticSnapshot =
    private
        { Id: Guid
          CapturedAt: DateTimeOffset
          Health: OperationalHealth list
          Saturation: SaturationSignal list }

[<RequireQualifiedAccess>]
module DiagnosticSnapshot =
    let create capturedAt health saturation =
        let duplicateHealth =
            health
            |> Seq.countBy (fun value ->
                OperationalHealth.probe value, OperationalHealth.componentKind value, OperationalHealth.name value)
            |> Seq.tryFind (fun (_, count) -> count > 1)

        if duplicateHealth.IsSome then
            invalidArg (nameof health) "A diagnostic snapshot cannot contain duplicate health observations."

        let duplicateSaturation =
            saturation
            |> Seq.countBy (fun value -> SaturationSignal.componentKind value, SaturationSignal.name value)
            |> Seq.tryFind (fun (_, count) -> count > 1)

        if duplicateSaturation.IsSome then
            invalidArg (nameof saturation) "A diagnostic snapshot cannot contain duplicate saturation signals."

        { Id = Guid.NewGuid()
          CapturedAt = capturedAt
          Health = List.ofSeq health
          Saturation = List.ofSeq saturation }

    let id value = value.Id
    let capturedAt value = value.CapturedAt
    let health value = value.Health
    let saturation value = value.Saturation
