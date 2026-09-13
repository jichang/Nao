namespace Nao.Agents

open System
open System.Security.Cryptography
open System.Text

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
type ServiceId = private ServiceId of string

[<Struct>]
type WorkspaceId = private WorkspaceId of string

[<Struct>]
type SessionId = private SessionId of string

[<Struct>]
type TurnId = private TurnId of string

[<Struct>]
type ArtifactId = private ArtifactId of Guid

[<Struct>]
type IdempotencyKey = private IdempotencyKey of string

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

module ServiceId =
    let tryParse = TextIdentity.tryParse ServiceId

    let parse value =
        TextIdentity.parse (nameof value) tryParse value

    let value (ServiceId value) = value

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

/// Provider-neutral strength asserted by the host authentication boundary.
[<RequireQualifiedAccess>]
type AuthenticationStrength =
    | Unspecified
    | SingleFactor
    | MultiFactor
    | PhishingResistant

/// A host-authorized switch from the authenticated user to an effective user.
type UserImpersonation =
    { EffectiveUserId: UserId
      Reason: string }

/// Host-authenticated identity. Callers cannot replace its tenant or subject with request data.
type SecurityPrincipal =
    private
        { TenantId: TenantId
          SubjectId: UserId
          Impersonation: UserImpersonation option
          ServiceId: ServiceId option
          ServiceDelegations: ServiceId list
          GroupIds: Set<GroupId>
          Roles: Set<string>
          Claims: Map<string, string>
          AuthenticationStrength: AuthenticationStrength }

/// Persistable resource ownership, without authentication evidence. This is not an authorization capability.
type AuthorizationIdentity =
    { TenantId: TenantId
      GroupId: GroupId option
      UserId: UserId
      WorkspaceId: WorkspaceId
      SessionId: SessionId option }

/// One agent execution acting under the original authenticated principal.
type AgentAuthority =
    { AgentId: string
      ExecutionId: ExecutionId }

module AuthorizationIdentity =
    /// Fixed-order JSON preserves optional fields and delimiters before hashing the storage partition.
    let storageKey (identity: AuthorizationIdentity) =
        [| TenantId.value identity.TenantId
           UserId.value identity.UserId
           identity.GroupId |> Option.map GroupId.value |> Option.toObj
           WorkspaceId.value identity.WorkspaceId
           identity.SessionId |> Option.map SessionId.value |> Option.toObj |]
        |> System.Text.Json.JsonSerializer.Serialize
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

/// The complete authorization lineage for one workspace or session operation.
type AuthorizationScope =
    private
        { Identity: AuthorizationIdentity
          Principal: SecurityPrincipal
          AgentLineage: AgentAuthority list }

