namespace Nao.Identity.Oidc

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Nao.Agents

type ConnectedAccount =
    { Id: string
      Provider: string
      Subject: string
      DisplayName: string
      Scopes: Set<string>
      ExpiresAt: DateTimeOffset
      ReconnectRequired: bool }

type OAuthAccount =
    { Provider: string
      Subject: string
      DisplayName: string }

type OAuthTokens =
    { AccessToken: SecretValue
      RefreshToken: SecretValue option
      Scopes: Set<string>
      ExpiresAt: DateTimeOffset }

type StoredAccountConnection =
    { OwnerKey: string
      Account: ConnectedAccount
      Tokens: OAuthTokens option }

type IAccountConnectionStore =
    abstract ListAsync: ownerKey: string * CancellationToken -> Task<StoredAccountConnection list>
    abstract GetAsync: ownerKey: string * id: string * CancellationToken -> Task<StoredAccountConnection option>
    abstract SaveAsync: StoredAccountConnection * CancellationToken -> Task
    abstract DeleteAsync: ownerKey: string * id: string * CancellationToken -> Task<bool>

[<RequireQualifiedAccess>]
type OAuthRefreshResult =
    | Refreshed of OAuthTokens
    | ReauthenticationRequired

type IOAuthTokenRefresher =
    abstract RefreshAsync: provider: string * OAuthTokens * CancellationToken -> Task<OAuthRefreshResult>

[<RequireQualifiedAccess>]
type AccountTokenResult =
    | Available of token: SecretValue * expiresAt: DateTimeOffset
    | ConnectionRequired
    | ReauthenticationRequired
    | AdditionalConsentRequired of scopes: Set<string>

type IAccountTokenBroker =
    abstract ListAsync: AuthorizationScope * CancellationToken -> Task<ConnectedAccount list>

    abstract CompleteAsync:
        AuthorizationScope * connectionId: string option * OAuthAccount * OAuthTokens * CancellationToken ->
            Task<ConnectedAccount>

    abstract AcquireAsync:
        AuthorizationScope * connectionId: string * requiredScopes: Set<string> * CancellationToken ->
            Task<AccountTokenResult>

    abstract DisconnectAsync: AuthorizationScope * connectionId: string * CancellationToken -> Task<bool>

module AccountConnectionOwner =
    let key authorization =
        let principal = AuthorizationScope.principal authorization

        [| AuthorizationScope.tenantId authorization |> TenantId.value
           SecurityPrincipal.subjectId principal |> UserId.value
           SecurityPrincipal.userId principal |> UserId.value
           AuthorizationScope.workspaceId authorization |> WorkspaceId.value
           SecurityPrincipal.serviceLineage principal
           |> List.map ServiceId.value
           |> List.toArray
           |> System.Text.Json.JsonSerializer.Serialize |]
        |> System.Text.Json.JsonSerializer.Serialize
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

