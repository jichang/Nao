namespace Nao.Identity.Oidc.Tests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Identity.Oidc

type private ConnectionFixture() =
    let values = ConcurrentDictionary<string * string, StoredAccountConnection>()
    let mutable refreshes = 0
    let mutable revoked = false

    let store =
        { new IAccountConnectionStore with
            member _.ListAsync(owner, token) =
                token.ThrowIfCancellationRequested()

                values.Values
                |> Seq.filter (fun value -> value.OwnerKey = owner)
                |> Seq.toList
                |> Task.FromResult

            member _.GetAsync(owner, id, _) =
                match values.TryGetValue((owner, id)) with
                | true, value -> Task.FromResult(Some value)
                | _ -> Task.FromResult None

            member _.SaveAsync(value, _) =
                values[(value.OwnerKey, value.Account.Id)] <- value
                Task.CompletedTask

            member _.DeleteAsync(owner, id, _) =
                values.TryRemove((owner, id)) |> fst |> Task.FromResult }

    let secret kind =
        SecretValue.create (SecretReference.parse kind) ("test-" + kind)

    let refresher =
        { new IOAuthTokenRefresher with
            member _.RefreshAsync(_, previous, token) =
                task {
                    Interlocked.Increment(&refreshes) |> ignore
                    do! Task.Delay(10, token)

                    if revoked then
                        return OAuthRefreshResult.ReauthenticationRequired
                    else
                        let fresh =
                            { previous with
                                AccessToken = secret "new-access"
                                ExpiresAt = DateTimeOffset.UtcNow.AddHours 1.0 }

                        return OAuthRefreshResult.Refreshed fresh
                } }

    member _.Broker = AccountTokenBroker(store, refresher) :> IAccountTokenBroker
    member _.Refreshes = refreshes
    member _.Revoke() = revoked <- true

    member _.Tokens(minutes) =
        { AccessToken = secret "access"
          RefreshToken = Some(secret "refresh")
          Scopes = Set.ofList [ "Mail.Read" ]
          ExpiresAt = DateTimeOffset.UtcNow.AddMinutes minutes }

    member _.Account =
        { Provider = "microsoft"
          Subject = "external-user"
          DisplayName = "Test account" }

[<TestClass>]
type AccountConnectionTests() =
    let scope tenant user session =
        let principal =
            SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse user) []

        AuthorizationScope.tryCreate
            principal
            None
            (WorkspaceId.parse "workspace")
            (session |> Option.map SessionId.parse)
        |> Option.get

    let owner = scope "tenant" "user" None
    let token = CancellationToken.None

    [<TestMethod>]
    member _.ValidTokenDoesNotRefresh() : Task =
        task {
            let fixture = ConnectionFixture()
            let broker = fixture.Broker
            let! account = broker.CompleteAsync(owner, None, fixture.Account, fixture.Tokens 30.0, token)
            let! result = broker.AcquireAsync(owner, account.Id, Set.singleton "Mail.Read", token)

            match result with
            | AccountTokenResult.Available(secret, _) -> Assert.AreEqual("[REDACTED]", secret.ToString())
            | _ -> Assert.Fail "Expected a usable token."

            Assert.AreEqual(0, fixture.Refreshes)
        }

    [<TestMethod>]
    member _.AcquiredTokensParticipateInExecutionSecretRedaction() : Task =
        task {
            let fixture = ConnectionFixture()
            let broker = fixture.Broker
            let! account = broker.CompleteAsync(owner, None, fixture.Account, fixture.Tokens 30.0, token)

            do!
                SecretRedaction.runAsync (fun () ->
                    task {
                        let! _ = broker.AcquireAsync(owner, account.Id, Set.empty, token)
                        Assert.AreEqual("[REDACTED]", SecretRedaction.redact "test-access")
                    })
        }

    [<TestMethod>]
    member _.TokensArePartitionedByOwnerButAvailableAcrossOwnedSessions() : Task =
        task {
            let fixture = ConnectionFixture()
            let broker = fixture.Broker
            let! account = broker.CompleteAsync(owner, None, fixture.Account, fixture.Tokens 30.0, token)

            for other in [ scope "other-tenant" "user" None; scope "tenant" "other-user" None ] do
                let! result = broker.AcquireAsync(other, account.Id, Set.empty, token)
                Assert.AreEqual(AccountTokenResult.ConnectionRequired, result)
                let! removed = broker.DisconnectAsync(other, account.Id, token)
                Assert.IsFalse removed

            let! owned = broker.ListAsync(scope "tenant" "user" (Some "session"), token)
            Assert.AreEqual(account.Id, owned.Head.Id)
        }

    [<TestMethod>]
    member _.ConcurrentRefreshRunsOnceAndPersistsFreshToken() : Task =
        task {
            let fixture = ConnectionFixture()
            let broker = fixture.Broker
            let! account = broker.CompleteAsync(owner, None, fixture.Account, fixture.Tokens 0.1, token)

            let! results =
                [| for _ in 1..8 -> broker.AcquireAsync(owner, account.Id, Set.singleton "Mail.Read", token) |]
                |> Task.WhenAll

            Assert.AreEqual(1, fixture.Refreshes)

            for result in results do
                match result with
                | AccountTokenResult.Available(secret, _) ->
                    Assert.AreEqual("test-new-access", SecretValue.useValue id secret)
                | _ -> Assert.Fail "Expected a refreshed token."
        }

    [<TestMethod>]
    member _.MissingScopeRequiresConsentWithoutRefreshing() : Task =
        task {
            let fixture = ConnectionFixture()
            let broker = fixture.Broker
            let! account = broker.CompleteAsync(owner, None, fixture.Account, fixture.Tokens 0.1, token)
            let! result = broker.AcquireAsync(owner, account.Id, Set.singleton "Mail.Send", token)
            Assert.AreEqual(AccountTokenResult.AdditionalConsentRequired(Set.singleton "Mail.Send"), result)
            Assert.AreEqual(0, fixture.Refreshes)
        }

    [<TestMethod>]
    member _.InvalidGrantRequiresReconnectionAndStopsFurtherRefresh() : Task =
        task {
            let fixture = ConnectionFixture()
            fixture.Revoke()
            let broker = fixture.Broker
            let! account = broker.CompleteAsync(owner, None, fixture.Account, fixture.Tokens 0.1, token)

            for _ in 1..2 do
                let! result = broker.AcquireAsync(owner, account.Id, Set.empty, token)
                Assert.AreEqual(AccountTokenResult.ReauthenticationRequired, result)

            Assert.AreEqual(1, fixture.Refreshes)
            let! accounts = broker.ListAsync(owner, token)
            Assert.IsTrue accounts.Head.ReconnectRequired
        }

    [<TestMethod>]
    member _.ReconnectCannotReplaceAnExternalIdentityAndDisconnectRemovesTokens() : Task =
        task {
            let fixture = ConnectionFixture()
            let broker = fixture.Broker
            let! account = broker.CompleteAsync(owner, None, fixture.Account, fixture.Tokens 30.0, token)
            let mutable rejected = false

            try
                let! _ =
                    broker.CompleteAsync(
                        owner,
                        Some account.Id,
                        { fixture.Account with
                            Subject = "different-user" },
                        fixture.Tokens 30.0,
                        token
                    )

                ()
            with :? UnauthorizedAccessException ->
                rejected <- true

            Assert.IsTrue rejected
            let! removed = broker.DisconnectAsync(owner, account.Id, token)
            Assert.IsTrue removed
            let! result = broker.AcquireAsync(owner, account.Id, Set.empty, token)
            Assert.AreEqual(AccountTokenResult.ConnectionRequired, result)
        }

