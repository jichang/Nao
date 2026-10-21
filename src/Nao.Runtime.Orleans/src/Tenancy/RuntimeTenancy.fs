namespace Nao.Runtime.Orleans

open System
open System.Text
open Nao.Agents

/// Immutable logical and version identity for compiled workspace code.
[<Struct>]
type WorkspaceVersionId =
    private
        { Name: string
          Version: string }

module WorkspaceVersionId =
    let private required fieldName value =
        if
            String.IsNullOrWhiteSpace value
            || value <> value.Trim()
            || value.Contains('/')
            || value.Contains('@')
        then
            invalidArg fieldName "Workspace identity components must be non-blank and cannot contain '/' or '@'."

        value

    let create name version =
        { Name = required (nameof name) name
          Version = required (nameof version) version }

    let name identity = identity.Name
    let version identity = identity.Version
    let value identity = $"{identity.Name}@{identity.Version}"
    let workspaceId identity = identity |> value |> WorkspaceId.parse

    let parse value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg (nameof value) "Workspace version identity must be non-blank with no surrounding whitespace."

        match value.Split('@') with
        | [| name; version |] -> create name version
        | _ -> invalidArg (nameof value) "Workspace version identity must have the canonical '<name>@<version>' shape."

    let tryParse value =
        try
            Some(parse value)
        with :? ArgumentException ->
            None

type RuntimeSessionAddress =
    private
        { TenantId: TenantId
          WorkspaceId: WorkspaceId
          UserId: UserId
          SessionId: SessionId }

type RuntimeDirectoryAddress =
    private
        { TenantId: TenantId
          WorkspaceId: WorkspaceId
          UserId: UserId }

type RuntimeGroupAddress =
    private
        { TenantId: TenantId
          GroupId: GroupId }

type RuntimeWorkspaceRouteAddress =
    private
        { TenantId: TenantId
          WorkspaceName: string }

type RuntimeExecutionAddress =
    { Session: RuntimeSessionAddress
      ExecutionId: ExecutionId }

