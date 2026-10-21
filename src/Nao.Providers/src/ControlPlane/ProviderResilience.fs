namespace Nao.Providers

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Nao.Agents

type ProviderFailureDomain =
    private
        { EndpointId: string
          DomainId: string }

[<RequireQualifiedAccess>]
module ProviderFailureDomain =
    let create endpointId domainId =
        if String.IsNullOrWhiteSpace endpointId || endpointId <> endpointId.Trim() then
            invalidArg (nameof endpointId) "Failure-domain endpoint identity must be canonical and non-blank."

        if String.IsNullOrWhiteSpace domainId || domainId <> domainId.Trim() then
            invalidArg (nameof domainId) "Provider failure-domain identity must be canonical and non-blank."

        { EndpointId = endpointId
          DomainId = domainId }

[<RequireQualifiedAccess>]
type ProviderCircuitState =
    | Closed
    | Open of until: DateTimeOffset
    | HalfOpen

type ProviderCircuitConfiguration =
    private
        { FailureThreshold: int
          OpenDuration: TimeSpan }

[<RequireQualifiedAccess>]
module ProviderCircuitConfiguration =
    let create failureThreshold openDuration =
        if failureThreshold <= 0 then
            invalidArg (nameof failureThreshold) "Provider circuit failure threshold must be positive."

        if openDuration <= TimeSpan.Zero then
            invalidArg (nameof openDuration) "Provider circuit open duration must be positive."

        { FailureThreshold = failureThreshold
          OpenDuration = openDuration }

type private ProviderCircuitEntry =
    { mutable State: ProviderCircuitState
      mutable ConsecutiveFailures: int }

type ProviderCircuitRegistry =
    private
        { Configuration: ProviderCircuitConfiguration
          Clock: unit -> DateTimeOffset
          SyncRoot: obj
          Entries: Dictionary<ProviderFailureDomain, ProviderCircuitEntry> }

[<RequireQualifiedAccess>]
module ProviderCircuitRegistry =
    let create configuration clock =
        { Configuration = configuration
          Clock = clock
          SyncRoot = obj ()
          Entries = Dictionary() }

    let private entry domain registry =
        match registry.Entries.TryGetValue domain with
        | true, value -> value
        | _ ->
            let value =
                { State = ProviderCircuitState.Closed
                  ConsecutiveFailures = 0 }

            registry.Entries.Add(domain, value)
            value

    let tryEnter domain registry =
        lock registry.SyncRoot (fun () ->
            let value = entry domain registry

            match value.State with
            | ProviderCircuitState.Closed -> true
            | ProviderCircuitState.HalfOpen -> false
            | ProviderCircuitState.Open until when registry.Clock() >= until ->
                value.State <- ProviderCircuitState.HalfOpen
                true
            | ProviderCircuitState.Open _ -> false)

    let recordSuccess domain registry =
        lock registry.SyncRoot (fun () ->
            let value = entry domain registry
            value.State <- ProviderCircuitState.Closed
            value.ConsecutiveFailures <- 0)

    let recordFailure domain registry =
        lock registry.SyncRoot (fun () ->
            let value = entry domain registry
            value.ConsecutiveFailures <- value.ConsecutiveFailures + 1

            if
                value.State = ProviderCircuitState.HalfOpen
                || value.ConsecutiveFailures >= registry.Configuration.FailureThreshold
            then
                value.State <- ProviderCircuitState.Open(registry.Clock() + registry.Configuration.OpenDuration))

    let state domain registry =
        lock registry.SyncRoot (fun () -> (entry domain registry).State)

type ProviderRetryBudget =
    private
        { MaximumAttempts: int
          Deadline: DateTimeOffset
          Clock: unit -> DateTimeOffset
          SyncRoot: obj
          mutable ConsumedAttempts: int }

[<RequireQualifiedAccess>]
module ProviderRetryBudget =
    let create maximumAttempts maximumDuration clock =
        if maximumAttempts <= 0 then
            invalidArg (nameof maximumAttempts) "Provider retry budget must allow at least one attempt."

        if maximumDuration <= TimeSpan.Zero then
            invalidArg (nameof maximumDuration) "Provider retry duration budget must be positive."

        { MaximumAttempts = maximumAttempts
          Deadline = clock () + maximumDuration
          Clock = clock
          SyncRoot = obj ()
          ConsumedAttempts = 0 }

    let tryConsumeAttempt budget =
        lock budget.SyncRoot (fun () ->
            if
                budget.ConsumedAttempts >= budget.MaximumAttempts
                || budget.Clock() >= budget.Deadline
            then
                false
            else
                budget.ConsumedAttempts <- budget.ConsumedAttempts + 1
                true)

    let canDelay delay budget =
        delay >= TimeSpan.Zero && budget.Clock() + delay < budget.Deadline

    let consumedAttempts budget =
        lock budget.SyncRoot (fun () -> budget.ConsumedAttempts)

