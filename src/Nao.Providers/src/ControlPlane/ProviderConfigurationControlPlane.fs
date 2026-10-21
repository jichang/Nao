namespace Nao.Providers

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Nao.Agents

type ProviderModelAccessRule =
    private
        { AllowedModelIds: Set<string> option
          DeniedModelIds: Set<string> }

[<RequireQualifiedAccess>]
module ProviderModelAccessRule =
    let create allowedModelIds deniedModelIds =
        let validate values =
            values
            |> Set.iter (fun modelId ->
                if String.IsNullOrWhiteSpace modelId || modelId <> modelId.Trim() then
                    invalidArg (nameof modelId) "Model policy identities must be canonical and non-blank.")

        allowedModelIds |> Option.iter validate
        validate deniedModelIds

        match allowedModelIds with
        | Some allowed when Set.isEmpty allowed ->
            invalidArg (nameof allowedModelIds) "An explicit model allowlist cannot be empty."
        | Some allowed when not (Set.intersect allowed deniedModelIds |> Set.isEmpty) ->
            invalidArg (nameof deniedModelIds) "A model cannot be both allowed and denied."
        | _ -> ()

        { AllowedModelIds = allowedModelIds
          DeniedModelIds = deniedModelIds }

type ProviderModelPolicy =
    private
        { Rules: Map<TenantId * WorkspaceId, ProviderModelAccessRule> }

[<RequireQualifiedAccess>]
module ProviderModelPolicy =
    let create rules =
        if Map.isEmpty rules then
            invalidArg (nameof rules) "Provider model policy requires at least one tenant/workspace rule."

        { Rules = rules }

    let permits authorization modelId policy =
        let key =
            AuthorizationScope.tenantId authorization, AuthorizationScope.workspaceId authorization

        match Map.tryFind key policy.Rules with
        | None -> false
        | Some rule ->
            not (Set.contains modelId rule.DeniedModelIds)
            && (rule.AllowedModelIds |> Option.forall (Set.contains modelId))

type ProviderRuntimeConfiguration =
    private
        { Version: string
          ProviderType: ProviderType
          Endpoint: ProviderEndpointConfiguration
          ModelPolicy: ProviderModelPolicy }

[<RequireQualifiedAccess>]
module ProviderRuntimeConfiguration =
    let create version providerType endpoint modelPolicy =
        if String.IsNullOrWhiteSpace version || version <> version.Trim() then
            invalidArg (nameof version) "Provider configuration version must be canonical and non-blank."

        ProviderType.validate providerType

        { Version = version
          ProviderType = providerType
          Endpoint = endpoint
          ModelPolicy = modelPolicy }

    let version value = value.Version
    let modelId value = ProviderType.modelId value.ProviderType

    let activateEndpointAsync secretProvider authorization configuration cancellationToken =
        ProviderFactory.activateEndpointAsync
            secretProvider
            authorization
            configuration.ProviderType
            configuration.Endpoint
            cancellationToken

type ProviderOperationReference =
    { ExecutionId: ExecutionId
      TenantId: TenantId
      WorkspaceId: WorkspaceId
      SessionId: SessionId option }

[<RequireQualifiedAccess>]
type ProviderConfigurationAuditKind =
    | Activated
    | Reloaded
    | RolledBack
    | Rejected

[<RequireQualifiedAccess>]
type ProviderConfigurationRejection =
    | ModelIdentityMismatch
    | EndpointUnhealthy
    | EndpointNotReady
    | QuotaUnavailable
    | EndpointUnavailableForMaintenance

type ProviderConfigurationAudit =
    { Id: Guid
      Timestamp: DateTimeOffset
      Kind: ProviderConfigurationAuditKind
      PreviousVersion: string option
      CandidateVersion: string
      ModelId: string
      Rejection: ProviderConfigurationRejection option
      AffectedOperations: ProviderOperationReference list }

[<RequireQualifiedAccess>]
type ProviderConfigurationFailure =
    | ValidationFailed of ProviderConfigurationRejection
    | NoPreviousConfiguration

[<RequireQualifiedAccess>]
type ManagedProviderInvocationFailure =
    | ModelDenied
    | EndpointRejected of ProviderInvocationFailure

type private ProviderConfigurationState =
    { Configuration: ProviderRuntimeConfiguration
      Endpoint: ProviderEndpoint
      CredentialReference: SecretReference option
      Operations: Dictionary<Guid, ProviderOperationReference>
      mutable DisposeWhenIdle: bool }

