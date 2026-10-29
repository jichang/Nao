namespace Nao.Platform

open System
open System.Text.Json

[<RequireQualifiedAccess>]
module PlatformConfigurationJson =
    let private requiredString (name: string) (root: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>

        if
            not (root.TryGetProperty(name, &property))
            || property.ValueKind <> JsonValueKind.String
        then
            invalidArg name $"Platform configuration requires string property '{name}'."

        property.GetString()

    let private parseEnum (name: string) values (root: JsonElement) =
        let value = requiredString name root

        values
        |> List.tryFind (fst >> (=) value)
        |> Option.map snd
        |> Option.defaultWith (fun () -> invalidArg name $"Unsupported {name} value '{value}'.")

    let parse (json: string) =
        if String.IsNullOrWhiteSpace json then
            invalidArg (nameof json) "Platform configuration JSON cannot be blank."

        use document = JsonDocument.Parse json
        let root = document.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            invalidArg (nameof json) "Platform configuration must be a JSON object."

        let allowed =
            Set.ofList
                [ "schemaVersion"
                  "profile"
                  "instanceId"
                  "identityMode"
                  "administrationMode"
                  "toolPolicy"
                  "workerIsolation"
                  "durability"
                  "clusterMembership"
                  "secretReferences" ]

        for property in root.EnumerateObject() do
            if not (Set.contains property.Name allowed) then
                invalidArg (nameof json) $"Unknown platform configuration property '{property.Name}'."

        let mutable schemaVersion = Unchecked.defaultof<JsonElement>

        if
            not (root.TryGetProperty("schemaVersion", &schemaVersion))
            || schemaVersion.ValueKind <> JsonValueKind.Number
        then
            invalidArg (nameof json) "Platform configuration requires numeric schemaVersion."

        let profile =
            parseEnum
                "profile"
                [ "local-development", DeploymentProfile.LocalDevelopment
                  "single-node-production", DeploymentProfile.SingleNodeProduction
                  "clustered-production", DeploymentProfile.ClusteredProduction
                  "isolated-worker", DeploymentProfile.IsolatedWorker ]
                root

        let identityMode =
            parseEnum
                "identityMode"
                [ "development-permissive", PlatformIdentityMode.DevelopmentPermissive
                  "authenticated", PlatformIdentityMode.Authenticated
                  "workload-identity", PlatformIdentityMode.WorkloadIdentity ]
                root

        let administrationMode =
            parseEnum
                "administrationMode"
                [ "disabled", PlatformAdministrationMode.Disabled
                  "authenticated", PlatformAdministrationMode.Authenticated
                  "anonymous", PlatformAdministrationMode.Anonymous ]
                root

        let toolPolicy =
            parseEnum
                "toolPolicy"
                [ "restricted", PlatformToolPolicy.Restricted
                  "unrestricted", PlatformToolPolicy.Unrestricted ]
                root

        let workerIsolation =
            parseEnum
                "workerIsolation"
                [ "in-process", PlatformWorkerIsolation.InProcess
                  "process", PlatformWorkerIsolation.Process
                  "container", PlatformWorkerIsolation.Container ]
                root

        let durability =
            parseEnum
                "durability"
                [ "ephemeral", PlatformDurability.Ephemeral
                  "persistent", PlatformDurability.Persistent ]
                root

        let clusterMembership =
            match requiredString "clusterMembership" root with
            | "standalone" -> PlatformClusterMembership.Standalone
            | value when value.StartsWith("configured:", StringComparison.Ordinal) ->
                value["configured:".Length ..] |> PlatformClusterMembership.Configured
            | value -> invalidArg (nameof json) $"Unsupported clusterMembership value '{value}'."

        let mutable secretReferences = Unchecked.defaultof<JsonElement>

        let secrets =
            if root.TryGetProperty("secretReferences", &secretReferences) then
                if secretReferences.ValueKind <> JsonValueKind.Array then
                    invalidArg (nameof json) "secretReferences must be an array."

                secretReferences.EnumerateArray()
                |> Seq.map (fun secret ->
                    if secret.ValueKind <> JsonValueKind.Object then
                        invalidArg (nameof json) "Secret references must be JSON objects."

                    let allowedSecret = Set.ofList [ "name"; "provider"; "key" ]

                    for property in secret.EnumerateObject() do
                        if not (Set.contains property.Name allowedSecret) then
                            invalidArg (nameof json) $"Unknown secret reference property '{property.Name}'."

                    PlatformSecretReference.create
                        (requiredString "name" secret)
                        (requiredString "provider" secret)
                        (requiredString "key" secret))
                |> Seq.toList
            else
                []

        PlatformConfiguration.create
            (schemaVersion.GetInt32())
            profile
            (requiredString "instanceId" root)
            identityMode
            administrationMode
            toolPolicy
            workerIsolation
            durability
            clusterMembership
            secrets
