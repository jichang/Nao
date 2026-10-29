namespace Nao.Runtime.Orleans

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
type RuntimeClusterMode =
    | DevelopmentSingleSilo
    | ProductionSingleRegion

[<RequireQualifiedAccess>]
type RuntimeMembershipProvider =
    | Localhost
    | AdoNet

[<RequireQualifiedAccess>]
type RuntimeStorageProvider =
    | Memory
    | AdoNet

type RuntimeRecoveryObjectives =
    { MaximumDataLoss: TimeSpan
      MaximumRecoveryTime: TimeSpan }

type RuntimeClusterProfile =
    private
        { Mode: RuntimeClusterMode
          Membership: RuntimeMembershipProvider
          GrainStorage: RuntimeStorageProvider
          SiloCount: int
          Region: string
          DeploymentCompatibilityId: string
          RecoveryObjectives: RuntimeRecoveryObjectives }

[<RequireQualifiedAccess>]
module RuntimeClusterProfile =
    let private required fieldName value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg fieldName "Cluster profile values must be non-blank with no surrounding whitespace."

        value

    let create mode membership grainStorage siloCount region deploymentCompatibilityId recoveryObjectives =
        if siloCount <= 0 then
            invalidArg (nameof siloCount) "A runtime cluster must contain at least one silo."

        if recoveryObjectives.MaximumDataLoss < TimeSpan.Zero then
            invalidArg (nameof recoveryObjectives) "The maximum data-loss objective cannot be negative."

        if recoveryObjectives.MaximumRecoveryTime <= TimeSpan.Zero then
            invalidArg (nameof recoveryObjectives) "The maximum recovery-time objective must be positive."

        match mode, membership, grainStorage, siloCount with
        | RuntimeClusterMode.DevelopmentSingleSilo,
          RuntimeMembershipProvider.Localhost,
          RuntimeStorageProvider.Memory,
          1 -> ()
        | RuntimeClusterMode.ProductionSingleRegion,
          RuntimeMembershipProvider.AdoNet,
          RuntimeStorageProvider.AdoNet,
          count when count >= 3 -> ()
        | RuntimeClusterMode.DevelopmentSingleSilo, _, _, _ ->
            invalidArg (nameof mode) "The development profile supports exactly one localhost silo with memory storage."
        | RuntimeClusterMode.ProductionSingleRegion, _, _, _ ->
            invalidArg
                (nameof mode)
                "The production profile requires at least three silos with ADO.NET membership and grain storage."

        { Mode = mode
          Membership = membership
          GrainStorage = grainStorage
          SiloCount = siloCount
          Region = required (nameof region) region
          DeploymentCompatibilityId = required (nameof deploymentCompatibilityId) deploymentCompatibilityId
          RecoveryObjectives = recoveryObjectives }

    let mode profile = profile.Mode
    let membership profile = profile.Membership
    let grainStorage profile = profile.GrainStorage
    let siloCount profile = profile.SiloCount
    let region profile = profile.Region
    let deploymentCompatibilityId profile = profile.DeploymentCompatibilityId
    let recoveryObjectives profile = profile.RecoveryObjectives

    let validateRollingUpgrade currentProfile candidateProfile =
        if currentProfile.Mode <> candidateProfile.Mode then
            invalidArg (nameof candidateProfile) "A rolling upgrade cannot change the cluster operating mode."

        if currentProfile.Region <> candidateProfile.Region then
            invalidArg (nameof candidateProfile) "A rolling upgrade cannot move the cluster to another region."

        if
            currentProfile.DeploymentCompatibilityId
            <> candidateProfile.DeploymentCompatibilityId
        then
            invalidArg
                (nameof candidateProfile)
                "Every silo in a rolling upgrade must use the same deployment compatibility identity."

[<RequireQualifiedAccess>]
type RuntimeResourceClass =
    | General
    | CpuIntensive
    | MemoryIntensive
    | Gpu

[<RequireQualifiedAccess>]
type RuntimeIsolationClass =
    | Shared
    | DedicatedTenant

type RuntimePlacementRequest =
    { TenantId: TenantId
      PreferredRegion: string
      PreferredZone: string option
      ResourceClass: RuntimeResourceClass
      Isolation: RuntimeIsolationClass
      DeploymentCompatibilityId: string }

type RuntimeSiloDescriptor =
    { SiloId: string
      Region: string
      Zone: string
      ResourceClasses: Set<RuntimeResourceClass>
      DedicatedTenant: TenantId option
      DeploymentCompatibilityId: string
      ActiveActivations: int
      ActivationCapacity: int
      Healthy: bool }

