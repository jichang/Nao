namespace Nao.Platform

open System

[<RequireQualifiedAccess>]
type DeploymentProfile =
    | LocalDevelopment
    | SingleNodeProduction
    | ClusteredProduction
    | IsolatedWorker

[<RequireQualifiedAccess>]
type PlatformIdentityMode =
    | DevelopmentPermissive
    | Authenticated
    | WorkloadIdentity

[<RequireQualifiedAccess>]
type PlatformAdministrationMode =
    | Disabled
    | Authenticated
    | Anonymous

[<RequireQualifiedAccess>]
type PlatformToolPolicy =
    | Restricted
    | Unrestricted

[<RequireQualifiedAccess>]
type PlatformWorkerIsolation =
    | InProcess
    | Process
    | Container

[<RequireQualifiedAccess>]
type PlatformDurability =
    | Ephemeral
    | Persistent

[<RequireQualifiedAccess>]
type PlatformClusterMembership =
    | Standalone
    | Configured of provider: string

type PlatformSecretReference =
    private
        { Name: string
          Provider: string
          Key: string }

[<RequireQualifiedAccess>]
module PlatformSecretReference =
    let create name provider key =
        for fieldName, value in [ nameof name, name; nameof provider, provider; nameof key, key ] do
            if String.IsNullOrWhiteSpace value || value <> value.Trim() then
                invalidArg fieldName "Secret references must be canonical and non-blank."

        { Name = name
          Provider = provider
          Key = key }

    let name value = value.Name
    let provider value = value.Provider
    let key value = value.Key

type PlatformConfiguration =
    private
        { SchemaVersion: int
          Profile: DeploymentProfile
          InstanceId: string
          IdentityMode: PlatformIdentityMode
          AdministrationMode: PlatformAdministrationMode
          ToolPolicy: PlatformToolPolicy
          WorkerIsolation: PlatformWorkerIsolation
          Durability: PlatformDurability
          ClusterMembership: PlatformClusterMembership
          SecretReferences: PlatformSecretReference list }

[<RequireQualifiedAccess>]
module PlatformConfiguration =
    [<Literal>]
    let CurrentSchemaVersion = 1

    let create
        schemaVersion
        profile
        instanceId
        identityMode
        administrationMode
        toolPolicy
        workerIsolation
        durability
        clusterMembership
        secretReferences
        =
        if schemaVersion <> CurrentSchemaVersion then
            invalidArg (nameof schemaVersion) "Unsupported platform configuration schema version."

        if String.IsNullOrWhiteSpace instanceId || instanceId <> instanceId.Trim() then
            invalidArg (nameof instanceId) "Platform instance identity must be canonical and non-blank."

        let names = secretReferences |> List.map PlatformSecretReference.name

        if names.Length <> (names |> List.distinct |> List.length) then
            invalidArg (nameof secretReferences) "Platform secret reference names must be unique."

        match clusterMembership with
        | PlatformClusterMembership.Configured provider when
            String.IsNullOrWhiteSpace provider || provider <> provider.Trim()
            ->
            invalidArg (nameof clusterMembership) "Cluster membership provider must be canonical and non-blank."
        | _ -> ()

        { SchemaVersion = schemaVersion
          Profile = profile
          InstanceId = instanceId
          IdentityMode = identityMode
          AdministrationMode = administrationMode
          ToolPolicy = toolPolicy
          WorkerIsolation = workerIsolation
          Durability = durability
          ClusterMembership = clusterMembership
          SecretReferences = secretReferences }

    let schemaVersion value = value.SchemaVersion
    let profile value = value.Profile
    let instanceId value = value.InstanceId
    let identityMode value = value.IdentityMode
    let administrationMode value = value.AdministrationMode
    let toolPolicy value = value.ToolPolicy
    let workerIsolation value = value.WorkerIsolation
    let durability value = value.Durability
    let clusterMembership value = value.ClusterMembership
    let secretReferences value = value.SecretReferences

    let localDevelopment instanceId =
        create
            CurrentSchemaVersion
            DeploymentProfile.LocalDevelopment
            instanceId
            PlatformIdentityMode.DevelopmentPermissive
            PlatformAdministrationMode.Disabled
            PlatformToolPolicy.Restricted
            PlatformWorkerIsolation.InProcess
            PlatformDurability.Ephemeral
            PlatformClusterMembership.Standalone
            []

type PlatformConfigurationFailure =
    { Field: string
      Code: string
      Detail: string }

