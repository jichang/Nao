namespace Nao.Platform.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Platform

[<TestClass>]
type ConfigurationLifecycleTests() =
    let limits concurrent queued =
        PlatformOperationalLimits.create concurrent queued (TimeSpan.FromSeconds 30.0)

    let settings provider policy knowledge evaluation operationalLimits =
        ReloadablePlatformSettings.create provider policy knowledge evaluation operationalLimits

    let initial =
        settings "providers-1" "policies-1" "knowledge-1" "evaluations-1" (limits 8 32)

    [<TestMethod>]
    member _.InvalidOrStaleChangesCannotReplaceLastKnownGoodConfiguration() : Task =
        task {
            use runtime =
                new ReloadableConfigurationRuntime(initial, DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero))

            Assert.ThrowsExactly<ArgumentException>(fun () ->
                PlatformOperationalLimits.create 0 32 (TimeSpan.FromSeconds 30.0) |> ignore)
            |> ignore

            Assert.AreEqual(
                "providers-1",
                runtime.Current
                |> ReloadableConfigurationSnapshot.settings
                |> ReloadablePlatformSettings.providerRoutingRevision
            )

            let candidate =
                settings "providers-2" "policies-1" "knowledge-1" "evaluations-1" (limits 8 32)

            let! _ =
                Assert.ThrowsExactlyAsync<InvalidOperationException>(fun () ->
                    runtime.ActivateAsync(0L, "operator", "reload provider routing", candidate, CancellationToken.None)
                    :> Task)

            Assert.AreEqual(1L, ReloadableConfigurationSnapshot.version runtime.Current)

            Assert.AreEqual(
                "providers-1",
                runtime.Current
                |> ReloadableConfigurationSnapshot.settings
                |> ReloadablePlatformSettings.providerRoutingRevision
            )
        }

    [<TestMethod>]
    member _.ActivationAndRollbackRecordCompleteMutationEvidence() : Task =
        task {
            use runtime =
                new ReloadableConfigurationRuntime(initial, DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero))

            let candidate =
                settings "providers-2" "policies-2" "knowledge-1" "evaluations-1" (limits 16 64)

            let impact = runtime.Preview candidate

            CollectionAssert.AreEqual(
                [| "providers"; "policies"; "operations" |],
                impact |> ReloadableConfigurationImpact.affectedResources |> List.toArray
            )

            let! activated =
                runtime.ActivateAsync(1L, "operator", "increase governed capacity", candidate, CancellationToken.None)

            Assert.AreEqual("operator", ReloadableConfigurationEvidence.actor activated)
            Assert.AreEqual("increase governed capacity", ReloadableConfigurationEvidence.reason activated)
            Assert.AreEqual(1L, ReloadableConfigurationEvidence.priorVersion activated)
            Assert.AreEqual(2L, ReloadableConfigurationEvidence.newVersion activated)

            let! rollback =
                runtime.RollbackAsync(2L, 1L, "operator", "rollback failed provider rollout", CancellationToken.None)

            Assert.AreEqual(2L, ReloadableConfigurationEvidence.priorVersion rollback)
            Assert.AreEqual(3L, ReloadableConfigurationEvidence.newVersion rollback)

            Assert.AreEqual(
                "providers-1",
                runtime.Current
                |> ReloadableConfigurationSnapshot.settings
                |> ReloadablePlatformSettings.providerRoutingRevision
            )
        }