/// Trusted tool adapters use this broker; access tokens must never enter model-visible results.
type AccountTokenBroker(store: IAccountConnectionStore, refresher: IOAuthTokenRefresher, ?clock: unit -> DateTimeOffset)
    =
    let now = defaultArg clock (fun () -> DateTimeOffset.UtcNow)
    let locks = ConcurrentDictionary<string * string, SemaphoreSlim>()

    let validateId id =
        match Guid.TryParseExact(id, "N") with
        | true, _ -> ()
        | _ -> invalidArg (nameof id) "Connection ID must be a canonical opaque ID."

    let validateScopes scopes =
        if
            scopes
            |> Set.exists (fun scope -> String.IsNullOrWhiteSpace scope || scope |> Seq.exists Char.IsWhiteSpace)
        then
            invalidArg (nameof scopes) "OAuth scopes must be non-blank individual scope names."

    let verify owner connection =
        if connection.OwnerKey <> owner then
            raise (UnauthorizedAccessException "Connection ownership does not match the caller.")

        connection

    let locked owner id cancellationToken operation =
        task {
            validateId id
            let gate = locks.GetOrAdd((owner, id), fun _ -> new SemaphoreSlim(1, 1))
            do! gate.WaitAsync(cancellationToken: CancellationToken)

            try
                return! operation ()
            finally
                gate.Release() |> ignore
        }

    let available authorization (tokens: OAuthTokens) cancellationToken =
        task {
            let provider =
                SecretProvider.create "connected-accounts" "1" (fun _ _ _ ->
                    Task.FromResult(
                        SecretLookup.Available(
                            SecretVersion.parse (tokens.ExpiresAt.ToUnixTimeMilliseconds().ToString()),
                            tokens.AccessToken
                        )
                    ))

            let! resolution =
                SecretProvider.resolveAsync
                    authorization
                    (SecretValue.reference tokens.AccessToken)
                    cancellationToken
                    provider

            return AccountTokenResult.Available(SecretResolution.value resolution, tokens.ExpiresAt)
        }

    interface IAccountTokenBroker with
        member _.ListAsync(authorization, cancellationToken) =
            task {
                let owner = AccountConnectionOwner.key authorization
                let! values = store.ListAsync(owner, cancellationToken)
                return values |> List.map (verify owner >> _.Account)
            }

        member _.CompleteAsync(authorization, connectionId, account, tokens, cancellationToken) =
            task {
                if account.Provider <> "microsoft" && account.Provider <> "google" then
                    invalidArg (nameof account) "Unsupported connected-account provider."

                if
                    String.IsNullOrWhiteSpace account.Subject
                    || String.IsNullOrWhiteSpace account.DisplayName
                then
                    invalidArg (nameof account) "Provider account identity is required."

                validateScopes tokens.Scopes

                if tokens.ExpiresAt <= now () then
                    invalidArg (nameof tokens) "An unexpired access token is required."

                let owner = AccountConnectionOwner.key authorization
                let id = defaultArg connectionId (Guid.NewGuid().ToString("N"))

                return!
                    locked owner id cancellationToken (fun () ->
                        task {
                            let! existing = store.GetAsync(owner, id, cancellationToken)
                            let previous = existing |> Option.map (verify owner)

                            if connectionId.IsSome && previous.IsNone then
                                raise (Collections.Generic.KeyNotFoundException "Connection does not exist.")

                            match previous with
                            | Some previous when
                                previous.Account.Provider <> account.Provider
                                || previous.Account.Subject <> account.Subject
                                ->
                                raise (
                                    UnauthorizedAccessException "Reconnection must use the original external account."
                                )
                            | _ -> ()

                            let refresh =
                                tokens.RefreshToken
                                |> Option.orElseWith (fun () ->
                                    previous |> Option.bind _.Tokens |> Option.bind _.RefreshToken)

                            let tokens = { tokens with RefreshToken = refresh }

                            let connected =
                                { Id = id
                                  Provider = account.Provider
                                  Subject = account.Subject
                                  DisplayName = account.DisplayName
                                  Scopes = tokens.Scopes
                                  ExpiresAt = tokens.ExpiresAt
                                  ReconnectRequired = false }

                            do!
                                store.SaveAsync(
                                    { OwnerKey = owner
                                      Account = connected
                                      Tokens = Some tokens },
                                    cancellationToken
                                )

                            return connected
                        })
            }

        member _.AcquireAsync(authorization, id, requiredScopes, cancellationToken) =
            task {
                validateScopes requiredScopes
                let owner = AccountConnectionOwner.key authorization

                return!
                    locked owner id cancellationToken (fun () ->
                        task {
                            let! connection = store.GetAsync(owner, id, cancellationToken)

                            match connection |> Option.map (verify owner) with
                            | None -> return AccountTokenResult.ConnectionRequired
                            | Some connection when not (Set.isSubset requiredScopes connection.Account.Scopes) ->
                                return
                                    AccountTokenResult.AdditionalConsentRequired(
                                        Set.difference requiredScopes connection.Account.Scopes
                                    )
                            | Some connection ->
                                match connection.Tokens with
                                | None -> return AccountTokenResult.ReauthenticationRequired
                                | Some tokens when tokens.ExpiresAt > now().AddMinutes(1.0) ->
                                    return! available authorization tokens cancellationToken
                                | Some tokens ->
                                    let! refreshed =
                                        if tokens.RefreshToken.IsNone then
                                            Task.FromResult OAuthRefreshResult.ReauthenticationRequired
                                        else
                                            refresher.RefreshAsync(
                                                connection.Account.Provider,
                                                tokens,
                                                cancellationToken
                                            )

                                    match refreshed with
                                    | OAuthRefreshResult.ReauthenticationRequired ->
                                        do!
                                            store.SaveAsync(
                                                { connection with
                                                    Tokens = None
                                                    Account =
                                                        { connection.Account with
                                                            ReconnectRequired = true } },
                                                cancellationToken
                                            )

                                        return AccountTokenResult.ReauthenticationRequired
                                    | OAuthRefreshResult.Refreshed fresh ->
                                        validateScopes fresh.Scopes

                                        if fresh.ExpiresAt <= now () then
                                            invalidOp "The OAuth provider returned an expired access token."

                                        let fresh =
                                            { fresh with
                                                RefreshToken =
                                                    fresh.RefreshToken |> Option.orElse (tokens.RefreshToken) }

                                        let account =
                                            { connection.Account with
                                                Scopes = fresh.Scopes
                                                ExpiresAt = fresh.ExpiresAt }

                                        do!
                                            store.SaveAsync(
                                                { connection with
                                                    Account = account
                                                    Tokens = Some fresh },
                                                cancellationToken
                                            )

                                        if Set.isSubset requiredScopes fresh.Scopes then
                                            return! available authorization fresh cancellationToken
                                        else
                                            return
                                                AccountTokenResult.AdditionalConsentRequired(
                                                    Set.difference requiredScopes fresh.Scopes
                                                )
                        })
            }

        member _.DisconnectAsync(authorization, id, cancellationToken) =
            let owner = AccountConnectionOwner.key authorization
            locked owner id cancellationToken (fun () -> store.DeleteAsync(owner, id, cancellationToken))