type ProviderConfigurationControlPlane =
    private
        { ActivateAsync:
            ProviderRuntimeConfiguration -> CancellationToken -> Task<ProviderEndpoint * SecretReference option>
          RecordAuditAsync: ProviderConfigurationAudit -> Task
          Clock: unit -> DateTimeOffset
          ReloadGate: SemaphoreSlim
          OperationGate: SemaphoreSlim
          mutable Current: ProviderConfigurationState
          mutable Previous: ProviderConfigurationState option }

[<RequireQualifiedAccess>]
module ProviderConfigurationControlPlane =
    let private rejection configuration observation =
        if
            ProviderCapabilityObservation.model observation |> ProviderModelMetadata.modelId
            <> ProviderRuntimeConfiguration.modelId configuration
        then
            Some ProviderConfigurationRejection.ModelIdentityMismatch
        elif
            ProviderCapabilityObservation.health observation
            <> ProviderEndpointHealth.Healthy
        then
            Some ProviderConfigurationRejection.EndpointUnhealthy
        elif
            ProviderCapabilityObservation.readiness observation
            <> ProviderEndpointReadiness.Ready
        then
            Some ProviderConfigurationRejection.EndpointNotReady
        elif ProviderCapabilityObservation.quota observation <> ProviderQuotaStatus.Available then
            Some ProviderConfigurationRejection.QuotaUnavailable
        elif
            ProviderCapabilityObservation.maintenance observation
            <> ProviderMaintenanceStatus.Active
        then
            Some ProviderConfigurationRejection.EndpointUnavailableForMaintenance
        else
            None

    let private activateAndValidateAsync activateAsync configuration cancellationToken =
        task {
            let! endpoint, credential = activateAsync configuration cancellationToken

            try
                let! observation = ProviderEndpoint.refreshAsync cancellationToken endpoint

                match rejection configuration observation with
                | Some failure ->
                    ProviderEndpoint.dispose endpoint
                    return Error failure
                | None ->
                    return
                        Ok
                            { Configuration = configuration
                              Endpoint = endpoint
                              CredentialReference = credential
                              Operations = Dictionary()
                              DisposeWhenIdle = false }
            with error ->
                ProviderEndpoint.dispose endpoint
                return raise error
        }

    let private operationReference authorization executionId =
        { ExecutionId = executionId
          TenantId = AuthorizationScope.tenantId authorization
          WorkspaceId = AuthorizationScope.workspaceId authorization
          SessionId = AuthorizationScope.sessionId authorization }

    let private audit clock kind previous candidate rejection affected =
        { Id = Guid.NewGuid()
          Timestamp = clock ()
          Kind = kind
          PreviousVersion = previous
          CandidateVersion = ProviderRuntimeConfiguration.version candidate
          ModelId = ProviderRuntimeConfiguration.modelId candidate
          Rejection = rejection
          AffectedOperations = affected |> List.sortBy (_.ExecutionId >> ExecutionId.serialize) }

    let createAsync
        (activateAsync:
            ProviderRuntimeConfiguration -> CancellationToken -> Task<ProviderEndpoint * SecretReference option>)
        (recordAuditAsync: ProviderConfigurationAudit -> Task)
        (clock: unit -> DateTimeOffset)
        initialConfiguration
        cancellationToken
        =
        task {
            let! activated = activateAndValidateAsync activateAsync initialConfiguration cancellationToken

            match activated with
            | Error failure ->
                do!
                    recordAuditAsync (
                        audit clock ProviderConfigurationAuditKind.Rejected None initialConfiguration (Some failure) []
                    )

                return Error(ProviderConfigurationFailure.ValidationFailed failure)
            | Ok state ->
                do!
                    recordAuditAsync (
                        audit clock ProviderConfigurationAuditKind.Activated None initialConfiguration None []
                    )

                return
                    Ok
                        { ActivateAsync = activateAsync
                          RecordAuditAsync = recordAuditAsync
                          Clock = clock
                          ReloadGate = new SemaphoreSlim(1, 1)
                          OperationGate = new SemaphoreSlim(1, 1)
                          Current = state
                          Previous = None }
        }

    let private disposeIfIdle state =
        if state.DisposeWhenIdle && state.Operations.Count = 0 then
            ProviderEndpoint.dispose state.Endpoint

    let reloadAsync configuration (cancellationToken: CancellationToken) controlPlane =
        task {
            do! controlPlane.ReloadGate.WaitAsync cancellationToken

            try
                let! candidate = activateAndValidateAsync controlPlane.ActivateAsync configuration cancellationToken

                match candidate with
                | Error failure ->
                    do!
                        controlPlane.RecordAuditAsync(
                            audit
                                controlPlane.Clock
                                ProviderConfigurationAuditKind.Rejected
                                (Some(ProviderRuntimeConfiguration.version controlPlane.Current.Configuration))
                                configuration
                                (Some failure)
                                []
                        )

                    return Error(ProviderConfigurationFailure.ValidationFailed failure)
                | Ok candidate ->
                    do! controlPlane.OperationGate.WaitAsync cancellationToken

                    try
                        let affected = controlPlane.Current.Operations.Values |> Seq.toList

                        do!
                            controlPlane.RecordAuditAsync(
                                audit
                                    controlPlane.Clock
                                    ProviderConfigurationAuditKind.Reloaded
                                    (Some(ProviderRuntimeConfiguration.version controlPlane.Current.Configuration))
                                    configuration
                                    None
                                    affected
                            )

                        controlPlane.Previous
                        |> Option.iter (fun previous ->
                            previous.DisposeWhenIdle <- true
                            disposeIfIdle previous)

                        controlPlane.Previous <- Some controlPlane.Current
                        controlPlane.Current <- candidate
                        return Ok()
                    finally
                        controlPlane.OperationGate.Release() |> ignore
            finally
                controlPlane.ReloadGate.Release() |> ignore
        }

    let rollbackAsync (cancellationToken: CancellationToken) controlPlane =
        task {
            do! controlPlane.ReloadGate.WaitAsync cancellationToken

            try
                match controlPlane.Previous with
                | None -> return Error ProviderConfigurationFailure.NoPreviousConfiguration
                | Some previous ->
                    let! observation = ProviderEndpoint.refreshAsync cancellationToken previous.Endpoint

                    match rejection previous.Configuration observation with
                    | Some failure -> return Error(ProviderConfigurationFailure.ValidationFailed failure)
                    | None ->
                        do! controlPlane.OperationGate.WaitAsync cancellationToken

                        try
                            let affected = controlPlane.Current.Operations.Values |> Seq.toList

                            do!
                                controlPlane.RecordAuditAsync(
                                    audit
                                        controlPlane.Clock
                                        ProviderConfigurationAuditKind.RolledBack
                                        (Some(ProviderRuntimeConfiguration.version controlPlane.Current.Configuration))
                                        previous.Configuration
                                        None
                                        affected
                                )

                            let current = controlPlane.Current
                            controlPlane.Current <- previous
                            controlPlane.Previous <- Some current
                            return Ok()
                        finally
                            controlPlane.OperationGate.Release() |> ignore
            finally
                controlPlane.ReloadGate.Release() |> ignore
        }

    let completeAsync
        requirements
        authorization
        executionId
        correlation
        conversation
        options
        (cancellationToken: CancellationToken)
        controlPlane
        =
        task {
            do! controlPlane.OperationGate.WaitAsync cancellationToken
            let mutable operationId = None
            let mutable selectedState = None
            let mutable denied = false

            try
                let state = controlPlane.Current
                let modelId = ProviderRuntimeConfiguration.modelId state.Configuration

                if not (ProviderModelPolicy.permits authorization modelId state.Configuration.ModelPolicy) then
                    denied <- true
                else
                    let id = Guid.NewGuid()
                    state.Operations.Add(id, operationReference authorization executionId)
                    operationId <- Some id
                    selectedState <- Some state
            finally
                controlPlane.OperationGate.Release() |> ignore

            if denied then
                return Error ManagedProviderInvocationFailure.ModelDenied
            else
                let state = selectedState.Value
                let mutable invocationResult = None
                let mutable invocationError: exn option = None

                try
                    let! result =
                        ProviderEndpoint.completeAsync
                            requirements
                            correlation
                            conversation
                            options
                            cancellationToken
                            state.Endpoint

                    invocationResult <-
                        Some(result |> Result.mapError ManagedProviderInvocationFailure.EndpointRejected)
                with error ->
                    invocationError <- Some error

                do! controlPlane.OperationGate.WaitAsync CancellationToken.None

                try
                    state.Operations.Remove(operationId.Value) |> ignore
                    disposeIfIdle state
                finally
                    controlPlane.OperationGate.Release() |> ignore

                match invocationError with
                | Some error -> return raise error
                | None -> return invocationResult.Value
        }

    let currentVersion controlPlane =
        ProviderRuntimeConfiguration.version controlPlane.Current.Configuration

    let dispose controlPlane =
        let activeOperations =
            controlPlane.Current.Operations.Count
            + (controlPlane.Previous
               |> Option.map (_.Operations.Count)
               |> Option.defaultValue 0)

        if activeOperations > 0 then
            invalidOp "Provider configuration control plane cannot be disposed while operations are in flight."

        controlPlane.Current.DisposeWhenIdle <- true
        disposeIfIdle controlPlane.Current

        controlPlane.Previous
        |> Option.iter (fun previous ->
            previous.DisposeWhenIdle <- true
            disposeIfIdle previous)

        controlPlane.ReloadGate.Dispose()
        controlPlane.OperationGate.Dispose()
