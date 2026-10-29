namespace Nao.Platform

open System
open System.Threading
open System.Threading.Tasks

type PlatformOperationalLimits =
    private
        { MaximumConcurrentExecutions: int
          MaximumQueuedExecutions: int
          ProviderTimeout: TimeSpan }

[<RequireQualifiedAccess>]
module PlatformOperationalLimits =
    let create maximumConcurrentExecutions maximumQueuedExecutions providerTimeout =
        if maximumConcurrentExecutions <= 0 then
            invalidArg (nameof maximumConcurrentExecutions) "Maximum concurrent executions must be positive."

        if maximumQueuedExecutions < 0 then
            invalidArg (nameof maximumQueuedExecutions) "Maximum queued executions cannot be negative."

        if providerTimeout <= TimeSpan.Zero then
            invalidArg (nameof providerTimeout) "Provider timeout must be positive."

        { MaximumConcurrentExecutions = maximumConcurrentExecutions
          MaximumQueuedExecutions = maximumQueuedExecutions
          ProviderTimeout = providerTimeout }

    let maximumConcurrentExecutions value = value.MaximumConcurrentExecutions

    let maximumQueuedExecutions value = value.MaximumQueuedExecutions
    let providerTimeout value = value.ProviderTimeout

type ReloadablePlatformSettings =
    private
        { ProviderRoutingRevision: string
          PolicyRevision: string
          KnowledgeRevision: string
          EvaluationRevision: string
          OperationalLimits: PlatformOperationalLimits }

[<RequireQualifiedAccess>]
module ReloadablePlatformSettings =
    let private canonical argumentName value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg argumentName "Reloadable configuration revisions must be canonical and non-blank."

        value

    let create providerRoutingRevision policyRevision knowledgeRevision evaluationRevision operationalLimits =
        { ProviderRoutingRevision = canonical (nameof providerRoutingRevision) providerRoutingRevision
          PolicyRevision = canonical (nameof policyRevision) policyRevision
          KnowledgeRevision = canonical (nameof knowledgeRevision) knowledgeRevision
          EvaluationRevision = canonical (nameof evaluationRevision) evaluationRevision
          OperationalLimits = operationalLimits }

    let providerRoutingRevision value = value.ProviderRoutingRevision
    let policyRevision value = value.PolicyRevision
    let knowledgeRevision value = value.KnowledgeRevision
    let evaluationRevision value = value.EvaluationRevision
    let operationalLimits value = value.OperationalLimits

type ReloadableConfigurationSnapshot =
    private
        { Version: int64
          Settings: ReloadablePlatformSettings
          ActivatedAt: DateTimeOffset
          Actor: string
          Reason: string }

[<RequireQualifiedAccess>]
module ReloadableConfigurationSnapshot =
    let version value = value.Version
    let settings value = value.Settings
    let activatedAt value = value.ActivatedAt
    let actor value = value.Actor
    let reason value = value.Reason

type ReloadableConfigurationImpact =
    private
        { CurrentVersion: int64
          TargetVersion: int64
          AffectedResources: string list }

[<RequireQualifiedAccess>]
module ReloadableConfigurationImpact =
    let currentVersion value = value.CurrentVersion
    let targetVersion value = value.TargetVersion
    let affectedResources value = value.AffectedResources

type ReloadableConfigurationEvidence =
    private
        { Actor: string
          Reason: string
          PriorVersion: int64
          NewVersion: int64
          AffectedResources: string list
          ActivatedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module ReloadableConfigurationEvidence =
    let actor value = value.Actor
    let reason value = value.Reason
    let priorVersion value = value.PriorVersion
    let newVersion value = value.NewVersion
    let affectedResources value = value.AffectedResources
    let activatedAt value = value.ActivatedAt

type ReloadableConfigurationRuntime(initialSettings: ReloadablePlatformSettings, initializedAt: DateTimeOffset) =
    let mutation = new SemaphoreSlim(1, 1)
    let sync = obj ()

    let mutable current =
        { Version = 1L
          Settings = initialSettings
          ActivatedAt = initializedAt
          Actor = "startup"
          Reason = "validated startup configuration" }

    let mutable history = Map.ofList [ 1L, current ]

    let canonical argumentName value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg argumentName "Configuration mutation identity and reason must be canonical and non-blank."

    let affected currentSettings candidate =
        [ if
              ReloadablePlatformSettings.providerRoutingRevision currentSettings
              <> ReloadablePlatformSettings.providerRoutingRevision candidate
          then
              "providers"

          if
              ReloadablePlatformSettings.policyRevision currentSettings
              <> ReloadablePlatformSettings.policyRevision candidate
          then
              "policies"

          if
              ReloadablePlatformSettings.knowledgeRevision currentSettings
              <> ReloadablePlatformSettings.knowledgeRevision candidate
          then
              "knowledge"

          if
              ReloadablePlatformSettings.evaluationRevision currentSettings
              <> ReloadablePlatformSettings.evaluationRevision candidate
          then
              "evaluations"

          if
              ReloadablePlatformSettings.operationalLimits currentSettings
              <> ReloadablePlatformSettings.operationalLimits candidate
          then
              "operations" ]

    member _.Current = lock sync (fun () -> current)

    member _.Preview(candidate) =
        let snapshot = lock sync (fun () -> current)
        let affectedResources = affected snapshot.Settings candidate

        if List.isEmpty affectedResources then
            invalidArg (nameof candidate) "Reloadable configuration does not change any setting."

        { CurrentVersion = snapshot.Version
          TargetVersion = snapshot.Version + 1L
          AffectedResources = affectedResources }

    member this.ActivateAsync
        (
            expectedVersion: int64,
            actor: string,
            reason: string,
            candidate: ReloadablePlatformSettings,
            cancellationToken: CancellationToken
        ) =
        task {
            canonical (nameof actor) actor
            canonical (nameof reason) reason
            do! mutation.WaitAsync cancellationToken

            try
                let snapshot = lock sync (fun () -> current)

                if snapshot.Version <> expectedVersion then
                    invalidOp
                        $"Reloadable configuration revision conflict: expected {expectedVersion}, current {snapshot.Version}."

                let impact = this.Preview candidate
                let activatedAt = DateTimeOffset.UtcNow

                let activated =
                    { Version = ReloadableConfigurationImpact.targetVersion impact
                      Settings = candidate
                      ActivatedAt = activatedAt
                      Actor = actor
                      Reason = reason }

                lock sync (fun () ->
                    current <- activated
                    history <- Map.add activated.Version activated history)

                return
                    { Actor = actor
                      Reason = reason
                      PriorVersion = snapshot.Version
                      NewVersion = activated.Version
                      AffectedResources = ReloadableConfigurationImpact.affectedResources impact
                      ActivatedAt = activatedAt }
            finally
                mutation.Release() |> ignore
        }

    member this.RollbackAsync
        (
            expectedVersion: int64,
            targetVersion: int64,
            actor: string,
            reason: string,
            cancellationToken: CancellationToken
        ) =
        task {
            let target =
                lock sync (fun () -> Map.tryFind targetVersion history)
                |> Option.defaultWith (fun () -> invalidArg (nameof targetVersion) "Rollback target is not retained.")

            return! this.ActivateAsync(expectedVersion, actor, reason, target.Settings, cancellationToken)
        }

    interface IDisposable with
        member _.Dispose() = mutation.Dispose()
