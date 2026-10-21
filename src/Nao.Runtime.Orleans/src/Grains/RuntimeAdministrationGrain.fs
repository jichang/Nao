namespace Nao.Runtime.Orleans.Grains

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Nao.Agents
open Nao.Runtime.Orleans
open Orleans
open Orleans.Runtime

[<GenerateSerializer>]
type AdministrativeParameter() =
    [<Id(0u)>]
    member val Name: string = "" with get, set

    [<Id(1u)>]
    member val Value: string = "" with get, set

[<GenerateSerializer>]
type AdministrativeMutationRequest() =
    [<Id(0u)>]
    member val OperationId: Guid = Guid.Empty with get, set

    [<Id(1u)>]
    member val ResourceKind: string = "" with get, set

    [<Id(2u)>]
    member val ResourceId: string = "" with get, set

    [<Id(3u)>]
    member val Action: string = "" with get, set

    [<Id(4u)>]
    member val ExpectedRevision: int64 = 0L with get, set

    [<Id(5u)>]
    member val Parameters: ResizeArray<AdministrativeParameter> = ResizeArray() with get, set

[<GenerateSerializer>]
type AdministrativeImpactPreview() =
    [<Id(0u)>]
    member val Summary: string = "" with get, set

    [<Id(1u)>]
    member val AffectedResourceIds: ResizeArray<string> = ResizeArray() with get, set

    [<Id(2u)>]
    member val Warnings: ResizeArray<string> = ResizeArray() with get, set

    [<Id(3u)>]
    member val CurrentRevision: int64 = 0L with get, set

    [<Id(4u)>]
    member val TargetRevision: int64 = 0L with get, set

[<GenerateSerializer>]
type AdministrativeMutationResult() =
    [<Id(0u)>]
    member val OperationId: Guid = Guid.Empty with get, set

    [<Id(1u)>]
    member val AuditId: Guid = Guid.Empty with get, set

    [<Id(2u)>]
    member val Revision: int64 = 0L with get, set

    [<Id(3u)>]
    member val Impact: AdministrativeImpactPreview = AdministrativeImpactPreview() with get, set

[<GenerateSerializer>]
type AdministrativeResourceSnapshot() =
    [<Id(0u)>]
    member val ResourceKind: string = "" with get, set

    [<Id(1u)>]
    member val ResourceId: string = "" with get, set

    [<Id(2u)>]
    member val Revision: int64 = 0L with get, set

    [<Id(3u)>]
    member val Disabled: bool = false with get, set

    [<Id(4u)>]
    member val UpdatedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

[<GenerateSerializer>]
type AdministrativePendingOperation() =
    [<Id(0u)>]
    member val OperationId: Guid = Guid.Empty with get, set

    [<Id(1u)>]
    member val Fingerprint: string = "" with get, set

    [<Id(2u)>]
    member val AuditId: Guid = Guid.Empty with get, set

    [<Id(3u)>]
    member val ActorId: string = "" with get, set

    [<Id(4u)>]
    member val ResourceKind: string = "" with get, set

    [<Id(5u)>]
    member val ResourceId: string = "" with get, set

    [<Id(6u)>]
    member val Action: string = "" with get, set

    [<Id(7u)>]
    member val CurrentRevision: int64 = 0L with get, set

    [<Id(8u)>]
    member val TargetRevision: int64 = 0L with get, set

    [<Id(9u)>]
    member val Parameters: ResizeArray<AdministrativeParameter> = ResizeArray() with get, set

    [<Id(10u)>]
    member val Impact: AdministrativeImpactPreview = AdministrativeImpactPreview() with get, set

    [<Id(11u)>]
    member val CreatedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

[<GenerateSerializer>]
type AdministrativeCompletedOperation() =
    [<Id(0u)>]
    member val OperationId: Guid = Guid.Empty with get, set

    [<Id(1u)>]
    member val Fingerprint: string = "" with get, set

    [<Id(2u)>]
    member val Result: AdministrativeMutationResult = AdministrativeMutationResult() with get, set

