namespace Nao.Agents

open System

module private TextIdentity =
    let tryParse constructor value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            None
        else
            Some(constructor value)

    let parse name tryParse value =
        tryParse value
        |> Option.defaultWith (fun () ->
            invalidArg name "Identity must be non-blank and have no surrounding whitespace.")

    let generate constructor () =
        Guid.NewGuid().ToString("N") |> constructor

[<Struct>]
type TenantId = private TenantId of string

[<Struct>]
type GroupId = private GroupId of string

[<Struct>]
type UserId = private UserId of string

[<Struct>]
type WorkspaceId = private WorkspaceId of string

[<Struct>]
type SessionId = private SessionId of string

[<Struct>]
type TurnId = private TurnId of string

[<Struct>]
type ArtifactId = private ArtifactId of Guid

[<Struct>]
type SourceId = private SourceId of string

[<Struct>]
type ExecutionId = private ExecutionId of Guid

[<Struct>]
type CorrelationId = private CorrelationId of Guid

module TenantId =
    let tryParse = TextIdentity.tryParse TenantId

    let parse value =
        TextIdentity.parse (nameof value) tryParse value

    let value (TenantId value) = value

module GroupId =
    let tryParse = TextIdentity.tryParse GroupId

    let parse value =
        TextIdentity.parse (nameof value) tryParse value

    let value (GroupId value) = value

module UserId =
    let tryParse = TextIdentity.tryParse UserId

    let parse value =
        TextIdentity.parse (nameof value) tryParse value

    let value (UserId value) = value

module WorkspaceId =
    let tryParse = TextIdentity.tryParse WorkspaceId

    let parse value =
        TextIdentity.parse (nameof value) tryParse value

    let value (WorkspaceId value) = value
    let create = parse
    let defaultId = parse "default"
    let versioned key version = parse (sprintf "%s@%s" key version)

module SessionId =
    let tryParse = TextIdentity.tryParse SessionId

    let parse value =
        TextIdentity.parse (nameof value) tryParse value

    let value (SessionId value) = value

module TurnId =
    let tryParse = TextIdentity.tryParse TurnId

    let parse value =
        TextIdentity.parse (nameof value) tryParse value

    let value (TurnId value) = value
    let generate = TextIdentity.generate TurnId

module ArtifactId =
    let generate () = ArtifactId(Guid.NewGuid())

    let tryParse (value: string) =
        match Guid.TryParse value with
        | true, id -> Some(ArtifactId id)
        | _ -> None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () -> invalidArg (nameof value) "Invalid artifact ID.")

    let value (ArtifactId value) = value
    let serialize = value >> _.ToString("D")

module SourceId =
    let tryParse = TextIdentity.tryParse SourceId

    let parse value =
        TextIdentity.parse (nameof value) tryParse value

    let value (SourceId value) = value

module ExecutionId =
    let generate () = ExecutionId(Guid.NewGuid())
    let ofGuid value = ExecutionId value
    let value (ExecutionId value) = value

    let tryParse (value: string) =
        match Guid.TryParse value with
        | true, id -> Some(ExecutionId id)
        | _ -> None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () -> invalidArg (nameof value) "Invalid execution ID.")

    let serialize = value >> _.ToString("D")

module CorrelationId =
    let generate () = CorrelationId(Guid.NewGuid())
    let value (CorrelationId value) = value

    let tryParse (value: string) =
        match Guid.TryParse value with
        | true, id -> Some(CorrelationId id)
        | _ -> None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () -> invalidArg (nameof value) "Invalid correlation ID.")

    let serialize = value >> _.ToString("D")

type CorrelationContext =
    { ExecutionId: ExecutionId
      CorrelationId: CorrelationId
      CausationId: ExecutionId option
      Attempt: int }

module CorrelationContext =
    let root () =
        { ExecutionId = ExecutionId.generate ()
          CorrelationId = CorrelationId.generate ()
          CausationId = None
          Attempt = 1 }

    let delegateFrom parent =
        { ExecutionId = ExecutionId.generate ()
          CorrelationId = parent.CorrelationId
          CausationId = Some parent.ExecutionId
          Attempt = 1 }

    let retry previous =
        { ExecutionId = ExecutionId.generate ()
          CorrelationId = previous.CorrelationId
          CausationId = Some previous.ExecutionId
          Attempt = previous.Attempt + 1 }

/// Host-authenticated identity. Callers cannot replace its tenant or user with request data.
type SecurityPrincipal =
    private
        { TenantId: TenantId
          UserId: UserId
          GroupIds: Set<GroupId> }

/// The complete authorization lineage for one workspace or session operation.
type AuthorizationScope =
    private
        { TenantId: TenantId
          GroupId: GroupId option
          UserId: UserId
          WorkspaceId: WorkspaceId
          SessionId: SessionId option }

module SecurityPrincipal =
    let create tenantId userId groupIds =
        { TenantId = tenantId
          UserId = userId
          GroupIds = Set.ofSeq groupIds }

    let tenantId (principal: SecurityPrincipal) = principal.TenantId
    let userId (principal: SecurityPrincipal) = principal.UserId
    let groupIds (principal: SecurityPrincipal) = principal.GroupIds

module AuthorizationScope =
    let tryCreate principal groupId workspaceId sessionId =
        let groupAllowed =
            groupId
            |> Option.forall (fun candidate -> SecurityPrincipal.groupIds principal |> Set.contains candidate)

        if groupAllowed then
            Some
                { TenantId = SecurityPrincipal.tenantId principal
                  GroupId = groupId
                  UserId = SecurityPrincipal.userId principal
                  WorkspaceId = workspaceId
                  SessionId = sessionId }
        else
            None

    let tenantId (scope: AuthorizationScope) = scope.TenantId
    let groupId (scope: AuthorizationScope) = scope.GroupId
    let userId (scope: AuthorizationScope) = scope.UserId
    let workspaceId (scope: AuthorizationScope) = scope.WorkspaceId
    let sessionId (scope: AuthorizationScope) = scope.SessionId

    /// Fail closed unless every authorization-bearing scope component is identical.
    let contains granted requested = granted = requested