module SecurityPrincipal =
    let private createCore
        tenantId
        subjectId
        impersonation
        serviceId
        serviceDelegations
        groupIds
        roles
        claims
        authenticationStrength
        =
        let normalizedRoles = Set.ofSeq roles

        if
            normalizedRoles
            |> Set.exists (fun role -> String.IsNullOrWhiteSpace role || role <> role.Trim())
        then
            invalidArg (nameof roles) "Principal roles must be non-blank and have no surrounding whitespace."

        if
            claims
            |> Map.exists (fun claim value -> String.IsNullOrWhiteSpace claim || claim <> claim.Trim() || isNull value)
        then
            invalidArg (nameof claims) "Principal claim names must be canonical and claim values must be non-null."

        { TenantId = tenantId
          SubjectId = subjectId
          Impersonation = impersonation
          ServiceId = serviceId
          ServiceDelegations = serviceDelegations
          GroupIds = Set.ofSeq groupIds
          Roles = normalizedRoles
          Claims = claims
          AuthenticationStrength = authenticationStrength }

    let create tenantId userId groupIds =
        createCore tenantId userId None None [] groupIds [] Map.empty AuthenticationStrength.Unspecified

    let createAuthenticated tenantId subjectId serviceId groupIds roles claims authenticationStrength =
        createCore tenantId subjectId None serviceId [] groupIds roles claims authenticationStrength

    let createServiceAuthenticated tenantId subjectId serviceLineage groupIds roles claims authenticationStrength =
        match serviceLineage with
        | [] -> invalidArg (nameof serviceLineage) "Service authentication requires a root service identity."
        | _ when serviceLineage |> List.distinct |> List.length <> serviceLineage.Length ->
            invalidArg (nameof serviceLineage) "Service authority lineage cannot repeat a service identity."
        | serviceId :: serviceDelegations ->
            createCore
                tenantId
                subjectId
                None
                (Some serviceId)
                serviceDelegations
                groupIds
                roles
                claims
                authenticationStrength

    /// Hosts may mint impersonation only after independently authorizing the original user.
    let createImpersonated tenantId subjectId effectiveUserId reason groupIds roles claims authenticationStrength =
        if subjectId = effectiveUserId then
            invalidArg (nameof effectiveUserId) "Impersonation must change the effective user."

        if String.IsNullOrWhiteSpace reason || reason <> reason.Trim() then
            invalidArg (nameof reason) "Impersonation requires a canonical non-blank reason."

        if authenticationStrength = AuthenticationStrength.Unspecified then
            invalidArg (nameof authenticationStrength) "Impersonation requires authenticated host evidence."

        createCore
            tenantId
            subjectId
            (Some
                { EffectiveUserId = effectiveUserId
                  Reason = reason })
            None
            []
            groupIds
            roles
            claims
            authenticationStrength

    let tenantId (principal: SecurityPrincipal) = principal.TenantId
    let subjectId (principal: SecurityPrincipal) = principal.SubjectId
    let impersonation (principal: SecurityPrincipal) = principal.Impersonation

    let userId (principal: SecurityPrincipal) =
        principal.Impersonation
        |> Option.map _.EffectiveUserId
        |> Option.defaultValue principal.SubjectId

    let serviceId (principal: SecurityPrincipal) = principal.ServiceId

    let serviceLineage (principal: SecurityPrincipal) =
        match principal.ServiceId with
        | Some serviceId -> serviceId :: principal.ServiceDelegations
        | None -> []

    let currentService = serviceLineage >> List.tryLast
    let groupIds (principal: SecurityPrincipal) = principal.GroupIds
    let roles (principal: SecurityPrincipal) = principal.Roles
    let claims (principal: SecurityPrincipal) = principal.Claims
    let authenticationStrength (principal: SecurityPrincipal) = principal.AuthenticationStrength

    let internal delegateService delegatingServiceId delegatedServiceId principal =
        match currentService principal with
        | None -> raise (UnauthorizedAccessException "Direct user authority cannot delegate to a service.")
        | Some current when current <> delegatingServiceId ->
            raise (UnauthorizedAccessException "Only the current service authority may delegate.")
        | Some _ when delegatingServiceId = delegatedServiceId ->
            raise (UnauthorizedAccessException "A service cannot delegate authority to itself.")
        | Some _ when serviceLineage principal |> List.contains delegatedServiceId ->
            raise (UnauthorizedAccessException "A service identity cannot be reused in its delegation lineage.")
        | Some _ ->
            { principal with
                ServiceDelegations = principal.ServiceDelegations @ [ delegatedServiceId ] }

    let internal contains (granted: SecurityPrincipal) (requested: SecurityPrincipal) =
        let rec isPrefix expected actual =
            match expected, actual with
            | [], _ -> true
            | expectedHead :: expectedTail, actualHead :: actualTail when expectedHead = actualHead ->
                isPrefix expectedTail actualTail
            | _ -> false

        granted.TenantId = requested.TenantId
        && granted.SubjectId = requested.SubjectId
        && granted.Impersonation = requested.Impersonation
        && granted.ServiceId = requested.ServiceId
        && granted.GroupIds = requested.GroupIds
        && granted.Roles = requested.Roles
        && granted.Claims = requested.Claims
        && granted.AuthenticationStrength = requested.AuthenticationStrength
        && isPrefix granted.ServiceDelegations requested.ServiceDelegations

