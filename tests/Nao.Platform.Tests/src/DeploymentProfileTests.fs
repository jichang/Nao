namespace Nao.Platform.Tests

open System
open System.IO
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Platform

[<TestClass>]
type DeploymentProfileTests() =
    let profileDirectory =
        Path.Combine(Nao.Agents.NaoTestFixtures.repositoryRoot (), "deploy", "profiles", "v1")

    let parse name =
        Path.Combine(profileDirectory, name)
        |> File.ReadAllText
        |> PlatformConfigurationJson.parse

    let production profile identity administration tools isolation durability membership =
        PlatformConfiguration.create
            PlatformConfiguration.CurrentSchemaVersion
            profile
            "test"
            identity
            administration
            tools
            isolation
            durability
            membership
            []

    [<TestMethod>]
    member _.AllVersionedProfileExamplesParseAndValidate() =
        let expected =
            [ "local-development.json", DeploymentProfile.LocalDevelopment
              "single-node-production.json", DeploymentProfile.SingleNodeProduction
              "clustered-production.json", DeploymentProfile.ClusteredProduction
              "isolated-worker.json", DeploymentProfile.IsolatedWorker ]

        for fileName, profile in expected do
            let configuration = parse fileName
            let validated, effective = PlatformStartup.validate configuration
            Assert.AreEqual(profile, PlatformConfiguration.profile validated)
            Assert.AreEqual(string profile, (EffectivePlatformConfiguration.values effective)["profile"])

    [<TestMethod>]
    member _.ProductionProfilesRejectEveryPermissiveSettingBeforeStartup() =
        let baseline identity administration tools isolation durability =
            production
                DeploymentProfile.SingleNodeProduction
                identity
                administration
                tools
                isolation
                durability
                PlatformClusterMembership.Standalone

        let unsafe =
            [ baseline
                  PlatformIdentityMode.DevelopmentPermissive
                  PlatformAdministrationMode.Authenticated
                  PlatformToolPolicy.Restricted
                  PlatformWorkerIsolation.Process
                  PlatformDurability.Persistent
              baseline
                  PlatformIdentityMode.Authenticated
                  PlatformAdministrationMode.Anonymous
                  PlatformToolPolicy.Restricted
                  PlatformWorkerIsolation.Process
                  PlatformDurability.Persistent
              baseline
                  PlatformIdentityMode.Authenticated
                  PlatformAdministrationMode.Authenticated
                  PlatformToolPolicy.Unrestricted
                  PlatformWorkerIsolation.Process
                  PlatformDurability.Persistent
              baseline
                  PlatformIdentityMode.Authenticated
                  PlatformAdministrationMode.Authenticated
                  PlatformToolPolicy.Restricted
                  PlatformWorkerIsolation.InProcess
                  PlatformDurability.Persistent
              baseline
                  PlatformIdentityMode.Authenticated
                  PlatformAdministrationMode.Authenticated
                  PlatformToolPolicy.Restricted
                  PlatformWorkerIsolation.Process
                  PlatformDurability.Ephemeral ]

        for configuration in unsafe do
            Assert.ThrowsExactly<InvalidOperationException>(fun () -> PlatformStartup.validate configuration |> ignore)
            |> ignore

    [<TestMethod>]
    member _.ClusterAndWorkerProfilesEnforceTheirTopology() =
        let missingMembership =
            production
                DeploymentProfile.ClusteredProduction
                PlatformIdentityMode.WorkloadIdentity
                PlatformAdministrationMode.Authenticated
                PlatformToolPolicy.Restricted
                PlatformWorkerIsolation.Container
                PlatformDurability.Persistent
                PlatformClusterMembership.Standalone

        Assert.ThrowsExactly<InvalidOperationException>(fun () -> PlatformStartup.validate missingMembership |> ignore)
        |> ignore

        let workerAdministration =
            production
                DeploymentProfile.IsolatedWorker
                PlatformIdentityMode.WorkloadIdentity
                PlatformAdministrationMode.Authenticated
                PlatformToolPolicy.Restricted
                PlatformWorkerIsolation.Container
                PlatformDurability.Persistent
                PlatformClusterMembership.Standalone

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            PlatformStartup.validate workerAdministration |> ignore)
        |> ignore

    [<TestMethod>]
    member _.EffectiveDiagnosticsNeverContainSecretKeysOrValues() =
        let configuration = parse "single-node-production.json"
        let _, effective = PlatformStartup.validate configuration
        let diagnostics = EffectivePlatformConfiguration.secretReferences effective

        Assert.AreEqual("environment:<redacted>", diagnostics["database"])

        Assert.IsFalse(
            diagnostics
            |> Map.toSeq
            |> Seq.exists (snd >> _.Contains("NAO_DATABASE_CONNECTION"))
        )

    [<TestMethod>]
    member _.JsonParserRejectsUnknownFieldsAndUnsupportedVersions() =
        let local =
            File.ReadAllText(Path.Combine(profileDirectory, "local-development.json"))

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            local.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2")
            |> PlatformConfigurationJson.parse
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            local.Replace("\"instanceId\":", "\"unexpected\": true, \"instanceId\":")
            |> PlatformConfigurationJson.parse
            |> ignore)
        |> ignore

        let production =
            File.ReadAllText(Path.Combine(profileDirectory, "single-node-production.json"))

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            production.Replace("\"key\":", "\"unexpected\": true, \"key\":")
            |> PlatformConfigurationJson.parse
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.DuplicateSecretNamesAreRejected() =
        let secret =
            PlatformSecretReference.create "database" "environment" "NAO_DATABASE_CONNECTION"

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            PlatformConfiguration.create
                PlatformConfiguration.CurrentSchemaVersion
                DeploymentProfile.SingleNodeProduction
                "duplicate-secret"
                PlatformIdentityMode.Authenticated
                PlatformAdministrationMode.Authenticated
                PlatformToolPolicy.Restricted
                PlatformWorkerIsolation.Process
                PlatformDurability.Persistent
                PlatformClusterMembership.Standalone
                [ secret; secret ]
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.DevelopmentDefaultsAreExplicitAndNeverProductionDefaults() =
        let local = PlatformConfiguration.localDevelopment "developer"

        Assert.AreEqual(DeploymentProfile.LocalDevelopment, PlatformConfiguration.profile local)

        Assert.AreEqual(PlatformIdentityMode.DevelopmentPermissive, PlatformConfiguration.identityMode local)

        Assert.AreEqual(PlatformAdministrationMode.Disabled, PlatformConfiguration.administrationMode local)

        Assert.AreEqual(PlatformToolPolicy.Restricted, PlatformConfiguration.toolPolicy local)

        Assert.AreEqual(PlatformWorkerIsolation.InProcess, PlatformConfiguration.workerIsolation local)

        Assert.AreEqual(PlatformDurability.Ephemeral, PlatformConfiguration.durability local)

        PlatformStartup.validate local |> ignore

        let unsafeProduction =
            production
                DeploymentProfile.SingleNodeProduction
                (PlatformConfiguration.identityMode local)
                (PlatformConfiguration.administrationMode local)
                (PlatformConfiguration.toolPolicy local)
                (PlatformConfiguration.workerIsolation local)
                (PlatformConfiguration.durability local)
                (PlatformConfiguration.clusterMembership local)

        Assert.ThrowsExactly<InvalidOperationException>(fun () -> PlatformStartup.validate unsafeProduction |> ignore)
        |> ignore