[<GenerateSerializer>]
type RuntimeAdministrationState() =
    [<Id(0u)>]
    member val TenantId: string = "" with get, set

    [<Id(1u)>]
    member val Resources: ResizeArray<AdministrativeResourceSnapshot> = ResizeArray() with get, set

    [<Id(2u)>]
    member val PendingOperations: ResizeArray<AdministrativePendingOperation> = ResizeArray() with get, set

    [<Id(3u)>]
    member val CompletedOperations: ResizeArray<AdministrativeCompletedOperation> = ResizeArray() with get, set

    [<Id(4u)>]
    member val SchemaVersion: int = 1 with get, set

[<RequireQualifiedAccess>]
module RuntimeAdministrationState =
    let resourceKey kind resourceId =
        $"{AdministrativeResourceKind.serialize kind}/{resourceId}"

    let tryResource kind resourceId (state: RuntimeAdministrationState) =
        let key = resourceKey kind resourceId

        state.Resources
        |> Seq.tryFind (fun resource -> $"{resource.ResourceKind}/{resource.ResourceId}" = key)

    let revision kind resourceId state =
        tryResource kind resourceId state
        |> Option.map _.Revision
        |> Option.defaultValue 0L

    let disabled kind resourceId state =
        tryResource kind resourceId state |> Option.exists _.Disabled

type IRuntimeAdministrationGrain =
    inherit IGrainWithStringKey

    abstract member PreviewAsync: request: AdministrativeMutationRequest -> Task<AdministrativeImpactPreview>

    abstract member MutateAsync: request: AdministrativeMutationRequest -> Task<AdministrativeMutationResult>

    abstract member GetResourceAsync: resourceKind: string * resourceId: string -> Task<AdministrativeResourceSnapshot>

    abstract member IsDisabledAsync: resourceKind: string * resourceId: string -> Task<bool>

[<RequireQualifiedAccess>]
module RuntimeAdministrationDisablement =
    let requireEnabledAsync (grainFactory: IGrainFactory) tenant resourceKind resourceId =
        task {
            let grain =
                grainFactory.GetGrain<IRuntimeAdministrationGrain>(TenantId.value tenant)

            let! disabled = grain.IsDisabledAsync(AdministrativeResourceKind.serialize resourceKind, resourceId)

            if disabled then
                raise (
                    InvalidOperationException(
                        $"Administrative emergency disablement blocks {AdministrativeResourceKind.serialize resourceKind} '{resourceId}'."
                    )
                )
        }