module AuthorizationScope =
    let tryCreate principal groupId workspaceId sessionId =
        let identityValid =
            (SecurityPrincipal.tenantId principal
             |> TenantId.value
             |> TenantId.tryParse
             |> Option.isSome)
            && (SecurityPrincipal.userId principal
                |> UserId.value
                |> UserId.tryParse
                |> Option.isSome)
            && (WorkspaceId.value workspaceId |> WorkspaceId.tryParse |> Option.isSome)
            && (groupId |> Option.forall (GroupId.value >> GroupId.tryParse >> Option.isSome))
            && (sessionId
                |> Option.forall (SessionId.value >> SessionId.tryParse >> Option.isSome))

        let groupAllowed =
            groupId
            |> Option.forall (fun candidate -> SecurityPrincipal.groupIds principal |> Set.contains candidate)

        if identityValid && groupAllowed then
            Some
                { Identity =
                    { TenantId = SecurityPrincipal.tenantId principal
                      GroupId = groupId
                      UserId = SecurityPrincipal.userId principal
                      WorkspaceId = workspaceId
                      SessionId = sessionId }
                  Principal = principal
                  AgentLineage = [] }
        else
            None

    let principal (scope: AuthorizationScope) = scope.Principal
    let identity (scope: AuthorizationScope) = scope.Identity
    let tenantId (scope: AuthorizationScope) = scope.Identity.TenantId
    let groupId (scope: AuthorizationScope) = scope.Identity.GroupId
    let userId (scope: AuthorizationScope) = scope.Identity.UserId
    let workspaceId (scope: AuthorizationScope) = scope.Identity.WorkspaceId
    let sessionId (scope: AuthorizationScope) = scope.Identity.SessionId
    let agentLineage (scope: AuthorizationScope) = scope.AgentLineage
    let currentAgent (scope: AuthorizationScope) = scope.AgentLineage |> List.tryLast

    let delegateService delegatingServiceId delegatedServiceId scope =
        if not scope.AgentLineage.IsEmpty then
            raise (UnauthorizedAccessException "Service delegation must be completed before agent execution begins.")

        { scope with
            Principal = SecurityPrincipal.delegateService delegatingServiceId delegatedServiceId scope.Principal }

    let private validateAgent agentId =
        if String.IsNullOrWhiteSpace agentId || agentId <> agentId.Trim() then
            invalidArg (nameof agentId) "Agent identity must be non-blank and have no surrounding whitespace."

    let startAgent agentId executionId scope =
        validateAgent agentId

        if not scope.AgentLineage.IsEmpty then
            raise (UnauthorizedAccessException "A root agent cannot replace an existing delegated authority lineage.")

        { scope with
            AgentLineage =
                [ { AgentId = agentId
                    ExecutionId = executionId } ] }

    let delegateAgent delegatingAgentId delegatedAgentId executionId scope =
        validateAgent delegatingAgentId
        validateAgent delegatedAgentId

        match currentAgent scope with
        | None -> raise (UnauthorizedAccessException "Agent delegation requires an established caller authority.")
        | Some current when current.AgentId <> delegatingAgentId ->
            raise (UnauthorizedAccessException "Only the current agent authority may delegate.")
        | Some _ when delegatingAgentId = delegatedAgentId ->
            raise (UnauthorizedAccessException "An agent cannot delegate authority to itself.")
        | Some _ when
            scope.AgentLineage
            |> List.exists (fun authority -> authority.ExecutionId = executionId)
            ->
            raise (
                UnauthorizedAccessException "Delegated execution identity must be unique within its authority lineage."
            )
        | Some _ ->
            { scope with
                AgentLineage =
                    scope.AgentLineage
                    @ [ { AgentId = delegatedAgentId
                          ExecutionId = executionId } ] }

    let requireAgent agentId executionId scope =
        match currentAgent scope with
        | Some authority when authority.AgentId = agentId && authority.ExecutionId = executionId -> ()
        | _ -> raise (UnauthorizedAccessException "Agent execution does not match the current delegated authority.")

    let requireOwner owner scope =
        if identity scope <> owner then
            raise (
                UnauthorizedAccessException "Resource ownership does not match the authenticated authorization scope."
            )

    /// A delegated scope is contained only when ownership and authenticated evidence are
    /// unchanged and both service and agent authority lineages extend the granting scope.
    let contains granted requested =
        let rec isPrefix expected actual =
            match expected, actual with
            | [], _ -> true
            | expectedHead :: expectedTail, actualHead :: actualTail when expectedHead = actualHead ->
                isPrefix expectedTail actualTail
            | _ -> false

        identity granted = identity requested
        && SecurityPrincipal.contains (principal granted) (principal requested)
        && isPrefix (agentLineage granted) (agentLineage requested)

module IdempotencyKey =
    let private hash scope value =
        let bytes = Encoding.UTF8.GetBytes(sprintf "%s\n%s" scope value)
        let digest = SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
        IdempotencyKey(sprintf "%s:%s" scope digest)

    let forExecution executionId =
        ExecutionId.serialize executionId |> hash "execution"

    let turn authorization conversationId turnId =
        let groupId =
            AuthorizationScope.groupId authorization
            |> Option.map GroupId.value
            |> Option.defaultValue ""

        let sessionId =
            AuthorizationScope.sessionId authorization
            |> Option.map SessionId.value
            |> Option.defaultValue ""

        [ AuthorizationScope.tenantId authorization |> TenantId.value
          AuthorizationScope.userId authorization |> UserId.value
          AuthorizationScope.workspaceId authorization |> WorkspaceId.value
          groupId
          sessionId
          conversationId
          TurnId.value turnId ]
        |> String.concat "\n"
        |> hash "turn"

    let operation kind ordinal (IdempotencyKey turnKey) =
        if ordinal < 1 then
            invalidArg (nameof ordinal) "Operation ordinal must be positive."

        if String.IsNullOrWhiteSpace kind || kind <> kind.Trim() then
            invalidArg (nameof kind) "Operation kind must be non-blank and have no surrounding whitespace."

        hash kind (sprintf "%s\n%d" turnKey ordinal)

    let value (IdempotencyKey value) = value

    let tryParse (value: string) =
        match value.Split(':') with
        | [| scope; digest |] when
            not (String.IsNullOrWhiteSpace scope)
            && digest.Length = 64
            && digest |> Seq.forall Uri.IsHexDigit
            ->
            Some(IdempotencyKey value)
        | _ -> None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () -> invalidArg (nameof value) "Invalid idempotency key.")
