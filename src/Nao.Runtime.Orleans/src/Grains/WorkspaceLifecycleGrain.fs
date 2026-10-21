namespace Nao.Runtime.Orleans.Grains

open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Nao.Agents
open Nao.Runtime.Orleans
open Orleans
open Orleans.Runtime

[<RequireQualifiedAccess>]
type WorkspaceVersionLifecycle =
    | Staged
    | Canary
    | Active
    | Retired

[<RequireQualifiedAccess>]
module WorkspaceVersionLifecycle =
    let serialize =
        function
        | WorkspaceVersionLifecycle.Staged -> "staged"
        | WorkspaceVersionLifecycle.Canary -> "canary"
        | WorkspaceVersionLifecycle.Active -> "active"
        | WorkspaceVersionLifecycle.Retired -> "retired"

[<GenerateSerializer>]
type WorkspaceRouteVersionRecord() =
    [<Id(0u)>]
    member val VersionId: string = "" with get, set

    [<Id(1u)>]
    member val ContentDigest: string = "" with get, set

    [<Id(2u)>]
    member val Lifecycle: string = "" with get, set

    [<Id(3u)>]
    member val CanaryPercentage: int = 0 with get, set

    [<Id(4u)>]
    member val RegisteredAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

    [<Id(5u)>]
    member val RegisteredBy: string = "" with get, set

[<GenerateSerializer>]
type WorkspaceLifecycleAuditRecord() =
    [<Id(0u)>]
    member val Id: Guid = Guid.Empty with get, set

    [<Id(1u)>]
    member val VersionId: string = "" with get, set

    [<Id(2u)>]
    member val Action: string = "" with get, set

    [<Id(3u)>]
    member val FromState: string = "" with get, set

    [<Id(4u)>]
    member val ToState: string = "" with get, set

    [<Id(5u)>]
    member val Revision: int64 = 0L with get, set

    [<Id(6u)>]
    member val ActorId: string = "" with get, set

    [<Id(7u)>]
    member val RecordedAt: DateTimeOffset = DateTimeOffset.MinValue with get, set

[<GenerateSerializer>]
type WorkspaceRouteState() =
    [<Id(0u)>]
    member val TenantId: string = "" with get, set

    [<Id(1u)>]
    member val WorkspaceName: string = "" with get, set

    [<Id(2u)>]
    member val Revision: int64 = 0L with get, set

    [<Id(3u)>]
    member val Versions: ResizeArray<WorkspaceRouteVersionRecord> = ResizeArray() with get, set

    [<Id(4u)>]
    member val ActiveVersionId: string = "" with get, set

    [<Id(5u)>]
    member val PromotionHistory: ResizeArray<string> = ResizeArray() with get, set

    [<Id(6u)>]
    member val SchemaVersion: int = 1 with get, set

    [<Id(7u)>]
    member val PendingAudits: ResizeArray<WorkspaceLifecycleAuditRecord> = ResizeArray() with get, set

