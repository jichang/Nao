namespace Nao.Agents

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

type PermissionApprovalRequest =
    { Authorization: AuthorizationScope
      Correlation: CorrelationContext
      Access: ResourceAccess
      Reason: string
      ForceConfirmation: bool
      RequestedAt: DateTimeOffset
      AllowedGrantScopes: Set<PermissionGrantScope> }

type PermissionApprovalGrant =
    { Scope: PermissionGrantScope
      ExpiresAt: DateTimeOffset option }

[<RequireQualifiedAccess>]
type PermissionApproval =
    | Approved of PermissionApprovalGrant
    | Denied

type PermissionGrant =
    { Id: Guid
      Version: int
      Scope: PermissionGrantScope
      Target: PermissionTarget
      GrantedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset option
      RevokedAt: DateTimeOffset option }

type PermissionGrantStore =
    { SaveAsync: AuthorizationScope -> PermissionGrant -> Task
      GetActiveAsync: AuthorizationScope -> DateTimeOffset -> Task<PermissionGrant list>
      RevokeAsync: AuthorizationScope -> Guid -> DateTimeOffset -> Task<bool>
      DeleteExpiredAsync: AuthorizationScope -> DateTimeOffset -> Task<int>
      DeleteScopeAsync: AuthorizationScope -> PermissionGrantScope -> Task<int> }

[<RequireQualifiedAccess>]
module PermissionGrant =
    let currentVersion = 1

    let validate grant =
        if grant.Version <> currentVersion then
            invalidArg (nameof grant) $"Unsupported permission grant version {grant.Version}."

        if grant.Scope = PermissionGrantScope.Operation then
            invalidArg (nameof grant) "Operation approvals are not persistent grants."

        match grant.ExpiresAt with
        | Some expiresAt when expiresAt <= grant.GrantedAt ->
            invalidArg (nameof grant) "Permission grant expiry must be later than its grant time."
        | _ -> ()

    let create scope target grantedAt expiresAt =
        if scope = PermissionGrantScope.Operation then
            invalidArg (nameof scope) "Operation approvals are not persistent grants."

        match expiresAt with
        | Some expiresAt when expiresAt <= grantedAt ->
            invalidArg (nameof expiresAt) "Permission grant expiry must be later than its grant time."
        | _ ->
            { Id = Guid.NewGuid()
              Version = currentVersion
              Scope = scope
              Target = target
              GrantedAt = grantedAt
              ExpiresAt = expiresAt
              RevokedAt = None }

    let isActive at grant =
        grant.RevokedAt.IsNone
        && (grant.ExpiresAt |> Option.forall (fun expiresAt -> expiresAt > at))

[<RequireQualifiedAccess>]
module PermissionGrantPartition =
    let storageKey grantScope authorization =
        if grantScope = PermissionGrantScope.Operation then
            invalidArg (nameof grantScope) "Operation approvals do not have a durable partition."

        let principal = AuthorizationScope.principal authorization

        let workspaceId, sessionId =
            match grantScope with
            | PermissionGrantScope.Session ->
                AuthorizationScope.workspaceId authorization |> WorkspaceId.value |> Some,
                (AuthorizationScope.sessionId authorization
                 |> Option.map SessionId.value
                 |> Option.orElseWith (fun () ->
                     invalidArg
                         (nameof authorization)
                         "Session permission grants require a session-bound authorization scope."))
            | PermissionGrantScope.Workspace ->
                Some(AuthorizationScope.workspaceId authorization |> WorkspaceId.value), None
            | PermissionGrantScope.Durable -> None, None
            | PermissionGrantScope.Operation -> None, None

        [| AuthorizationScope.tenantId authorization |> TenantId.value
           SecurityPrincipal.subjectId principal |> UserId.value
           SecurityPrincipal.userId principal |> UserId.value
           AuthorizationScope.groupId authorization
           |> Option.map GroupId.value
           |> Option.toObj
           workspaceId |> Option.toObj
           sessionId |> Option.toObj
           SecurityPrincipal.serviceLineage principal
           |> List.map ServiceId.value
           |> List.toArray
           |> System.Text.Json.JsonSerializer.Serialize
           string grantScope |]
        |> System.Text.Json.JsonSerializer.Serialize
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

