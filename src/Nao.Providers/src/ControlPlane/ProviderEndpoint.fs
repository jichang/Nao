namespace Nao.Providers

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
type ProviderCapabilityAuditKind =
    | Activated
    | Changed

type ProviderCapabilityAudit =
    { Id: Guid
      Timestamp: DateTimeOffset
      EndpointId: string
      PreviousVersion: string option
      CurrentVersion: string
      Kind: ProviderCapabilityAuditKind
      ChangedCapabilities: ProviderCapability list }

[<RequireQualifiedAccess>]
type ProviderInvocationFailure = Incompatible of ProviderCompatibilityFailure list

type ProviderEndpoint =
    private
        { Provider: LlmProvider
          ObserveAsync: CancellationToken -> Task<ProviderCapabilityObservation>
          RecordAuditAsync: ProviderCapabilityAudit -> Task
          Clock: unit -> DateTimeOffset
          Gate: SemaphoreSlim
          mutable LastObservation: ProviderCapabilityObservation option }

type ProviderEndpointConfiguration =
    private
        { ObserveAsync: CancellationToken -> Task<ProviderCapabilityObservation>
          RecordAuditAsync: ProviderCapabilityAudit -> Task
          Clock: unit -> DateTimeOffset }

[<RequireQualifiedAccess>]
module ProviderEndpointConfiguration =
    let create observeAsync recordAuditAsync clock =
        if isNull (box observeAsync) then
            nullArg (nameof observeAsync)

        if isNull (box recordAuditAsync) then
            nullArg (nameof recordAuditAsync)

        if isNull (box clock) then
            nullArg (nameof clock)

        { ObserveAsync = observeAsync
          RecordAuditAsync = recordAuditAsync
          Clock = clock }

    let configured observation recordAuditAsync clock =
        create (fun _ -> Task.FromResult observation) recordAuditAsync clock

[<RequireQualifiedAccess>]
module ProviderEndpoint =
    let create provider observeAsync recordAuditAsync clock =
        if isNull (box observeAsync) then
            nullArg (nameof observeAsync)

        if isNull (box recordAuditAsync) then
            nullArg (nameof recordAuditAsync)

        if isNull (box clock) then
            nullArg (nameof clock)

        { Provider = provider
          ObserveAsync = observeAsync
          RecordAuditAsync = recordAuditAsync
          Clock = clock
          Gate = new SemaphoreSlim(1, 1)
          LastObservation = None }

    let fromConfiguration provider configuration =
        create provider configuration.ObserveAsync configuration.RecordAuditAsync configuration.Clock

    let private changedCapabilities previous current =
        ProviderCapability.all
        |> List.filter (fun capability ->
            ProviderCapabilities.support capability previous
            <> ProviderCapabilities.support capability current)

    let private sameObservation previous current =
        ProviderCapabilityObservation.endpointId previous = ProviderCapabilityObservation.endpointId current
        && ProviderCapabilityObservation.version previous = ProviderCapabilityObservation.version current
        && ProviderCapabilityObservation.source previous = ProviderCapabilityObservation.source current
        && ProviderCapabilityObservation.capabilities previous = ProviderCapabilityObservation.capabilities current
        && ProviderCapabilityObservation.model previous = ProviderCapabilityObservation.model current
        && ProviderCapabilityObservation.health previous = ProviderCapabilityObservation.health current
        && ProviderCapabilityObservation.readiness previous = ProviderCapabilityObservation.readiness current
        && ProviderCapabilityObservation.quota previous = ProviderCapabilityObservation.quota current
        && ProviderCapabilityObservation.maintenance previous = ProviderCapabilityObservation.maintenance current

    let refreshAsync (cancellationToken: CancellationToken) endpoint =
        task {
            do! endpoint.Gate.WaitAsync cancellationToken

            try
                let! current = endpoint.ObserveAsync cancellationToken

                match endpoint.LastObservation with
                | Some previous when sameObservation previous current -> ()
                | previous ->
                    let changed =
                        previous
                        |> Option.map (fun value ->
                            changedCapabilities
                                (ProviderCapabilityObservation.capabilities value)
                                (ProviderCapabilityObservation.capabilities current))
                        |> Option.defaultValue ProviderCapability.all

                    let audit =
                        { Id = Guid.NewGuid()
                          Timestamp = endpoint.Clock()
                          EndpointId = ProviderCapabilityObservation.endpointId current
                          PreviousVersion = previous |> Option.map ProviderCapabilityObservation.version
                          CurrentVersion = ProviderCapabilityObservation.version current
                          Kind =
                            if previous.IsSome then
                                ProviderCapabilityAuditKind.Changed
                            else
                                ProviderCapabilityAuditKind.Activated
                          ChangedCapabilities = changed }

                    do! endpoint.RecordAuditAsync audit
                    endpoint.LastObservation <- Some current

                return current
            finally
                endpoint.Gate.Release() |> ignore
        }

    let evaluateAsync requirements (cancellationToken: CancellationToken) endpoint =
        task {
            let! observation = refreshAsync cancellationToken endpoint
            let failures = ProviderCompatibility.evaluate requirements observation

            if failures.IsEmpty then
                return Ok observation
            else
                return Error(ProviderInvocationFailure.Incompatible failures)
        }

    let completeAsync requirements correlation conversation options cancellationToken endpoint =
        task {
            let! compatibility = evaluateAsync requirements cancellationToken endpoint

            match compatibility with
            | Error failure -> return Error failure
            | Ok _ ->
                let! result =
                    LlmProvider.completeAsync correlation conversation options cancellationToken endpoint.Provider

                return Ok result
        }

    let streamAsync requirements correlation conversation options onChunk cancellationToken endpoint =
        task {
            let! compatibility = evaluateAsync requirements cancellationToken endpoint

            match compatibility with
            | Error failure -> return Error failure
            | Ok _ ->
                let! result =
                    LlmProvider.streamAsync endpoint.Provider correlation conversation options onChunk cancellationToken

                return Ok result
        }

    let dispose endpoint =
        endpoint.Gate.Dispose()
        LlmProvider.dispose endpoint.Provider
