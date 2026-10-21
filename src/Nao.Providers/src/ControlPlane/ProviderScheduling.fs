namespace Nao.Providers

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

type ProviderSchedulingKey =
    private
        { TenantId: string
          EndpointId: string
          ModelId: string }

[<RequireQualifiedAccess>]
module ProviderSchedulingKey =
    let create tenantId endpointId modelId =
        if String.IsNullOrWhiteSpace tenantId || tenantId <> tenantId.Trim() then
            invalidArg (nameof tenantId) "Scheduling tenant identity must be canonical and non-blank."

        if String.IsNullOrWhiteSpace endpointId || endpointId <> endpointId.Trim() then
            invalidArg (nameof endpointId) "Scheduling endpoint identity must be canonical and non-blank."

        if String.IsNullOrWhiteSpace modelId || modelId <> modelId.Trim() then
            invalidArg (nameof modelId) "Scheduling model identity must be canonical and non-blank."

        { TenantId = tenantId
          EndpointId = endpointId
          ModelId = modelId }

[<RequireQualifiedAccess>]
type ProviderRateLimitState =
    | Available
    | Limited
    | Exhausted

[<RequireQualifiedAccess>]
type ProviderEndpointAdmissionState =
    | Active
    | Draining

[<RequireQualifiedAccess>]
type ProviderAdmissionFailure =
    | QueueFull
    | TenantQueueFull
    | UnknownEndpoint
    | UnknownModel
    | UnknownTenant

type ProviderSchedulerConfiguration =
    private
        { EndpointConcurrency: Map<string, int>
          ModelConcurrency: Map<string * string, int>
          TenantConcurrency: Map<string, int>
          QueueCapacity: int
          TenantQueueCapacity: int }

[<RequireQualifiedAccess>]
module ProviderSchedulerConfiguration =
    let create endpointConcurrency modelConcurrency tenantConcurrency queueCapacity tenantQueueCapacity =
        let validateLimits argumentName limits =
            limits
            |> Map.iter (fun key value ->
                if value <= 0 then
                    invalidArg argumentName $"Concurrency limit for '{key}' must be positive.")

        validateLimits (nameof endpointConcurrency) endpointConcurrency
        validateLimits (nameof modelConcurrency) modelConcurrency
        validateLimits (nameof tenantConcurrency) tenantConcurrency

        if queueCapacity <= 0 then
            invalidArg (nameof queueCapacity) "Provider scheduling queue capacity must be positive."

        if tenantQueueCapacity <= 0 || tenantQueueCapacity > queueCapacity then
            invalidArg
                (nameof tenantQueueCapacity)
                "Tenant queue capacity must be positive and cannot exceed total queue capacity."

        { EndpointConcurrency = endpointConcurrency
          ModelConcurrency = modelConcurrency
          TenantConcurrency = tenantConcurrency
          QueueCapacity = queueCapacity
          TenantQueueCapacity = tenantQueueCapacity }

type ProviderSchedulingLease internal (release: unit -> unit) =
    let mutable released = 0

    interface IDisposable with
        member _.Dispose() =
            if Interlocked.Exchange(&released, 1) = 0 then
                release ()

type private AdmissionRequest =
    { Key: ProviderSchedulingKey
      Completion: TaskCompletionSource<ProviderSchedulingLease> }

type ProviderScheduler =
    private
        { Configuration: ProviderSchedulerConfiguration
          SyncRoot: obj
          Queues: Dictionary<string, Queue<AdmissionRequest>>
          TenantOrder: ResizeArray<string>
          EndpointActive: Dictionary<string, int>
          ModelActive: Dictionary<string * string, int>
          TenantActive: Dictionary<string, int>
          RateLimits: Dictionary<string, ProviderRateLimitState>
          EndpointStates: Dictionary<string, ProviderEndpointAdmissionState>
          mutable QueueCount: int
          mutable Cursor: int
          mutable LastAdmittedTenant: string option }