[<RequireQualifiedAccess>]
module WorkspaceRouting =
    let private requireRevision expected (state: WorkspaceRouteState) =
        if state.Revision <> expected then
            raise (
                InvalidOperationException(
                    $"Workspace route revision conflict: expected {expected}, current {state.Revision}."
                )
            )

    let private findVersion versionId (state: WorkspaceRouteState) =
        state.Versions
        |> Seq.tryFind (fun version -> version.VersionId = WorkspaceVersionId.value versionId)
        |> Option.defaultWith (fun () ->
            raise (
                InvalidOperationException(
                    $"Workspace version '{WorkspaceVersionId.value versionId}' is not staged in this route."
                )
            ))

    let private advance (state: WorkspaceRouteState) = state.Revision <- state.Revision + 1L

    let clone (state: WorkspaceRouteState) =
        let copy =
            WorkspaceRouteState(
                TenantId = state.TenantId,
                WorkspaceName = state.WorkspaceName,
                Revision = state.Revision,
                ActiveVersionId = state.ActiveVersionId,
                SchemaVersion = state.SchemaVersion
            )

        state.Versions
        |> Seq.iter (fun version ->
            copy.Versions.Add(
                WorkspaceRouteVersionRecord(
                    VersionId = version.VersionId,
                    ContentDigest = version.ContentDigest,
                    Lifecycle = version.Lifecycle,
                    CanaryPercentage = version.CanaryPercentage,
                    RegisteredAt = version.RegisteredAt,
                    RegisteredBy = version.RegisteredBy
                )
            ))

        copy.PromotionHistory.AddRange state.PromotionHistory

        state.PendingAudits
        |> Seq.iter (fun audit ->
            copy.PendingAudits.Add(
                WorkspaceLifecycleAuditRecord(
                    Id = audit.Id,
                    VersionId = audit.VersionId,
                    Action = audit.Action,
                    FromState = audit.FromState,
                    ToState = audit.ToState,
                    Revision = audit.Revision,
                    ActorId = audit.ActorId,
                    RecordedAt = audit.RecordedAt
                )
            ))

        copy

    let register expectedRevision registeredBy registeredAt (registration: WorkspaceRegistration) state =
        requireRevision expectedRevision state

        if WorkspaceVersionId.name registration.Identity <> state.WorkspaceName then
            invalidArg (nameof registration) "Workspace registration does not belong to this logical route."

        let versionId = WorkspaceVersionId.value registration.Identity

        if state.Versions |> Seq.exists (fun version -> version.VersionId = versionId) then
            invalidArg (nameof registration) $"Workspace version '{versionId}' is already staged."

        state.Versions.Add(
            WorkspaceRouteVersionRecord(
                VersionId = versionId,
                ContentDigest = registration.ContentDigest,
                Lifecycle = WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Staged,
                RegisteredAt = registeredAt,
                RegisteredBy = registeredBy
            )
        )

        advance state

    let setCanary expectedRevision versionId percentage state =
        requireRevision expectedRevision state

        if percentage <= 0 || percentage >= 100 then
            invalidArg (nameof percentage) "Canary percentage must be between 1 and 99."

        let target = findVersion versionId state

        if
            target.Lifecycle = WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Active
            || target.Lifecycle = WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Retired
        then
            invalidOp "Only a staged or canary workspace version can receive canary traffic."

        state.Versions
        |> Seq.filter (fun version ->
            version.Lifecycle = WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Canary
            && version.VersionId <> target.VersionId)
        |> Seq.iter (fun version ->
            version.Lifecycle <- WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Staged
            version.CanaryPercentage <- 0)

        target.Lifecycle <- WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Canary
        target.CanaryPercentage <- percentage
        advance state

    let private activate expectedRevision versionId requirePriorPromotion state =
        requireRevision expectedRevision state
        let target = findVersion versionId state

        if target.Lifecycle = WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Retired then
            invalidOp "A retired workspace version cannot become active."

        if target.VersionId = state.ActiveVersionId then
            invalidOp "The requested workspace version is already active."

        if requirePriorPromotion && not (state.PromotionHistory.Contains target.VersionId) then
            invalidOp "Rollback can target only a previously active workspace version."

        let previousActive = state.ActiveVersionId

        if not (String.IsNullOrEmpty previousActive) && previousActive <> target.VersionId then
            let previous =
                state.Versions |> Seq.find (fun version -> version.VersionId = previousActive)

            previous.Lifecycle <- WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Staged
            previous.CanaryPercentage <- 0

            if not (state.PromotionHistory.Contains previousActive) then
                state.PromotionHistory.Add previousActive

        target.Lifecycle <- WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Active
        target.CanaryPercentage <- 0
        state.ActiveVersionId <- target.VersionId
        advance state

    let promote expectedRevision versionId state =
        activate expectedRevision versionId false state

    let rollback expectedRevision versionId state =
        activate expectedRevision versionId true state

    let retire expectedRevision versionId state =
        requireRevision expectedRevision state
        let target = findVersion versionId state

        if target.VersionId = state.ActiveVersionId then
            invalidOp "The active workspace version cannot be retired."

        if target.Lifecycle = WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Retired then
            invalidOp "The workspace version is already retired."

        target.Lifecycle <- WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Retired
        target.CanaryPercentage <- 0
        advance state

    let resolve stickinessKey (state: WorkspaceRouteState) =
        if String.IsNullOrWhiteSpace stickinessKey then
            invalidArg (nameof stickinessKey) "Workspace routing requires a stable non-blank stickiness key."

        if String.IsNullOrEmpty state.ActiveVersionId then
            invalidOp "Workspace route has no active version."

        match
            state.Versions
            |> Seq.tryFind (fun version ->
                version.Lifecycle = WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Canary)
        with
        | None -> WorkspaceVersionId.parse state.ActiveVersionId
        | Some canary ->
            let bucket =
                $"{state.TenantId}/{state.WorkspaceName}/{stickinessKey}"
                |> Encoding.UTF8.GetBytes
                |> SHA256.HashData
                |> fun digest -> BitConverter.ToUInt32(digest, 0) % 100u
                |> int

            if bucket < canary.CanaryPercentage then
                WorkspaceVersionId.parse canary.VersionId
            else
                WorkspaceVersionId.parse state.ActiveVersionId

