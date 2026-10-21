namespace Nao.Runtime.Orleans.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Runtime.Orleans

[<TestClass; TestCategory("Runtime"); TestCategory("ClusterReliability")>]
type ClusterReliabilityTests() =
    let objectives =
        { MaximumDataLoss = TimeSpan.Zero
          MaximumRecoveryTime = TimeSpan.FromMinutes 15.0 }

    let limits active tenantActive queued tenantQueued =
        RuntimeAdmissionLimits.create active tenantActive queued tenantQueued

    let acquire workClass tenant cancellation controller =
        RuntimeAdmissionController.acquireAsync workClass tenant cancellation controller

    let lease (result: Result<RuntimeAdmissionLease, RuntimeAdmissionFailure>) =
        match result with
        | Ok value -> value
        | Error failure -> raise (InvalidOperationException $"Expected admission lease, received {failure}.")

    [<TestMethod>]
    member _.SupportedProfilesRejectUnsafeProductionCombinationsAndIncompatibleRollingUpgrades() =
        let current =
            RuntimeClusterProfile.create
                RuntimeClusterMode.ProductionSingleRegion
                RuntimeMembershipProvider.AdoNet
                RuntimeStorageProvider.AdoNet
                3
                "region-a"
                "runtime-v1"
                objectives

        let candidate =
            RuntimeClusterProfile.create
                RuntimeClusterMode.ProductionSingleRegion
                RuntimeMembershipProvider.AdoNet
                RuntimeStorageProvider.AdoNet
                4
                "region-a"
                "runtime-v1"
                objectives

        RuntimeClusterProfile.validateRollingUpgrade current candidate

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            RuntimeClusterProfile.create
                RuntimeClusterMode.ProductionSingleRegion
                RuntimeMembershipProvider.Localhost
                RuntimeStorageProvider.Memory
                1
                "region-a"
                "runtime-v1"
                objectives
            |> ignore)
        |> ignore

        let incompatible =
            RuntimeClusterProfile.create
                RuntimeClusterMode.ProductionSingleRegion
                RuntimeMembershipProvider.AdoNet
                RuntimeStorageProvider.AdoNet
                3
                "region-a"
                "runtime-v2"
                objectives

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            RuntimeClusterProfile.validateRollingUpgrade current incompatible)
        |> ignore

    [<TestMethod>]
    member _.PlacementHonorsCompatibilityLocalityResourcesIsolationAndTenantAffinity() =
        let tenant = TenantId.parse "tenant"

        let request =
            { TenantId = tenant
              PreferredRegion = "region-a"
              PreferredZone = Some "zone-a"
              ResourceClass = RuntimeResourceClass.Gpu
              Isolation = RuntimeIsolationClass.DedicatedTenant
              DeploymentCompatibilityId = "runtime-v1" }

        let silo id region zone resource dedicated compatibility active =
            { SiloId = id
              Region = region
              Zone = zone
              ResourceClasses = Set.singleton resource
              DedicatedTenant = dedicated
              DeploymentCompatibilityId = compatibility
              ActiveActivations = active
              ActivationCapacity = 10
              Healthy = true }

        let eligible =
            silo "eligible" "region-a" "zone-a" RuntimeResourceClass.Gpu (Some tenant) "runtime-v1" 1

        let candidates =
            [ eligible
              silo
                  "wrong-tenant"
                  "region-a"
                  "zone-a"
                  RuntimeResourceClass.Gpu
                  (Some(TenantId.parse "other"))
                  "runtime-v1"
                  0
              silo "wrong-region" "region-b" "zone-a" RuntimeResourceClass.Gpu (Some tenant) "runtime-v1" 0
              silo "wrong-version" "region-a" "zone-a" RuntimeResourceClass.Gpu (Some tenant) "runtime-v2" 0
              silo "wrong-resource" "region-a" "zone-a" RuntimeResourceClass.General (Some tenant) "runtime-v1" 0 ]

        Assert.AreEqual(Some eligible, RuntimePlacement.select request candidates)
        Assert.AreEqual(Some eligible, RuntimePlacement.select request candidates)

    [<TestMethod>]
    member _.AdmissionQueuesAreBoundedAndReleaseDispatchesWaitingWork() : Task =
        task {
            let controller = RuntimeAdmissionController.create (limits 1 1 1 1) (limits 1 1 1 1)

            let! first = acquire RuntimeAdmissionClass.TurnExecution "tenant" CancellationToken.None controller

            let waiting =
                acquire RuntimeAdmissionClass.TurnExecution "tenant" CancellationToken.None controller

            Assert.IsFalse(waiting.IsCompleted)

            let! rejected = acquire RuntimeAdmissionClass.TurnExecution "tenant" CancellationToken.None controller

            Assert.AreEqual(Error RuntimeAdmissionFailure.QueueFull, rejected)

            (lease first :> IDisposable).Dispose()
            let! second = waiting
            (lease second :> IDisposable).Dispose()

            let snapshot =
                RuntimeAdmissionController.snapshot controller
                |> List.find (fun value -> value.WorkClass = RuntimeAdmissionClass.TurnExecution)

            Assert.AreEqual(0, snapshot.Active)
            Assert.AreEqual(0, snapshot.Queued)
            Assert.AreEqual(1L, snapshot.Rejected)
        }

    [<TestMethod>]
    member _.AdmissionDispatchesTenantsRoundRobinAndCancellationFreesQueueCapacity() : Task =
        task {
            let controller = RuntimeAdmissionController.create (limits 1 1 4 2) (limits 1 1 4 2)

            let! active = acquire RuntimeAdmissionClass.TurnExecution "tenant-a" CancellationToken.None controller

            let queuedA =
                acquire RuntimeAdmissionClass.TurnExecution "tenant-a" CancellationToken.None controller

            let queuedB =
                acquire RuntimeAdmissionClass.TurnExecution "tenant-b" CancellationToken.None controller

            use cancellation = new CancellationTokenSource()

            let cancelled =
                acquire RuntimeAdmissionClass.TurnExecution "tenant-b" cancellation.Token controller

            cancellation.Cancel()
            let! cancelledResult = cancelled
            Assert.AreEqual(Error RuntimeAdmissionFailure.Cancelled, cancelledResult)

            (lease active :> IDisposable).Dispose()
            let! admittedA = queuedA
            Assert.IsFalse(queuedB.IsCompleted)
            (lease admittedA :> IDisposable).Dispose()
            let! admittedB = queuedB
            (lease admittedB :> IDisposable).Dispose()

            let snapshot =
                RuntimeAdmissionController.snapshot controller
                |> List.find (fun value -> value.WorkClass = RuntimeAdmissionClass.TurnExecution)

            Assert.AreEqual(0, snapshot.Queued)
            Assert.AreEqual(0, snapshot.Active)
        }

    [<TestMethod>]
    member _.CapacitySignalsDriveExplicitScalingRecommendations() =
        let controller = RuntimeAdmissionController.create (limits 2 2 2 2) (limits 1 1 1 1)

        Assert.AreEqual(
            RuntimeScalingRecommendation.ScaleIn,
            RuntimeAdmissionController.scalingRecommendation controller
        )

        let admitted =
            acquire RuntimeAdmissionClass.TurnExecution "tenant" CancellationToken.None controller
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(
            RuntimeScalingRecommendation.ScaleOut,
            RuntimeAdmissionController.scalingRecommendation controller
        )

        let signals = RuntimeAdmissionController.saturationSignals controller
        Assert.AreEqual(2, signals.Length)

        let execution =
            signals
            |> List.find (fun signal -> SaturationSignal.name signal = "turn_execution")

        Assert.AreEqual(1L, SaturationSignal.current execution)
        Assert.AreEqual(2L, SaturationSignal.capacity execution)
        (lease admitted :> IDisposable).Dispose()

    [<TestMethod>]
    member _.AdmissionFailuresUseStructuredResourceExhaustion() =
        let failure =
            RuntimeAdmissionFailure.QueueFull |> RuntimeAdmissionFailure.toPlatformFailure

        Assert.AreEqual(PlatformErrorCategory.ResourceExhausted, failure.Category)
        Assert.IsTrue(failure.Retryable)

    [<TestMethod>]
    member _.MembershipStorageAndDrainDegradationRejectNewWorkExplicitly() : Task =
        task {
            let controller = RuntimeAdmissionController.create (limits 1 1 1 1) (limits 1 1 1 1)

            let cases =
                [ RuntimeAvailabilityState.MembershipUnavailable, RuntimeAdmissionFailure.ClusterUnavailable
                  RuntimeAvailabilityState.StorageUnavailable, RuntimeAdmissionFailure.StorageUnavailable
                  RuntimeAvailabilityState.Draining, RuntimeAdmissionFailure.Draining ]

            for state, expected in cases do
                RuntimeAdmissionController.setAvailability state controller

                let! result = acquire RuntimeAdmissionClass.TurnExecution "tenant" CancellationToken.None controller

                Assert.AreEqual(Error expected, result)

            RuntimeAdmissionController.setAvailability RuntimeAvailabilityState.Healthy controller

            let! recovered = acquire RuntimeAdmissionClass.TurnExecution "tenant" CancellationToken.None controller

            (lease recovered :> IDisposable).Dispose()

            let storageFailure =
                RuntimeAdmissionFailure.StorageUnavailable
                |> RuntimeAdmissionFailure.toPlatformFailure

            Assert.AreEqual(PlatformErrorCategory.TransientDependency, storageFailure.Category)
            Assert.IsTrue(storageFailure.Retryable)
        }