[<RequireQualifiedAccess>]
type ProviderResponseProgress =
    | NoResponse
    | PartialResponse

type ProviderAttemptFailure =
    { Failure: PlatformFailure
      RetryAfter: TimeSpan option
      Progress: ProviderResponseProgress }

[<RequireQualifiedAccess>]
module ProviderAttemptFailure =
    let create failure retryAfter progress =
        retryAfter
        |> Option.iter (fun value ->
            if value < TimeSpan.Zero then
                invalidArg (nameof retryAfter) "Provider retry-after duration cannot be negative.")

        { Failure = failure
          RetryAfter = retryAfter
          Progress = progress }

type ProviderFailoverPlan =
    private
        { EndpointDomains: (string * ProviderFailureDomain) list
          MaximumAttemptsPerEndpoint: int }

[<RequireQualifiedAccess>]
module ProviderFailoverPlan =
    let create decision failureDomains maximumAttemptsPerEndpoint =
        if maximumAttemptsPerEndpoint <= 0 then
            invalidArg (nameof maximumAttemptsPerEndpoint) "Per-endpoint attempts must be positive."

        let endpointDomains =
            decision.OrderedEligibleEndpointIds
            |> List.map (fun endpointId ->
                match Map.tryFind endpointId failureDomains with
                | Some domain -> endpointId, domain
                | None ->
                    invalidArg
                        (nameof failureDomains)
                        $"Eligible endpoint '{endpointId}' has no configured failure domain.")

        if endpointDomains.IsEmpty then
            invalidArg (nameof decision) "Failover requires at least one eligible routed endpoint."

        { EndpointDomains = endpointDomains
          MaximumAttemptsPerEndpoint = maximumAttemptsPerEndpoint }

type ProviderResiliencePolicy =
    private
        { InitialRetryDelay: TimeSpan
          MaximumRetryDelay: TimeSpan }

[<RequireQualifiedAccess>]
module ProviderResiliencePolicy =
    let create initialRetryDelay maximumRetryDelay =
        if initialRetryDelay < TimeSpan.Zero then
            invalidArg (nameof initialRetryDelay) "Initial provider retry delay cannot be negative."

        if maximumRetryDelay < initialRetryDelay then
            invalidArg (nameof maximumRetryDelay) "Maximum retry delay cannot be less than the initial delay."

        { InitialRetryDelay = initialRetryDelay
          MaximumRetryDelay = maximumRetryDelay }

[<RequireQualifiedAccess>]
type ProviderExecutionAttemptOutcome =
    | Succeeded
    | Failed of ProviderErrorCategory option
    | CircuitOpen

type ProviderExecutionAttempt =
    { EndpointId: string
      Attempt: int
      Outcome: ProviderExecutionAttemptOutcome }

[<RequireQualifiedAccess>]
type ProviderResilienceFailure =
    | BudgetExhausted of PlatformFailure option
    | PermanentFailure of endpointId: string * failure: PlatformFailure
    | PartialResponse of endpointId: string * failure: PlatformFailure
    | AllEndpointsUnavailable of PlatformFailure option

type ProviderResilienceResult<'value> =
    { Result: Result<'value, ProviderResilienceFailure>
      Attempts: ProviderExecutionAttempt list }