[<RequireQualifiedAccess>]
module PlatformConfigurationValidator =
    let private failure field code detail =
        { Field = field
          Code = code
          Detail = detail }

    let validate configuration =
        let production =
            match PlatformConfiguration.profile configuration with
            | DeploymentProfile.SingleNodeProduction
            | DeploymentProfile.ClusteredProduction
            | DeploymentProfile.IsolatedWorker -> true
            | DeploymentProfile.LocalDevelopment -> false

        [ if production then
              match PlatformConfiguration.identityMode configuration with
              | PlatformIdentityMode.DevelopmentPermissive ->
                  failure "identityMode" "permissive_identity" "Production profiles require authenticated identity."
              | PlatformIdentityMode.Authenticated
              | PlatformIdentityMode.WorkloadIdentity -> ()

          if
              production
              && PlatformConfiguration.administrationMode configuration = PlatformAdministrationMode.Anonymous
          then
              failure
                  "administrationMode"
                  "anonymous_administration"
                  "Production profiles cannot expose anonymous administration."

          if
              production
              && PlatformConfiguration.toolPolicy configuration = PlatformToolPolicy.Unrestricted
          then
              failure "toolPolicy" "unrestricted_tools" "Production profiles require restricted tools."

          if
              production
              && PlatformConfiguration.workerIsolation configuration = PlatformWorkerIsolation.InProcess
          then
              failure
                  "workerIsolation"
                  "in_process_untrusted_execution"
                  "Production profiles cannot execute untrusted work in process."

          if
              production
              && PlatformConfiguration.durability configuration = PlatformDurability.Ephemeral
          then
              failure "durability" "ephemeral_state" "Production profiles require persistent authoritative state."

          match PlatformConfiguration.profile configuration, PlatformConfiguration.clusterMembership configuration with
          | DeploymentProfile.ClusteredProduction, PlatformClusterMembership.Standalone ->
              failure
                  "clusterMembership"
                  "missing_cluster_membership"
                  "Clustered production requires configured membership."
          | (DeploymentProfile.LocalDevelopment | DeploymentProfile.SingleNodeProduction | DeploymentProfile.IsolatedWorker),
            PlatformClusterMembership.Configured _ ->
              failure
                  "clusterMembership"
                  "unexpected_cluster_membership"
                  "Only clustered production can configure cluster membership."
          | _ -> ()

          match PlatformConfiguration.profile configuration, PlatformConfiguration.administrationMode configuration with
          | DeploymentProfile.IsolatedWorker, mode when mode <> PlatformAdministrationMode.Disabled ->
              failure "administrationMode" "worker_administration" "Isolated workers cannot expose administration."
          | _ -> () ]

    let requireValid configuration =
        match validate configuration with
        | [] -> configuration
        | failures ->
            let detail =
                failures
                |> List.map (fun value -> $"{value.Field}:{value.Code}")
                |> String.concat ", "

            invalidOp $"Unsafe platform configuration: {detail}."

type EffectivePlatformConfiguration =
    private
        { Values: Map<string, string>
          SecretReferences: Map<string, string> }

[<RequireQualifiedAccess>]
module EffectivePlatformConfiguration =
    let create configuration =
        PlatformConfigurationValidator.requireValid configuration |> ignore

        let cluster =
            match PlatformConfiguration.clusterMembership configuration with
            | PlatformClusterMembership.Standalone -> "standalone"
            | PlatformClusterMembership.Configured provider -> $"configured:{provider}"

        { Values =
            Map.ofList
                [ "schemaVersion", string (PlatformConfiguration.schemaVersion configuration)
                  "profile", string (PlatformConfiguration.profile configuration)
                  "instanceId", PlatformConfiguration.instanceId configuration
                  "identityMode", string (PlatformConfiguration.identityMode configuration)
                  "administrationMode", string (PlatformConfiguration.administrationMode configuration)
                  "toolPolicy", string (PlatformConfiguration.toolPolicy configuration)
                  "workerIsolation", string (PlatformConfiguration.workerIsolation configuration)
                  "durability", string (PlatformConfiguration.durability configuration)
                  "clusterMembership", cluster ]
          SecretReferences =
            PlatformConfiguration.secretReferences configuration
            |> List.map (fun value ->
                PlatformSecretReference.name value, $"{PlatformSecretReference.provider value}:<redacted>")
            |> Map.ofList }

    let values value = value.Values
    let secretReferences value = value.SecretReferences

[<RequireQualifiedAccess>]
module PlatformStartup =
    let validate configuration =
        let configuration = PlatformConfigurationValidator.requireValid configuration
        configuration, EffectivePlatformConfiguration.create configuration