[<RequireQualifiedAccess>]
module InMemoryPermissionGrantStore =
    let create () : PermissionGrantStore =
        let grants = ConcurrentDictionary<string * Guid, PermissionGrant>()

        let keys authorization =
            [ PermissionGrantScope.Session
              PermissionGrantScope.Workspace
              PermissionGrantScope.Durable ]
            |> List.choose (fun scope ->
                try
                    Some(PermissionGrantPartition.storageKey scope authorization)
                with :? ArgumentException ->
                    None)
            |> Set.ofList

        { SaveAsync =
            fun authorization grant ->
                PermissionGrant.validate grant
                let key = PermissionGrantPartition.storageKey grant.Scope authorization

                if grant.RevokedAt.IsSome then
                    invalidArg (nameof grant) "A new permission grant cannot already be revoked."

                grants.[(key, grant.Id)] <- grant
                Task.CompletedTask
          GetActiveAsync =
            fun authorization at ->
                let applicable = keys authorization

                grants
                |> Seq.choose (fun pair ->
                    if applicable.Contains(fst pair.Key) && PermissionGrant.isActive at pair.Value then
                        Some pair.Value
                    else
                        None)
                |> Seq.sortBy _.GrantedAt
                |> Seq.toList
                |> Task.FromResult
          RevokeAsync =
            fun authorization grantId revokedAt ->
                let applicable = keys authorization

                grants
                |> Seq.tryFind (fun pair -> snd pair.Key = grantId && applicable.Contains(fst pair.Key))
                |> function
                    | Some pair when pair.Value.RevokedAt.IsNone ->
                        grants.[pair.Key] <-
                            { pair.Value with
                                RevokedAt = Some revokedAt }

                        Task.FromResult true
                    | _ -> Task.FromResult false
          DeleteExpiredAsync =
            fun authorization before ->
                let applicable = keys authorization
                let mutable deleted = 0

                for pair in grants do
                    if
                        applicable.Contains(fst pair.Key)
                        && (pair.Value.ExpiresAt |> Option.exists (fun expiresAt -> expiresAt <= before))
                    then
                        match grants.TryRemove pair.Key with
                        | true, _ -> deleted <- deleted + 1
                        | false, _ -> ()

                Task.FromResult deleted
          DeleteScopeAsync =
            fun authorization scope ->
                let partition = PermissionGrantPartition.storageKey scope authorization
                let mutable deleted = 0

                for pair in grants do
                    if fst pair.Key = partition then
                        match grants.TryRemove pair.Key with
                        | true, _ -> deleted <- deleted + 1
                        | false, _ -> ()

                Task.FromResult deleted }

[<NoEquality; NoComparison>]
type PermissionApprovalBroker =
    private
        { Timeout: TimeSpan
          ResolveAsync: PermissionApprovalRequest -> CancellationToken -> Task<PermissionApproval> }

[<RequireQualifiedAccess>]
module PermissionApprovalBroker =
    let create timeout resolveAsync =
        if timeout <= TimeSpan.Zero then
            invalidArg (nameof timeout) "Permission approval timeout must be positive."

        { Timeout = timeout
          ResolveAsync = resolveAsync }

    let unavailable =
        create (TimeSpan.FromSeconds 1.0) (fun _ _ -> Task.FromResult PermissionApproval.Denied)

[<RequireQualifiedAccess>]
module PermissionGate =
    /// Resolve through a host-owned broker. Broker timeout and unsupported grant scopes deny;
    /// caller cancellation still propagates.
    let resolve broker request cancellationToken =
        task {
            if request.AllowedGrantScopes.IsEmpty then
                invalidArg (nameof request) "A permission request must allow at least one grant scope."

            if
                String.IsNullOrWhiteSpace request.Reason
                || request.Reason <> request.Reason.Trim()
            then
                invalidArg (nameof request) "A permission request requires a canonical non-blank reason."

            if not (ResourceAccess.isCanonical request.Access) then
                return PermissionApproval.Denied
            else
                try
                    let! approval =
                        broker.ResolveAsync request cancellationToken
                        |> _.WaitAsync(broker.Timeout, cancellationToken)

                    match approval with
                    | PermissionApproval.Approved grant when
                        request.AllowedGrantScopes.Contains grant.Scope
                        && (grant.ExpiresAt
                            |> Option.forall (fun expiresAt -> expiresAt > request.RequestedAt))
                        ->
                        return approval
                    | _ -> return PermissionApproval.Denied
                with :? TimeoutException ->
                    return PermissionApproval.Denied
        }