[<RequireQualifiedAccess>]
module WorkspaceLifecycleAuthorization =
    [<Literal>]
    let RequiredRole = "nao.workspace.manage"

    let authorize address principal =
        if
            SecurityPrincipal.tenantId principal
            <> RuntimeAddress.workspaceRouteTenant address
        then
            raise (UnauthorizedAccessException "Workspace lifecycle access cannot cross tenant boundaries.")

        if not (SecurityPrincipal.roles principal |> Set.contains RequiredRole) then
            raise (UnauthorizedAccessException $"Workspace lifecycle requires role '{RequiredRole}'.")

        match SecurityPrincipal.authenticationStrength principal with
        | AuthenticationStrength.MultiFactor
        | AuthenticationStrength.PhishingResistant -> ()
        | _ -> raise (UnauthorizedAccessException "Workspace lifecycle mutation requires multi-factor authentication.")

type IWorkspaceLifecycleGrain =
    inherit IGrainWithStringKey

    abstract member RegisterVersionAsync: versionId: string * expectedRevision: int64 -> Task
    abstract member SetCanaryAsync: versionId: string * percentage: int * expectedRevision: int64 -> Task
    abstract member PromoteAsync: versionId: string * expectedRevision: int64 -> Task
    abstract member RollbackAsync: versionId: string * expectedRevision: int64 -> Task
    abstract member RetireAsync: versionId: string * expectedRevision: int64 -> Task
    abstract member ResolveAsync: stickinessKey: string -> Task<string>
    abstract member GetAsync: unit -> Task<WorkspaceRouteState>

