namespace Nao.Runtime.Orleans

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents
open Nao.Runtime.Orleans.Grains

type AdministrativeChange =
    private
        { OperationId: Guid
          ResourceKind: AdministrativeResourceKind
          ResourceId: string
          Action: string
          Reason: string
          ExpectedRevision: int64
          Parameters: Map<string, string> }

[<RequireQualifiedAccess>]
module AdministrativeChange =
    let create operationId resourceKind resourceId action reason expectedRevision parameters =
        if operationId = Guid.Empty then
            invalidArg (nameof operationId) "Administrative operation identity cannot be empty."

        if expectedRevision < 0L then
            invalidArg (nameof expectedRevision) "Administrative expected revision cannot be negative."

        RuntimeAdministrationValidation.validate resourceKind resourceId action parameters
        RuntimeAdministrationValidation.canonical (nameof reason) reason

        { OperationId = operationId
          ResourceKind = resourceKind
          ResourceId = resourceId
          Action = action
          Reason = reason
          ExpectedRevision = expectedRevision
          Parameters = parameters }

    let operationId value = value.OperationId
    let resourceKind value = value.ResourceKind
    let resourceId value = value.ResourceId
    let action value = value.Action
    let reason value = value.Reason
    let expectedRevision value = value.ExpectedRevision
    let parameters value = value.Parameters

type IRuntimeAdministrationTransport =
    abstract member PreviewAsync:
        principal: SecurityPrincipal *
        tenantId: TenantId *
        request: AdministrativeMutationRequest *
        cancellationToken: CancellationToken ->
            Task<AdministrativeImpactPreview>

    abstract member MutateAsync:
        principal: SecurityPrincipal *
        tenantId: TenantId *
        request: AdministrativeMutationRequest *
        cancellationToken: CancellationToken ->
            Task<AdministrativeMutationResult>

    abstract member GetResourceAsync:
        principal: SecurityPrincipal *
        tenantId: TenantId *
        resourceKind: string *
        resourceId: string *
        cancellationToken: CancellationToken ->
            Task<AdministrativeResourceSnapshot>

type RuntimeAdministrationClient
    (principal: SecurityPrincipal, tenantId: TenantId, transport: IRuntimeAdministrationTransport) =
    do
        if isNull (box principal) then
            nullArg (nameof principal)

        if isNull (box transport) then
            nullArg (nameof transport)

        if SecurityPrincipal.tenantId principal <> tenantId then
            raise (UnauthorizedAccessException "Administration client principal belongs to another tenant.")

    let request (change: AdministrativeChange) =
        let resourceKind =
            change
            |> AdministrativeChange.resourceKind
            |> AdministrativeResourceKind.serialize

        let value =
            AdministrativeMutationRequest(
                OperationId = AdministrativeChange.operationId change,
                ResourceKind = resourceKind,
                ResourceId = AdministrativeChange.resourceId change,
                Action = AdministrativeChange.action change,
                Reason = AdministrativeChange.reason change,
                ExpectedRevision = AdministrativeChange.expectedRevision change
            )

        change
        |> AdministrativeChange.parameters
        |> Map.iter (fun name parameterValue ->
            value.Parameters.Add(AdministrativeParameter(Name = name, Value = parameterValue)))

        value

    member _.PreviewAsync(change, cancellationToken) =
        RuntimeAdministrationAuthorization.authorize
            tenantId
            (AdministrativeChange.resourceKind change)
            (AdministrativeChange.action change)
            principal

        transport.PreviewAsync(principal, tenantId, request change, cancellationToken)

    member _.ApplyAsync(change, cancellationToken) =
        RuntimeAdministrationAuthorization.authorize
            tenantId
            (AdministrativeChange.resourceKind change)
            (AdministrativeChange.action change)
            principal

        transport.MutateAsync(principal, tenantId, request change, cancellationToken)

    member _.GetResourceAsync(resourceKind, resourceId, cancellationToken) =
        RuntimeAdministrationAuthorization.authorize tenantId resourceKind "read" principal

        transport.GetResourceAsync(
            principal,
            tenantId,
            AdministrativeResourceKind.serialize resourceKind,
            resourceId,
            cancellationToken
        )

    member this.RollbackAsync
        (operationId, resourceKind, resourceId, reason, expectedRevision, targetRevision, cancellationToken)
        =
        let change =
            AdministrativeChange.create
                operationId
                resourceKind
                resourceId
                "rollback"
                reason
                expectedRevision
                (Map.ofList [ "target_revision", string targetRevision ])

        this.ApplyAsync(change, cancellationToken)
