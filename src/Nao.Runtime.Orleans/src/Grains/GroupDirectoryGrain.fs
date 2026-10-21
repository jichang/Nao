namespace Nao.Runtime.Orleans.Grains

open System
open System.Threading.Tasks
open Nao.Agents
open Nao.Runtime.Orleans
open Orleans
open Orleans.Runtime

[<RequireQualifiedAccess>]
type GroupRole =
    | Owner
    | Administrator
    | Member

[<RequireQualifiedAccess>]
module GroupRole =
    let serialize =
        function
        | GroupRole.Owner -> "owner"
        | GroupRole.Administrator -> "administrator"
        | GroupRole.Member -> "member"

    let parse =
        function
        | "owner" -> GroupRole.Owner
        | "administrator" -> GroupRole.Administrator
        | "member" -> GroupRole.Member
        | value -> invalidArg (nameof value) "Unknown group role."

[<RequireQualifiedAccess>]
type GroupLifecycle =
    | Active
    | Suspended
    | Retired

[<RequireQualifiedAccess>]
module GroupLifecycle =
    let serialize =
        function
        | GroupLifecycle.Active -> "active"
        | GroupLifecycle.Suspended -> "suspended"
        | GroupLifecycle.Retired -> "retired"

[<GenerateSerializer>]
type GroupQuotaRecord() =
    [<Id(0u)>]
    member val MaximumSessions: int = 1 with get, set

    [<Id(1u)>]
    member val MaximumConcurrentExecutions: int = 1 with get, set

    [<Id(2u)>]
    member val MaximumMonthlyTokens: int64 = 1L with get, set

[<GenerateSerializer>]
type GroupMemberRecord() =
    [<Id(0u)>]
    member val TenantId: string = "" with get, set

    [<Id(1u)>]
    member val GroupId: string = "" with get, set

    [<Id(2u)>]
    member val UserId: string = "" with get, set

    [<Id(3u)>]
    member val Role: string = "" with get, set

[<GenerateSerializer>]
type GroupDirectoryState() =
    [<Id(0u)>]
    member val TenantId: string = "" with get, set

    [<Id(1u)>]
    member val GroupId: string = "" with get, set

    [<Id(2u)>]
    member val DefaultWorkspaceId: string = "" with get, set

    [<Id(3u)>]
    member val Lifecycle: string = "" with get, set

    [<Id(4u)>]
    member val Quota: GroupQuotaRecord = GroupQuotaRecord() with get, set

    [<Id(5u)>]
    member val Members: ResizeArray<GroupMemberRecord> = ResizeArray() with get, set

    [<Id(6u)>]
    member val SchemaVersion: int = 1 with get, set

[<RequireQualifiedAccess>]
module GroupGovernance =
    let validateQuota (quota: GroupQuotaRecord) =
        if
            quota.MaximumSessions <= 0
            || quota.MaximumConcurrentExecutions <= 0
            || quota.MaximumMonthlyTokens <= 0L
        then
            invalidArg (nameof quota) "Every group quota must be positive."

    let configure
        (address: RuntimeGroupAddress)
        (principal: SecurityPrincipal)
        (defaultWorkspaceId: string)
        (quota: GroupQuotaRecord)
        =
        validateQuota quota

        let workspaceId =
            defaultWorkspaceId |> WorkspaceVersionId.parse |> WorkspaceVersionId.workspaceId

        let state = GroupDirectoryState()
        state.TenantId <- address |> RuntimeAddress.groupTenant |> TenantId.value
        state.GroupId <- address |> RuntimeAddress.groupId |> GroupId.value
        state.DefaultWorkspaceId <- WorkspaceId.value workspaceId
        state.Lifecycle <- GroupLifecycle.serialize GroupLifecycle.Active
        state.Quota <- quota
        let owner = GroupMemberRecord()
        owner.TenantId <- state.TenantId
        owner.GroupId <- state.GroupId
        owner.UserId <- principal |> SecurityPrincipal.userId |> UserId.value
        owner.Role <- GroupRole.serialize GroupRole.Owner
        state.Members.Add owner
        state

    let isAuthorizedMember (address: RuntimeGroupAddress) (principal: SecurityPrincipal) (state: GroupDirectoryState) =
        let tenantId = address |> RuntimeAddress.groupTenant
        let groupId = address |> RuntimeAddress.groupId
        let tenantValue = TenantId.value tenantId
        let groupValue = GroupId.value groupId
        let userValue = principal |> SecurityPrincipal.userId |> UserId.value

        SecurityPrincipal.tenantId principal = tenantId
        && (SecurityPrincipal.groupIds principal |> Set.contains groupId)
        && state.TenantId = tenantValue
        && state.GroupId = groupValue
        && (state.Members
            |> Seq.forall (fun memberRecord -> memberRecord.TenantId = tenantValue && memberRecord.GroupId = groupValue))
        && (state.Members
            |> Seq.exists (fun memberRecord -> memberRecord.UserId = userValue))

    let canAdminister (principal: SecurityPrincipal) (state: GroupDirectoryState) =
        let userId = SecurityPrincipal.userId principal |> UserId.value

        state.Members
        |> Seq.exists (fun memberRecord ->
            memberRecord.UserId = userId
            && (memberRecord.Role = GroupRole.serialize GroupRole.Owner
                || memberRecord.Role = GroupRole.serialize GroupRole.Administrator))