[<RequireQualifiedAccess>]
module RuntimePlacement =
    let private validateSilo silo =
        if
            String.IsNullOrWhiteSpace silo.SiloId
            || String.IsNullOrWhiteSpace silo.Region
            || String.IsNullOrWhiteSpace silo.Zone
            || String.IsNullOrWhiteSpace silo.DeploymentCompatibilityId
        then
            invalidArg (nameof silo) "Silo placement metadata must be complete."

        if silo.ActivationCapacity <= 0 || silo.ActiveActivations < 0 then
            invalidArg (nameof silo) "Silo activation capacity must be positive and usage cannot be negative."

    let private affinity tenantId siloId =
        $"{TenantId.value tenantId}/{siloId}"
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString

    let select request silos =
        if
            String.IsNullOrWhiteSpace request.PreferredRegion
            || String.IsNullOrWhiteSpace request.DeploymentCompatibilityId
        then
            invalidArg (nameof request) "Placement requests require region and deployment compatibility."

        silos |> Seq.iter validateSilo

        let candidates =
            silos
            |> Seq.filter (fun silo ->
                silo.Healthy
                && silo.Region = request.PreferredRegion
                && silo.DeploymentCompatibilityId = request.DeploymentCompatibilityId
                && silo.ResourceClasses.Contains request.ResourceClass
                && silo.ActiveActivations < silo.ActivationCapacity
                && match request.Isolation, silo.DedicatedTenant with
                   | RuntimeIsolationClass.Shared, None -> true
                   | RuntimeIsolationClass.DedicatedTenant, Some tenant -> tenant = request.TenantId
                   | _ -> false)
            |> Seq.toList

        let zoneCandidates =
            match request.PreferredZone with
            | Some zone ->
                let local = candidates |> List.filter (fun silo -> silo.Zone = zone)

                if local.IsEmpty then candidates else local
            | None -> candidates

        zoneCandidates
        |> List.sortBy (fun silo ->
            float silo.ActiveActivations / float silo.ActivationCapacity, affinity request.TenantId silo.SiloId)
        |> List.tryHead

[<RequireQualifiedAccess>]
type RuntimeAdmissionClass =
    | SessionActivation
    | TurnExecution

[<RequireQualifiedAccess>]
type RuntimeAdmissionFailure =
    | QueueFull
    | TenantQueueFull
    | ClusterUnavailable
    | StorageUnavailable
    | Draining
    | Cancelled

[<RequireQualifiedAccess>]
type RuntimeAvailabilityState =
    | Healthy
    | MembershipUnavailable
    | StorageUnavailable
    | Draining

type RuntimeAdmissionLimits =
    private
        { ActiveCapacity: int
          TenantActiveCapacity: int
          QueueCapacity: int
          TenantQueueCapacity: int }

[<RequireQualifiedAccess>]
module RuntimeAdmissionLimits =
    let create activeCapacity tenantActiveCapacity queueCapacity tenantQueueCapacity =
        if
            activeCapacity <= 0
            || tenantActiveCapacity <= 0
            || tenantActiveCapacity > activeCapacity
        then
            invalidArg
                (nameof activeCapacity)
                "Active capacities must be positive and tenant capacity cannot exceed total capacity."

        if
            queueCapacity <= 0
            || tenantQueueCapacity <= 0
            || tenantQueueCapacity > queueCapacity
        then
            invalidArg
                (nameof queueCapacity)
                "Queue capacities must be positive and tenant capacity cannot exceed total capacity."

        { ActiveCapacity = activeCapacity
          TenantActiveCapacity = tenantActiveCapacity
          QueueCapacity = queueCapacity
          TenantQueueCapacity = tenantQueueCapacity }

type RuntimeAdmissionLease internal (release: unit -> unit) =
    let mutable released = 0

    interface IDisposable with
        member _.Dispose() =
            if Interlocked.Exchange(&released, 1) = 0 then
                release ()