type WorkspaceLifecycleGrain
    (
        [<PersistentState("workspaceRoute", "sessionStore")>] persistentState: IPersistentState<WorkspaceRouteState>,
        catalog: WorkspaceCatalog,
        principalAccessor: Func<SecurityPrincipal>,
        auditLog: AuditLog
    ) as this =
    inherit Grain()

    let address () =
        RuntimeAddress.tryParseWorkspaceRouteKey (this.GetPrimaryKeyString())
        |> Option.defaultWith (fun () ->
            raise (UnauthorizedAccessException "Invalid tenant-scoped workspace route grain key."))

    let principalAndAddress () =
        let routeAddress = address ()
        let principal = principalAccessor.Invoke()

        if
            SecurityPrincipal.tenantId principal
            <> RuntimeAddress.workspaceRouteTenant routeAddress
        then
            raise (UnauthorizedAccessException "Workspace lifecycle access cannot cross tenant boundaries.")

        principal, routeAddress

    let administratorAndAddress () =
        let principal, routeAddress = principalAndAddress ()
        WorkspaceLifecycleAuthorization.authorize routeAddress principal
        principal, routeAddress

    let initializeState routeAddress =
        if not persistentState.RecordExists then
            persistentState.State.TenantId <- routeAddress |> RuntimeAddress.workspaceRouteTenant |> TenantId.value

            persistentState.State.WorkspaceName <- RuntimeAddress.workspaceRouteName routeAddress

    let auditAsync routeAddress (audit: WorkspaceLifecycleAuditRecord) =
        let versionId = WorkspaceVersionId.parse audit.VersionId
        let workspaceId = WorkspaceVersionId.workspaceId versionId

        let principal =
            SecurityPrincipal.create (RuntimeAddress.workspaceRouteTenant routeAddress) (UserId.parse audit.ActorId) []

        let scope =
            AuthorizationScope.tryCreate principal None workspaceId None
            |> Option.defaultWith (fun () ->
                raise (UnauthorizedAccessException "Workspace lifecycle audit scope is invalid."))

        let entry =
            { Id = audit.Id
              Timestamp = audit.RecordedAt
              AgentId = audit.ActorId
              Action = AuditAction.LifecycleTransition(audit.FromState, audit.ToState)
              Input = None
              Output = None
              Permitted = true
              Decision = PermissionDecision.Allow
              PermissionEvidence = None
              PolicyEvidence = None
              ConstitutionEvidence = None
              ExecutionId = None
              Metadata =
                Map.ofList
                    [ "action", audit.Action
                      "tenant_id", routeAddress |> RuntimeAddress.workspaceRouteTenant |> TenantId.value
                      "workspace_name", RuntimeAddress.workspaceRouteName routeAddress
                      "workspace_version", WorkspaceVersionId.value versionId
                      "route_revision", string audit.Revision ] }

        auditLog.RecordAsync scope entry

    let flushPendingAuditsAsync routeAddress =
        task {
            let pending = persistentState.State.PendingAudits |> Seq.toList

            for audit in pending do
                do! auditAsync routeAddress audit
                persistentState.State.PendingAudits.Remove audit |> ignore
                do! persistentState.WriteStateAsync()
        }

    let mutateAsync versionId action fromState toState mutation =
        task {
            let principal, routeAddress = administratorAndAddress ()
            initializeState routeAddress
            do! flushPendingAuditsAsync routeAddress
            let candidate = WorkspaceRouting.clone persistentState.State
            mutation candidate
            let actorId = principal |> SecurityPrincipal.subjectId |> UserId.value

            candidate.PendingAudits.Add(
                WorkspaceLifecycleAuditRecord(
                    Id = Guid.NewGuid(),
                    VersionId = WorkspaceVersionId.value versionId,
                    Action = action,
                    FromState = fromState,
                    ToState = toState,
                    Revision = candidate.Revision,
                    ActorId = actorId,
                    RecordedAt = DateTimeOffset.UtcNow
                )
            )

            persistentState.State <- candidate
            do! persistentState.WriteStateAsync()
            do! flushPendingAuditsAsync routeAddress
        }

    interface IWorkspaceLifecycleGrain with
        member _.RegisterVersionAsync(versionId, expectedRevision) =
            let identity = WorkspaceVersionId.parse versionId
            let principal, routeAddress = administratorAndAddress ()
            initializeState routeAddress

            let registration =
                catalog.Get(RuntimeAddress.workspaceRouteTenant routeAddress, identity)

            let actor = principal |> SecurityPrincipal.subjectId |> UserId.value

            mutateAsync
                identity
                "register"
                "unregistered"
                (WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Staged)
                (WorkspaceRouting.register expectedRevision actor DateTimeOffset.UtcNow registration)

        member _.SetCanaryAsync(versionId, percentage, expectedRevision) =
            let identity = WorkspaceVersionId.parse versionId

            mutateAsync
                identity
                "canary"
                (WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Staged)
                (WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Canary)
                (WorkspaceRouting.setCanary expectedRevision identity percentage)

        member _.PromoteAsync(versionId, expectedRevision) =
            let identity = WorkspaceVersionId.parse versionId

            mutateAsync
                identity
                "promote"
                "routable"
                (WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Active)
                (WorkspaceRouting.promote expectedRevision identity)

        member _.RollbackAsync(versionId, expectedRevision) =
            let identity = WorkspaceVersionId.parse versionId

            mutateAsync
                identity
                "rollback"
                "previously-active"
                (WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Active)
                (WorkspaceRouting.rollback expectedRevision identity)

        member _.RetireAsync(versionId, expectedRevision) =
            let identity = WorkspaceVersionId.parse versionId

            mutateAsync
                identity
                "retire"
                "inactive"
                (WorkspaceVersionLifecycle.serialize WorkspaceVersionLifecycle.Retired)
                (WorkspaceRouting.retire expectedRevision identity)

        member _.ResolveAsync(stickinessKey) =
            task {
                let _, routeAddress = principalAndAddress ()
                initializeState routeAddress
                do! flushPendingAuditsAsync routeAddress

                return
                    persistentState.State
                    |> WorkspaceRouting.resolve stickinessKey
                    |> WorkspaceVersionId.value
            }

        member _.GetAsync() =
            task {
                let _, routeAddress = principalAndAddress ()
                initializeState routeAddress
                do! flushPendingAuditsAsync routeAddress
                return WorkspaceRouting.clone persistentState.State
            }

    override _.OnActivateAsync(cancellationToken) =
        let baseActivation = base.OnActivateAsync(cancellationToken)

        task {
            let routeAddress = address ()
            initializeState routeAddress
            do! flushPendingAuditsAsync routeAddress
            do! baseActivation
        }