type IGroupDirectoryGrain =
    inherit IGrainWithStringKey

    abstract member ConfigureAsync: defaultWorkspaceId: string * quota: GroupQuotaRecord -> Task
    abstract member AddMemberAsync: userId: string * role: string -> Task
    abstract member RemoveMemberAsync: userId: string -> Task
    abstract member SetDefaultWorkspaceAsync: workspaceId: string -> Task
    abstract member SetQuotaAsync: quota: GroupQuotaRecord -> Task
    abstract member SetLifecycleAsync: lifecycle: string -> Task
    abstract member GetAsync: unit -> Task<GroupDirectoryState>

type GroupDirectoryGrain
    (
        [<PersistentState("groupDirectory", "sessionStore")>] persistentState: IPersistentState<GroupDirectoryState>,
        principalAccessor: Func<SecurityPrincipal>
    ) as this =
    inherit Grain()

    let address () =
        RuntimeAddress.tryParseGroupKey (this.GetPrimaryKeyString())
        |> Option.defaultWith (fun () -> raise (UnauthorizedAccessException "Invalid tenant-scoped group grain key."))

    let requirePrincipal () =
        let groupAddress = address ()
        let principal = principalAccessor.Invoke()
        let groupId = RuntimeAddress.groupId groupAddress

        if
            SecurityPrincipal.tenantId principal <> RuntimeAddress.groupTenant groupAddress
            || not (SecurityPrincipal.groupIds principal |> Set.contains groupId)
        then
            raise (UnauthorizedAccessException "The authenticated principal is not authorized for this group.")

        if
            persistentState.RecordExists
            && not (GroupGovernance.isAuthorizedMember groupAddress principal persistentState.State)
        then
            raise (
                UnauthorizedAccessException
                    "The persisted group owner or membership does not match the authenticated group scope."
            )

        principal, groupAddress

    let requireAdministrator () =
        let principal, groupAddress = requirePrincipal ()

        if not (GroupGovernance.canAdminister principal persistentState.State) then
            raise (UnauthorizedAccessException "Group administration requires owner or administrator role.")

        groupAddress

    interface IGroupDirectoryGrain with
        member _.ConfigureAsync(defaultWorkspaceId, quota) =
            task {
                let principal, groupAddress = requirePrincipal ()

                if persistentState.RecordExists then
                    raise (InvalidOperationException "Group is already configured.")

                persistentState.State <- GroupGovernance.configure groupAddress principal defaultWorkspaceId quota

                do! persistentState.WriteStateAsync()
            }

        member _.AddMemberAsync(userId, role) =
            task {
                let groupAddress = requireAdministrator ()
                let parsedUserId = UserId.parse userId
                let parsedRole = GroupRole.parse role

                if
                    persistentState.State.Members
                    |> Seq.exists (fun memberRecord -> memberRecord.UserId = UserId.value parsedUserId)
                then
                    invalidArg (nameof userId) "Group member already exists."

                let memberRecord = GroupMemberRecord()
                memberRecord.TenantId <- groupAddress |> RuntimeAddress.groupTenant |> TenantId.value
                memberRecord.GroupId <- groupAddress |> RuntimeAddress.groupId |> GroupId.value
                memberRecord.UserId <- UserId.value parsedUserId
                memberRecord.Role <- GroupRole.serialize parsedRole
                persistentState.State.Members.Add memberRecord
                do! persistentState.WriteStateAsync()
            }

        member _.RemoveMemberAsync(userId) =
            task {
                requireAdministrator () |> ignore

                let index =
                    persistentState.State.Members
                    |> Seq.tryFindIndex (fun memberRecord -> memberRecord.UserId = userId)

                match index with
                | None -> ()
                | Some value when persistentState.State.Members.[value].Role = GroupRole.serialize GroupRole.Owner ->
                    invalidOp "The group owner cannot be removed."
                | Some value ->
                    persistentState.State.Members.RemoveAt value
                    do! persistentState.WriteStateAsync()
            }

        member _.SetDefaultWorkspaceAsync(workspaceId) =
            task {
                requireAdministrator () |> ignore

                persistentState.State.DefaultWorkspaceId <-
                    workspaceId |> WorkspaceVersionId.parse |> WorkspaceVersionId.value

                do! persistentState.WriteStateAsync()
            }

        member _.SetQuotaAsync(quota) =
            task {
                requireAdministrator () |> ignore
                GroupGovernance.validateQuota quota
                persistentState.State.Quota <- quota
                do! persistentState.WriteStateAsync()
            }

        member _.SetLifecycleAsync(lifecycle) =
            task {
                requireAdministrator () |> ignore

                persistentState.State.Lifecycle <-
                    match lifecycle with
                    | "active" -> GroupLifecycle.serialize GroupLifecycle.Active
                    | "suspended" -> GroupLifecycle.serialize GroupLifecycle.Suspended
                    | "retired" -> GroupLifecycle.serialize GroupLifecycle.Retired
                    | _ -> invalidArg (nameof lifecycle) "Unknown group lifecycle."

                do! persistentState.WriteStateAsync()
            }

        member _.GetAsync() =
            requirePrincipal () |> ignore
            Task.FromResult persistentState.State