type private RefreshHandler(body: string, status: HttpStatusCode) =
    inherit HttpMessageHandler()

    override _.SendAsync(request, cancellationToken) =
        cancellationToken.ThrowIfCancellationRequested()
        let uri = request.RequestUri |> Option.ofObj |> Option.get
        Assert.AreEqual("https://login.microsoftonline.com/common/oauth2/v2.0/token", uri.AbsoluteUri)
        Task.FromResult(new HttpResponseMessage(status, Content = new StringContent(body)))

[<TestClass>]
type OAuthRefreshTests() =
    let secret kind =
        SecretValue.create (SecretReference.parse kind) ("test-" + kind)

    let previous =
        { AccessToken = secret "access"
          RefreshToken = Some(secret "refresh")
          ExpiresAt = DateTimeOffset.UtcNow
          Scopes = Set.singleton "Mail.Read" }

    let registrations =
        [ { Provider = "microsoft"
            ClientId = "test-client"
            ClientSecret = secret "client-secret" } ]

    [<TestMethod>]
    member _.RefreshPreservesOmittedRefreshTokenAndScopes() : Task =
        task {
            use client =
                new HttpClient(
                    new RefreshHandler(
                        """{"access_token":"new-token","token_type":"Bearer","expires_in":3600}""",
                        HttpStatusCode.OK
                    )
                )

            let! result =
                (OAuthTokenRefresher(client, registrations) :> IOAuthTokenRefresher)
                    .RefreshAsync("microsoft", previous, CancellationToken.None)

            match result with
            | OAuthRefreshResult.Refreshed fresh ->
                Assert.AreEqual(previous.Scopes, fresh.Scopes)

                Assert.AreEqual(
                    "test-refresh",
                    fresh.RefreshToken |> Option.map (SecretValue.useValue id) |> Option.get
                )
            | _ -> Assert.Fail "Expected refreshed credentials."
        }

    [<TestMethod>]
    member _.InvalidGrantDoesNotBecomeAUsableToken() : Task =
        task {
            use client =
                new HttpClient(new RefreshHandler("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest))

            let! result =
                (OAuthTokenRefresher(client, registrations) :> IOAuthTokenRefresher)
                    .RefreshAsync("microsoft", previous, CancellationToken.None)

            match result with
            | OAuthRefreshResult.ReauthenticationRequired -> ()
            | _ -> Assert.Fail "Expected reauthentication."
        }