type RuntimeAdministrationGrain
    (
        [<PersistentState("runtimeAdministration", "sessionStore")>] state: IPersistentState<RuntimeAdministrationState>,
        principalAccessor: Func<SecurityPrincipal>,
        executor: IRuntimeAdministrationExecutor,
        auditStore: IRuntimeAdministrationAuditStore
    ) as this =
    inherit Grain()

    let tenantId () =
        this.GetPrimaryKeyString() |> TenantId.parse

    let parameters (request: AdministrativeMutationRequest) =
        request.Parameters
        |> Seq.map (fun parameter -> parameter.Name, parameter.Value)
        |> Map.ofSeq

    let parse (request: AdministrativeMutationRequest) =
        if request.OperationId = Guid.Empty then
            invalidArg (nameof request.OperationId) "Administrative operation ID cannot be empty."

        if request.ExpectedRevision < 0L then
            invalidArg (nameof request.ExpectedRevision) "Administrative expected revision cannot be negative."

        let kind = AdministrativeResourceKind.parse request.ResourceKind
        let values = parameters request
        RuntimeAdministrationValidation.validate kind request.ResourceId request.Action values
        kind, values

    let preview currentRevision (impact: AdministrativeImpact) =
        let value =
            AdministrativeImpactPreview(
                Summary = impact.Summary,
                CurrentRevision = currentRevision,
                TargetRevision = currentRevision + 1L
            )

        value.AffectedResourceIds.AddRange impact.AffectedResourceIds
        value.Warnings.AddRange impact.Warnings
        value

    let requireRevision expected current =
        if expected <> current then
            raise (
                InvalidOperationException(
                    $"Administrative resource revision conflict: expected {expected}, current {current}."
                )
            )

    let operationParameters (pending: AdministrativePendingOperation) =
        pending.Parameters
        |> Seq.map (fun parameter -> parameter.Name, parameter.Value)
        |> Map.ofSeq

    let execution tenant (pending: AdministrativePendingOperation) =
        { OperationId = pending.OperationId
          TenantId = tenant
          ResourceKind = AdministrativeResourceKind.parse pending.ResourceKind
          ResourceId = pending.ResourceId
          Action = pending.Action
          Parameters = operationParameters pending
          TargetRevision = pending.TargetRevision }

    let result (pending: AdministrativePendingOperation) =
        AdministrativeMutationResult(
            OperationId = pending.OperationId,
            AuditId = pending.AuditId,
            Revision = pending.TargetRevision,
            Impact = pending.Impact
        )

    let completeAsync tenant (pending: AdministrativePendingOperation) =
        task {
            let operation = execution tenant pending
            do! executor.ApplyAsync(operation, CancellationToken.None)
            do! executor.PropagateAsync(operation, CancellationToken.None)

            let impact = pending.Impact

            do!
                auditStore.RecordAsync
                    { Id = pending.AuditId
                      OperationId = pending.OperationId
                      Timestamp = pending.CreatedAt
                      TenantId = tenant
                      ActorId = UserId.parse pending.ActorId
                      ResourceKind = operation.ResourceKind
                      ResourceId = operation.ResourceId
                      Action = operation.Action
                      Revision = operation.TargetRevision
                      ParameterNames = operation.Parameters |> Map.keys |> Seq.sort |> Seq.toList
                      ImpactSummary = impact.Summary
                      AffectedResourceCount = impact.AffectedResourceIds.Count
                      WarningCount = impact.Warnings.Count }

            let resource =
                RuntimeAdministrationState.tryResource operation.ResourceKind operation.ResourceId state.State
                |> Option.defaultWith (fun () ->
                    let created =
                        AdministrativeResourceSnapshot(
                            ResourceKind = pending.ResourceKind,
                            ResourceId = pending.ResourceId
                        )

                    state.State.Resources.Add created
                    created)

            resource.Revision <- pending.TargetRevision
            resource.UpdatedAt <- DateTimeOffset.UtcNow

            if AdministrativeResourceKind.supportsEmergencyDisablement operation.ResourceKind then
                resource.Disabled <- operation.Action = "disable"

            let completedResult = result pending

            state.State.CompletedOperations.Add(
                AdministrativeCompletedOperation(
                    OperationId = pending.OperationId,
                    Fingerprint = pending.Fingerprint,
                    Result = completedResult
                )
            )

            if state.State.CompletedOperations.Count > 256 then
                state.State.CompletedOperations.RemoveAt 0

            state.State.PendingOperations.Remove pending |> ignore
            do! state.WriteStateAsync()
            return completedResult
        }

    let flushPendingAsync () =
        task {
            let tenant = tenantId ()

            for pending in state.State.PendingOperations |> Seq.toList do
                let! _ = completeAsync tenant pending
                ()
        }

    override _.OnActivateAsync cancellationToken =
        task {
            let tenant = tenantId ()

            if not state.RecordExists then
                state.State.TenantId <- TenantId.value tenant
                do! state.WriteStateAsync cancellationToken
            elif state.State.TenantId <> TenantId.value tenant then
                raise (UnauthorizedAccessException "Administrative grain state belongs to another tenant.")

            do! flushPendingAsync ()
        }
        :> Task

    interface IRuntimeAdministrationGrain with
        member _.PreviewAsync(request) =
            task {
                let kind, values = parse request
                let tenant = tenantId ()
                RuntimeAdministrationAuthorization.authorize tenant kind request.Action (principalAccessor.Invoke())

                if state.State.PendingOperations.Count <> 0 then
                    raise (InvalidOperationException "An administrative mutation is awaiting durable propagation.")

                let current =
                    RuntimeAdministrationState.revision kind request.ResourceId state.State

                requireRevision request.ExpectedRevision current

                let! impact =
                    executor.PreviewAsync(
                        tenant,
                        kind,
                        request.ResourceId,
                        request.Action,
                        values,
                        CancellationToken.None
                    )

                return preview current impact
            }

        member _.MutateAsync(request) =
            task {
                let kind, values = parse request
                let tenant = tenantId ()
                let principal = principalAccessor.Invoke()
                RuntimeAdministrationAuthorization.authorize tenant kind request.Action principal

                let fingerprint =
                    RuntimeAdministrationValidation.fingerprint
                        request.OperationId
                        kind
                        request.ResourceId
                        request.Action
                        request.ExpectedRevision
                        values

                match
                    state.State.CompletedOperations
                    |> Seq.tryFind (fun operation -> operation.OperationId = request.OperationId)
                with
                | Some completed when completed.Fingerprint <> fingerprint ->
                    return
                        raise (
                            InvalidOperationException(
                                "Administrative operation ID was reused with different request content."
                            )
                        )
                | Some completed -> return completed.Result
                | None ->
                    match
                        state.State.PendingOperations
                        |> Seq.tryFind (fun operation -> operation.OperationId = request.OperationId)
                    with
                    | Some pending when pending.Fingerprint <> fingerprint ->
                        return
                            raise (
                                InvalidOperationException(
                                    "Administrative operation ID was reused with different request content."
                                )
                            )
                    | Some pending -> return! completeAsync tenant pending
                    | None when state.State.PendingOperations.Count > 0 ->
                        return
                            raise (
                                InvalidOperationException("An administrative mutation is awaiting durable propagation.")
                            )
                    | None ->
                        let current =
                            RuntimeAdministrationState.revision kind request.ResourceId state.State

                        requireRevision request.ExpectedRevision current

                        let! impact =
                            executor.PreviewAsync(
                                tenant,
                                kind,
                                request.ResourceId,
                                request.Action,
                                values,
                                CancellationToken.None
                            )

                        let impactPreview = preview current impact

                        let pending =
                            AdministrativePendingOperation(
                                OperationId = request.OperationId,
                                Fingerprint = fingerprint,
                                AuditId = Guid.NewGuid(),
                                ActorId = (SecurityPrincipal.subjectId principal |> UserId.value),
                                ResourceKind = request.ResourceKind,
                                ResourceId = request.ResourceId,
                                Action = request.Action,
                                CurrentRevision = current,
                                TargetRevision = current + 1L,
                                Impact = impactPreview,
                                CreatedAt = DateTimeOffset.UtcNow
                            )

                        request.Parameters
                        |> Seq.iter (fun parameter ->
                            pending.Parameters.Add(
                                AdministrativeParameter(Name = parameter.Name, Value = parameter.Value)
                            ))

                        state.State.PendingOperations.Add pending
                        do! state.WriteStateAsync()
                        return! completeAsync tenant pending
            }

        member _.GetResourceAsync(resourceKind, resourceId) =
            task {
                let kind = AdministrativeResourceKind.parse resourceKind
                RuntimeAdministrationValidation.canonical (nameof resourceId) resourceId
                let tenant = tenantId ()
                RuntimeAdministrationAuthorization.authorize tenant kind "read" (principalAccessor.Invoke())

                return
                    RuntimeAdministrationState.tryResource kind resourceId state.State
                    |> Option.defaultValue (
                        AdministrativeResourceSnapshot(ResourceKind = resourceKind, ResourceId = resourceId)
                    )
            }

        member _.IsDisabledAsync(resourceKind, resourceId) =
            task {
                let kind = AdministrativeResourceKind.parse resourceKind
                RuntimeAdministrationValidation.canonical (nameof resourceId) resourceId
                let tenant = tenantId ()

                if SecurityPrincipal.tenantId (principalAccessor.Invoke()) <> tenant then
                    raise (UnauthorizedAccessException "Disablement checks cannot cross tenant boundaries.")

                return RuntimeAdministrationState.disabled kind resourceId state.State
            }