type RuntimeCapacitySnapshot =
    { WorkClass: RuntimeAdmissionClass
      Active: int
      ActiveCapacity: int
      Queued: int
      QueueCapacity: int
      Rejected: int64
      ObservedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
type RuntimeScalingRecommendation =
    | ScaleOut
    | Stable
    | ScaleIn

type internal RuntimeAdmissionRequest =
    { TenantId: string
      Completion: TaskCompletionSource<Result<RuntimeAdmissionLease, RuntimeAdmissionFailure>> }

type internal RuntimeAdmissionPool =
    { Limits: RuntimeAdmissionLimits
      Queues: Dictionary<string, Queue<RuntimeAdmissionRequest>>
      TenantOrder: ResizeArray<string>
      TenantActive: Dictionary<string, int>
      mutable Active: int
      mutable Queued: int
      mutable Rejected: int64
      mutable Cursor: int }

/// Coordinates tenant-fair admission across runtime work classes.
type RuntimeAdmissionController
    internal (pools: Map<RuntimeAdmissionClass, RuntimeAdmissionPool>, availability: RuntimeAvailabilityState) =
    member val internal SyncRoot = obj ()
    member internal _.Pools = pools
    member val internal Availability = availability with get, set

[<RequireQualifiedAccess>]
module RuntimeAdmissionController =
    let private count key (counts: Dictionary<string, int>) =
        match counts.TryGetValue key with
        | true, value -> value
        | _ -> 0

    let private createPool limits =
        { Limits = limits
          Queues = Dictionary()
          TenantOrder = ResizeArray()
          TenantActive = Dictionary()
          Active = 0
          Queued = 0
          Rejected = 0L
          Cursor = 0 }

    let create activationLimits executionLimits =
        RuntimeAdmissionController(
            Map.ofList
                [ RuntimeAdmissionClass.SessionActivation, createPool activationLimits
                  RuntimeAdmissionClass.TurnExecution, createPool executionLimits ],
            RuntimeAvailabilityState.Healthy
        )

    let setAvailability state (controller: RuntimeAdmissionController) =
        lock controller.SyncRoot (fun () -> controller.Availability <- state)

    let rec private dispatch (controller: RuntimeAdmissionController) workClass =
        let pool = controller.Pools.[workClass]

        if
            pool.Queued > 0
            && pool.TenantOrder.Count > 0
            && pool.Active < pool.Limits.ActiveCapacity
        then
            let mutable admitted = false
            let mutable inspected = 0

            while not admitted && inspected < pool.TenantOrder.Count do
                if pool.Cursor >= pool.TenantOrder.Count then
                    pool.Cursor <- 0

                let tenantId = pool.TenantOrder.[pool.Cursor]
                pool.Cursor <- (pool.Cursor + 1) % pool.TenantOrder.Count
                inspected <- inspected + 1
                let queue = pool.Queues.[tenantId]

                while queue.Count > 0 && queue.Peek().Completion.Task.IsCompleted do
                    queue.Dequeue() |> ignore
                    pool.Queued <- pool.Queued - 1

                if
                    queue.Count > 0
                    && count tenantId pool.TenantActive < pool.Limits.TenantActiveCapacity
                then
                    let request = queue.Dequeue()
                    pool.Queued <- pool.Queued - 1
                    pool.Active <- pool.Active + 1
                    pool.TenantActive.[tenantId] <- count tenantId pool.TenantActive + 1

                    let release () =
                        lock controller.SyncRoot (fun () ->
                            pool.Active <- pool.Active - 1
                            let next = count tenantId pool.TenantActive - 1

                            if next <= 0 then
                                pool.TenantActive.Remove tenantId |> ignore
                            else
                                pool.TenantActive.[tenantId] <- next

                            dispatch controller workClass)

                    let lease = new RuntimeAdmissionLease(release)

                    if request.Completion.TrySetResult(Ok lease) then
                        admitted <- true
                    else
                        (lease :> IDisposable).Dispose()

            if admitted then
                dispatch controller workClass

    let acquireAsync
        workClass
        tenantId
        (cancellationToken: CancellationToken)
        (controller: RuntimeAdmissionController)
        =
        if String.IsNullOrWhiteSpace tenantId || tenantId <> tenantId.Trim() then
            invalidArg (nameof tenantId) "Admission tenant identity must be canonical and non-blank."

        lock controller.SyncRoot (fun () ->
            let pool = controller.Pools.[workClass]
            let tenantActive = count tenantId pool.TenantActive

            match controller.Availability with
            | RuntimeAvailabilityState.MembershipUnavailable ->
                pool.Rejected <- pool.Rejected + 1L
                Task.FromResult(Error RuntimeAdmissionFailure.ClusterUnavailable)
            | RuntimeAvailabilityState.StorageUnavailable ->
                pool.Rejected <- pool.Rejected + 1L
                Task.FromResult(Error RuntimeAdmissionFailure.StorageUnavailable)
            | RuntimeAvailabilityState.Draining ->
                pool.Rejected <- pool.Rejected + 1L
                Task.FromResult(Error RuntimeAdmissionFailure.Draining)
            | RuntimeAvailabilityState.Healthy when
                pool.Active < pool.Limits.ActiveCapacity
                && tenantActive < pool.Limits.TenantActiveCapacity
                ->
                pool.Active <- pool.Active + 1
                pool.TenantActive.[tenantId] <- tenantActive + 1

                let release () =
                    lock controller.SyncRoot (fun () ->
                        pool.Active <- pool.Active - 1
                        let next = count tenantId pool.TenantActive - 1

                        if next <= 0 then
                            pool.TenantActive.Remove tenantId |> ignore
                        else
                            pool.TenantActive.[tenantId] <- next

                        dispatch controller workClass)

                Task.FromResult(Ok(new RuntimeAdmissionLease(release)))
            | RuntimeAvailabilityState.Healthy ->
                let tenantQueue =
                    match pool.Queues.TryGetValue tenantId with
                    | true, queue -> queue
                    | _ ->
                        let queue = Queue()
                        pool.Queues.Add(tenantId, queue)
                        pool.TenantOrder.Add tenantId
                        queue

                if pool.Queued >= pool.Limits.QueueCapacity then
                    pool.Rejected <- pool.Rejected + 1L
                    Task.FromResult(Error RuntimeAdmissionFailure.QueueFull)
                elif tenantQueue.Count >= pool.Limits.TenantQueueCapacity then
                    pool.Rejected <- pool.Rejected + 1L
                    Task.FromResult(Error RuntimeAdmissionFailure.TenantQueueFull)
                elif cancellationToken.IsCancellationRequested then
                    Task.FromResult(Error RuntimeAdmissionFailure.Cancelled)
                else
                    let completion =
                        TaskCompletionSource<Result<RuntimeAdmissionLease, RuntimeAdmissionFailure>>(
                            TaskCreationOptions.RunContinuationsAsynchronously
                        )

                    let request =
                        { TenantId = tenantId
                          Completion = completion }

                    tenantQueue.Enqueue request
                    pool.Queued <- pool.Queued + 1

                    cancellationToken.Register(fun () ->
                        lock controller.SyncRoot (fun () ->
                            if completion.TrySetResult(Error RuntimeAdmissionFailure.Cancelled) then
                                dispatch controller workClass))
                    |> ignore

                    completion.Task)

    let snapshot (controller: RuntimeAdmissionController) =
        lock controller.SyncRoot (fun () ->
            controller.Pools
            |> Map.toList
            |> List.map (fun (workClass, pool) ->
                { WorkClass = workClass
                  Active = pool.Active
                  ActiveCapacity = pool.Limits.ActiveCapacity
                  Queued = pool.Queued
                  QueueCapacity = pool.Limits.QueueCapacity
                  Rejected = pool.Rejected
                  ObservedAt = DateTimeOffset.UtcNow }))

    let saturationSignals (controller: RuntimeAdmissionController) =
        snapshot controller
        |> List.map (fun snapshot ->
            SaturationSignal.create
                SaturationComponent.OrleansGrains
                (match snapshot.WorkClass with
                 | RuntimeAdmissionClass.SessionActivation -> "session_activation"
                 | RuntimeAdmissionClass.TurnExecution -> "turn_execution")
                (int64 (snapshot.Active + snapshot.Queued))
                (int64 (snapshot.ActiveCapacity + snapshot.QueueCapacity))
                snapshot.Rejected
                snapshot.ObservedAt)

    let scalingRecommendation (controller: RuntimeAdmissionController) =
        let snapshots = snapshot controller

        if
            snapshots
            |> List.exists (fun snapshot ->
                snapshot.Queued > 0
                || snapshot.Rejected > 0L
                || float snapshot.Active / float snapshot.ActiveCapacity >= 0.8)
        then
            RuntimeScalingRecommendation.ScaleOut
        elif
            snapshots
            |> List.forall (fun snapshot ->
                snapshot.Queued = 0
                && float snapshot.Active / float snapshot.ActiveCapacity < 0.25)
        then
            RuntimeScalingRecommendation.ScaleIn
        else
            RuntimeScalingRecommendation.Stable

module RuntimeAdmissionFailure =
    let toPlatformFailure =
        function
        | RuntimeAdmissionFailure.QueueFull ->
            PlatformFailure.create PlatformErrorCategory.ResourceExhausted "Runtime admission queue is full." true None
        | RuntimeAdmissionFailure.TenantQueueFull ->
            PlatformFailure.create
                PlatformErrorCategory.ResourceExhausted
                "Tenant runtime admission queue is full."
                true
                None
        | RuntimeAdmissionFailure.ClusterUnavailable ->
            PlatformFailure.create
                PlatformErrorCategory.TransientDependency
                "Runtime cluster membership is unavailable."
                true
                None
        | RuntimeAdmissionFailure.StorageUnavailable ->
            PlatformFailure.create
                PlatformErrorCategory.TransientDependency
                "Runtime durable storage is unavailable."
                true
                None
        | RuntimeAdmissionFailure.Draining ->
            PlatformFailure.create
                PlatformErrorCategory.NotReady
                "Runtime silo is draining and is not accepting new work."
                true
                None
        | RuntimeAdmissionFailure.Cancelled ->
            PlatformFailure.create PlatformErrorCategory.Cancelled "Runtime admission was cancelled." false None