[<RequireQualifiedAccess>]
module RuntimeAddress =
    let private encode (value: string) =
        Convert.ToBase64String(Encoding.UTF8.GetBytes value).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private decode (value: string) =
        let normalized = value.Replace('-', '+').Replace('_', '/')

        let padded =
            normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=')

        try
            Convert.FromBase64String padded |> Encoding.UTF8.GetString |> Some
        with :? FormatException ->
            None

    let private split expected (key: string) =
        if isNull key then
            None
        else
            let segments = key.Split('/')

            if segments.Length <> expected + 1 || segments.[0] <> "v1" then
                None
            else
                segments
                |> Array.skip 1
                |> Array.fold
                    (fun decoded segment ->
                        match decoded, decode segment with
                        | Some values, Some value -> Some(value :: values)
                        | _ -> None)
                    (Some [])
                |> Option.map List.rev

    let session
        (tenantId: TenantId)
        (workspaceId: WorkspaceId)
        (userId: UserId)
        (sessionId: SessionId)
        : RuntimeSessionAddress =
        WorkspaceId.value workspaceId |> WorkspaceVersionId.parse |> ignore

        { TenantId = tenantId
          WorkspaceId = workspaceId
          UserId = userId
          SessionId = sessionId }

    let directory (tenantId: TenantId) (workspaceId: WorkspaceId) (userId: UserId) : RuntimeDirectoryAddress =
        WorkspaceId.value workspaceId |> WorkspaceVersionId.parse |> ignore

        { TenantId = tenantId
          WorkspaceId = workspaceId
          UserId = userId }

    let group (tenantId: TenantId) (groupId: GroupId) : RuntimeGroupAddress =
        { TenantId = tenantId
          GroupId = groupId }

    let workspaceRoute (tenantId: TenantId) (workspaceName: string) : RuntimeWorkspaceRouteAddress =
        if
            String.IsNullOrWhiteSpace workspaceName
            || workspaceName <> workspaceName.Trim()
            || workspaceName.Contains('/')
            || workspaceName.Contains('@')
        then
            invalidArg (nameof workspaceName) "Workspace route names must be non-blank and cannot contain '/' or '@'."

        { TenantId = tenantId
          WorkspaceName = workspaceName }

    let sessionFromScope scope =
        AuthorizationScope.sessionId scope
        |> Option.map (
            session
                (AuthorizationScope.tenantId scope)
                (AuthorizationScope.workspaceId scope)
                (AuthorizationScope.userId scope)
        )

    let directoryFromScope scope =
        directory
            (AuthorizationScope.tenantId scope)
            (AuthorizationScope.workspaceId scope)
            (AuthorizationScope.userId scope)

    let sessionKey (address: RuntimeSessionAddress) =
        [ TenantId.value address.TenantId
          WorkspaceId.value address.WorkspaceId
          UserId.value address.UserId
          SessionId.value address.SessionId ]
        |> List.map encode
        |> String.concat "/"
        |> sprintf "v1/%s"

    let directoryKey (address: RuntimeDirectoryAddress) =
        [ TenantId.value address.TenantId
          WorkspaceId.value address.WorkspaceId
          UserId.value address.UserId ]
        |> List.map encode
        |> String.concat "/"
        |> sprintf "v1/%s"

    let groupKey (address: RuntimeGroupAddress) =
        [ TenantId.value address.TenantId; GroupId.value address.GroupId ]
        |> List.map encode
        |> String.concat "/"
        |> sprintf "v1/%s"

    let workspaceRouteKey (address: RuntimeWorkspaceRouteAddress) =
        [ TenantId.value address.TenantId; address.WorkspaceName ]
        |> List.map encode
        |> String.concat "/"
        |> sprintf "v1/%s"

    let tryParseSessionKey key =
        match split 4 key with
        | Some [ tenant; workspace; user; sessionValue ] ->
            match
                TenantId.tryParse tenant,
                WorkspaceId.tryParse workspace,
                UserId.tryParse user,
                SessionId.tryParse sessionValue
            with
            | Some tenantId, Some workspaceId, Some userId, Some sessionId when
                WorkspaceId.value workspaceId |> WorkspaceVersionId.tryParse |> Option.isSome
                ->
                Some(session tenantId workspaceId userId sessionId)
            | _ -> None
        | _ -> None

    let tryParseDirectoryKey key =
        match split 3 key with
        | Some [ tenant; workspace; user ] ->
            match TenantId.tryParse tenant, WorkspaceId.tryParse workspace, UserId.tryParse user with
            | Some tenantId, Some workspaceId, Some userId when
                WorkspaceId.value workspaceId |> WorkspaceVersionId.tryParse |> Option.isSome
                ->
                Some(directory tenantId workspaceId userId)
            | _ -> None
        | _ -> None

    let tryParseGroupKey key =
        match split 2 key with
        | Some [ tenant; groupValue ] ->
            match TenantId.tryParse tenant, GroupId.tryParse groupValue with
            | Some tenantId, Some groupId -> Some(group tenantId groupId)
            | _ -> None
        | _ -> None

    let tryParseWorkspaceRouteKey key =
        match split 2 key with
        | Some [ tenant; workspaceName ] ->
            match TenantId.tryParse tenant with
            | Some tenantId ->
                try
                    Some(workspaceRoute tenantId workspaceName)
                with :? ArgumentException ->
                    None
            | None -> None
        | _ -> None

    let sessionTenant (address: RuntimeSessionAddress) = address.TenantId
    let sessionWorkspace (address: RuntimeSessionAddress) = address.WorkspaceId
    let sessionUser (address: RuntimeSessionAddress) = address.UserId
    let sessionId (address: RuntimeSessionAddress) = address.SessionId
    let directoryTenant (address: RuntimeDirectoryAddress) = address.TenantId
    let directoryWorkspace (address: RuntimeDirectoryAddress) = address.WorkspaceId
    let directoryUser (address: RuntimeDirectoryAddress) = address.UserId
    let groupTenant (address: RuntimeGroupAddress) = address.TenantId
    let groupId (address: RuntimeGroupAddress) = address.GroupId
    let workspaceRouteTenant (address: RuntimeWorkspaceRouteAddress) = address.TenantId
    let workspaceRouteName (address: RuntimeWorkspaceRouteAddress) = address.WorkspaceName

    let scopeMatchesSession scope (address: RuntimeSessionAddress) =
        AuthorizationScope.tenantId scope = address.TenantId
        && AuthorizationScope.workspaceId scope = address.WorkspaceId
        && AuthorizationScope.userId scope = address.UserId
        && AuthorizationScope.sessionId scope = Some address.SessionId

    let principalMatchesDirectory principal (address: RuntimeDirectoryAddress) =
        SecurityPrincipal.tenantId principal = address.TenantId
        && SecurityPrincipal.userId principal = address.UserId

type PersistedOwnerScope =
    { TenantId: string
      WorkspaceId: string
      UserId: string
      SessionId: string option }

[<RequireQualifiedAccess>]
module PersistedOwnerScope =
    let session address =
        { TenantId = address |> RuntimeAddress.sessionTenant |> TenantId.value
          WorkspaceId = address |> RuntimeAddress.sessionWorkspace |> WorkspaceId.value
          UserId = address |> RuntimeAddress.sessionUser |> UserId.value
          SessionId = Some(address |> RuntimeAddress.sessionId |> SessionId.value) }

    let directory address =
        { TenantId = address |> RuntimeAddress.directoryTenant |> TenantId.value
          WorkspaceId = address |> RuntimeAddress.directoryWorkspace |> WorkspaceId.value
          UserId = address |> RuntimeAddress.directoryUser |> UserId.value
          SessionId = None }

    let fromAuthorization scope =
        { TenantId = scope |> AuthorizationScope.tenantId |> TenantId.value
          WorkspaceId = scope |> AuthorizationScope.workspaceId |> WorkspaceId.value
          UserId = scope |> AuthorizationScope.userId |> UserId.value
          SessionId = scope |> AuthorizationScope.sessionId |> Option.map SessionId.value }

    let matchesSession address owner = owner = session address
    let matchesDirectory address owner = owner = directory address