[<RequireQualifiedAccess>]
module ProviderScheduler =
    let private count key (values: Dictionary<_, int>) =
        match values.TryGetValue key with
        | true, value -> value
        | _ -> 0

    let private increment key (values: Dictionary<_, int>) = values.[key] <- count key values + 1

    let private decrement key (values: Dictionary<_, int>) =
        let next = count key values - 1

        if next <= 0 then
            values.Remove key |> ignore
        else
            values.[key] <- next

    let create configuration =
        { Configuration = configuration
          SyncRoot = obj ()
          Queues = Dictionary()
          TenantOrder = ResizeArray()
          EndpointActive = Dictionary()
          ModelActive = Dictionary()
          TenantActive = Dictionary()
          RateLimits = Dictionary()
          EndpointStates = Dictionary()
          QueueCount = 0
          Cursor = 0
          LastAdmittedTenant = None }

    let private canAdmit scheduler request =
        let key = request.Key
        let modelKey = key.EndpointId, key.ModelId

        let available =
            match scheduler.RateLimits.TryGetValue key.EndpointId with
            | true, ProviderRateLimitState.Available
            | false, _ -> true
            | _ -> false

        let active =
            match scheduler.EndpointStates.TryGetValue key.EndpointId with
            | true, ProviderEndpointAdmissionState.Draining -> false
            | _ -> true

        available
        && active
        && count key.EndpointId scheduler.EndpointActive < scheduler.Configuration.EndpointConcurrency.[key.EndpointId]
        && count modelKey scheduler.ModelActive < scheduler.Configuration.ModelConcurrency.[modelKey]
        && count key.TenantId scheduler.TenantActive < scheduler.Configuration.TenantConcurrency.[key.TenantId]

    let rec private dispatch scheduler =
        if scheduler.QueueCount > 0 && scheduler.TenantOrder.Count > 0 then
            scheduler.Cursor <-
                scheduler.LastAdmittedTenant
                |> Option.bind (fun tenantId -> scheduler.TenantOrder |> Seq.tryFindIndex ((=) tenantId))
                |> Option.map (fun index -> (index + 1) % scheduler.TenantOrder.Count)
                |> Option.defaultValue 0

            let mutable admitted = false
            let mutable inspected = 0

            while not admitted && inspected < scheduler.TenantOrder.Count do
                if scheduler.Cursor >= scheduler.TenantOrder.Count then
                    scheduler.Cursor <- 0

                let tenantId = scheduler.TenantOrder.[scheduler.Cursor]
                scheduler.Cursor <- (scheduler.Cursor + 1) % scheduler.TenantOrder.Count
                inspected <- inspected + 1
                let queue = scheduler.Queues.[tenantId]

                while queue.Count > 0 && queue.Peek().Completion.Task.IsCompleted do
                    queue.Dequeue() |> ignore
                    scheduler.QueueCount <- scheduler.QueueCount - 1

                if queue.Count > 0 && canAdmit scheduler (queue.Peek()) then
                    let request = queue.Dequeue()
                    scheduler.QueueCount <- scheduler.QueueCount - 1
                    increment request.Key.EndpointId scheduler.EndpointActive
                    increment (request.Key.EndpointId, request.Key.ModelId) scheduler.ModelActive
                    increment request.Key.TenantId scheduler.TenantActive

                    let release () =
                        lock scheduler.SyncRoot (fun () ->
                            decrement request.Key.EndpointId scheduler.EndpointActive
                            decrement (request.Key.EndpointId, request.Key.ModelId) scheduler.ModelActive
                            decrement request.Key.TenantId scheduler.TenantActive
                            dispatch scheduler)

                    if request.Completion.TrySetResult(new ProviderSchedulingLease(release)) then
                        admitted <- true
                        scheduler.LastAdmittedTenant <- Some request.Key.TenantId
                    else
                        release ()

            if admitted then
                dispatch scheduler

    let private validateKey scheduler key =
        if not (Map.containsKey key.EndpointId scheduler.Configuration.EndpointConcurrency) then
            Some ProviderAdmissionFailure.UnknownEndpoint
        elif not (Map.containsKey (key.EndpointId, key.ModelId) scheduler.Configuration.ModelConcurrency) then
            Some ProviderAdmissionFailure.UnknownModel
        elif not (Map.containsKey key.TenantId scheduler.Configuration.TenantConcurrency) then
            Some ProviderAdmissionFailure.UnknownTenant
        else
            None

    let acquireAsync key (cancellationToken: CancellationToken) scheduler =
        lock scheduler.SyncRoot (fun () ->
            match validateKey scheduler key with
            | Some failure -> Task.FromResult(Error failure)
            | None ->
                let tenantQueue =
                    match scheduler.Queues.TryGetValue key.TenantId with
                    | true, queue -> queue
                    | _ ->
                        let queue = Queue()
                        scheduler.Queues.Add(key.TenantId, queue)
                        scheduler.TenantOrder.Add key.TenantId
                        queue

                if scheduler.QueueCount >= scheduler.Configuration.QueueCapacity then
                    Task.FromResult(Error ProviderAdmissionFailure.QueueFull)
                elif tenantQueue.Count >= scheduler.Configuration.TenantQueueCapacity then
                    Task.FromResult(Error ProviderAdmissionFailure.TenantQueueFull)
                else
                    let completion =
                        TaskCompletionSource<ProviderSchedulingLease>(
                            TaskCreationOptions.RunContinuationsAsynchronously
                        )

                    tenantQueue.Enqueue { Key = key; Completion = completion }

                    scheduler.QueueCount <- scheduler.QueueCount + 1

                    if cancellationToken.CanBeCanceled then
                        cancellationToken.Register(fun () ->
                            lock scheduler.SyncRoot (fun () ->
                                completion.TrySetCanceled cancellationToken |> ignore
                                dispatch scheduler))
                        |> ignore

                    dispatch scheduler

                    task {
                        let! lease = completion.Task
                        return Ok lease
                    })

    let setRateLimit endpointId state scheduler =
        lock scheduler.SyncRoot (fun () ->
            if not (Map.containsKey endpointId scheduler.Configuration.EndpointConcurrency) then
                invalidArg (nameof endpointId) $"Unknown provider endpoint '{endpointId}'."

            scheduler.RateLimits.[endpointId] <- state

            if state = ProviderRateLimitState.Available then
                dispatch scheduler)

    let setEndpointState endpointId state scheduler =
        lock scheduler.SyncRoot (fun () ->
            if not (Map.containsKey endpointId scheduler.Configuration.EndpointConcurrency) then
                invalidArg (nameof endpointId) $"Unknown provider endpoint '{endpointId}'."

            scheduler.EndpointStates.[endpointId] <- state

            if state = ProviderEndpointAdmissionState.Active then
                dispatch scheduler)