[<RequireQualifiedAccess>]
module ProviderResilience =
    let private retryDelay policy attempt retryAfter =
        let multiplier = pown 2 (max 0 (attempt - 1))
        let exponentialTicks = policy.InitialRetryDelay.Ticks * int64 multiplier

        let bounded =
            TimeSpan.FromTicks(min exponentialTicks policy.MaximumRetryDelay.Ticks)

        match retryAfter with
        | Some guidance when guidance > bounded -> guidance
        | _ -> bounded

    let executeAsync
        policy
        budget
        circuits
        plan
        (delayAsync: TimeSpan -> CancellationToken -> Task)
        executeAttempt
        (cancellationToken: CancellationToken)
        =
        task {
            let attempts = ResizeArray<ProviderExecutionAttempt>()
            let mutable endpoints = plan.EndpointDomains
            let mutable terminal: Result<'value, ProviderResilienceFailure> option = None
            let mutable lastFailure: PlatformFailure option = None

            while terminal.IsNone && not endpoints.IsEmpty do
                let endpointId, domain = endpoints.Head
                endpoints <- endpoints.Tail

                if not (ProviderCircuitRegistry.tryEnter domain circuits) then
                    attempts.Add
                        { EndpointId = endpointId
                          Attempt = 0
                          Outcome = ProviderExecutionAttemptOutcome.CircuitOpen }
                else
                    let mutable endpointAttempt = 0
                    let mutable continueEndpoint = true

                    while terminal.IsNone && continueEndpoint do
                        if not (ProviderRetryBudget.tryConsumeAttempt budget) then
                            terminal <- Some(Error(ProviderResilienceFailure.BudgetExhausted lastFailure))
                        else
                            endpointAttempt <- endpointAttempt + 1
                            let! result = executeAttempt endpointId endpointAttempt cancellationToken

                            match result with
                            | Ok value ->
                                ProviderCircuitRegistry.recordSuccess domain circuits

                                attempts.Add
                                    { EndpointId = endpointId
                                      Attempt = endpointAttempt
                                      Outcome = ProviderExecutionAttemptOutcome.Succeeded }

                                terminal <- Some(Ok value)
                            | Error failure ->
                                lastFailure <- Some failure.Failure

                                attempts.Add
                                    { EndpointId = endpointId
                                      Attempt = endpointAttempt
                                      Outcome = ProviderExecutionAttemptOutcome.Failed failure.Failure.ProviderCategory }

                                if failure.Progress = ProviderResponseProgress.PartialResponse then
                                    terminal <-
                                        Some(
                                            Error(
                                                ProviderResilienceFailure.PartialResponse(endpointId, failure.Failure)
                                            )
                                        )
                                elif not failure.Failure.Retryable then
                                    terminal <-
                                        Some(
                                            Error(
                                                ProviderResilienceFailure.PermanentFailure(endpointId, failure.Failure)
                                            )
                                        )
                                else
                                    ProviderCircuitRegistry.recordFailure domain circuits

                                    if endpointAttempt >= plan.MaximumAttemptsPerEndpoint then
                                        continueEndpoint <- false
                                    else
                                        let delay = retryDelay policy endpointAttempt failure.RetryAfter

                                        if ProviderRetryBudget.canDelay delay budget then
                                            do! delayAsync delay cancellationToken
                                        else
                                            continueEndpoint <- false

            return
                { Result =
                    terminal
                    |> Option.defaultValue (Error(ProviderResilienceFailure.AllEndpointsUnavailable lastFailure))
                  Attempts = attempts |> Seq.toList }
        }

[<RequireQualifiedAccess>]
type ProviderCacheOperation =
    | DeterministicCompletion
    | Embedding

type ProviderCacheKey =
    private
        { OwnerId: string
          EndpointId: string
          ModelId: string
          Operation: ProviderCacheOperation
          InputDigest: string
          ConfigurationDigest: string }

[<RequireQualifiedAccess>]
module ProviderCacheKey =
    let create ownerId endpointId modelId operation inputDigest configurationDigest =
        let canonical name value =
            if String.IsNullOrWhiteSpace value || value <> value.Trim() then
                invalidArg name "Provider cache identities and digests must be canonical and non-blank."

        canonical (nameof ownerId) ownerId
        canonical (nameof endpointId) endpointId
        canonical (nameof modelId) modelId
        canonical (nameof inputDigest) inputDigest
        canonical (nameof configurationDigest) configurationDigest

        { OwnerId = ownerId
          EndpointId = endpointId
          ModelId = modelId
          Operation = operation
          InputDigest = inputDigest
          ConfigurationDigest = configurationDigest }

type ProviderCacheEntry<'value> =
    { Value: 'value
      CreatedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset }

type ProviderCache<'value> =
    { TryGetAsync: ProviderCacheKey -> CancellationToken -> Task<ProviderCacheEntry<'value> option>
      SetAsync: ProviderCacheKey -> ProviderCacheEntry<'value> -> CancellationToken -> Task }
