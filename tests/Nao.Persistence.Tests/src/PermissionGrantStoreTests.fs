namespace Nao.Persistence.Tests

open System
open System.IO
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass>]
type PermissionGrantStoreTests() =
    let now = DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)

    let scope principal workspace session =
        AuthorizationScope.tryCreate
            principal
            None
            (WorkspaceId.parse workspace)
            (session |> Option.map SessionId.parse)
        |> Option.get

    let direct =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "customer") []
        |> fun principal -> scope principal "workspace" (Some "session")

    let impersonated =
        SecurityPrincipal.createImpersonated
            (TenantId.parse "tenant")
            (UserId.parse "operator")
            (UserId.parse "customer")
            "support-case-private"
            []
            [ "support-private" ]
            (Map.ofList [ "private-claim", "private-value" ])
            AuthenticationStrength.MultiFactor
        |> fun principal -> scope principal "workspace" (Some "session")

    let grant grantScope access expiresAt =
        PermissionGrant.create grantScope (ResourcePermission.targetForAccess access) now expiresAt

    [<TestMethod>]
    member _.FileStoreReloadsRevocationAndSeparatesAuthority() =
        let root =
            Path.Combine(Path.GetTempPath(), $"nao-permission-grants-{Guid.NewGuid():N}")

        try
            let stored =
                grant
                    PermissionGrantScope.Durable
                    (ResourceAccess.file FileAction.Read "/private")
                    (Some(now.AddHours 1.0))

            PermissionGrantStores.file root
            |> fun store -> store.SaveAsync impersonated stored
            |> _.Wait()

            let reloaded = PermissionGrantStores.file root
            let active = reloaded.GetActiveAsync impersonated now |> _.Result
            Assert.AreEqual(1, active.Length)
            Assert.AreEqual(PermissionGrant.currentVersion, active.Head.Version)
            Assert.AreEqual(0, reloaded.GetActiveAsync direct now |> _.Result.Length)
            Assert.IsTrue(reloaded.RevokeAsync impersonated stored.Id (now.AddMinutes 1.0) |> _.Result)

            let afterRevocation = PermissionGrantStores.file root

            Assert.AreEqual(
                0,
                afterRevocation.GetActiveAsync impersonated (now.AddMinutes 2.0)
                |> _.Result.Length
            )

            let payload =
                Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories)
                |> Array.exactlyOne
                |> File.ReadAllText

            for privateValue in
                [ "operator"
                  "customer"
                  "support-case-private"
                  "support-private"
                  "private-claim"
                  "private-value"
                  "MultiFactor" ] do
                Assert.IsFalse(payload.Contains privateValue, $"Leaked authority evidence: {privateValue}")
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)

    [<TestMethod>]
    member _.FileStoreDeletesExpiredGrantsDurably() =
        let root =
            Path.Combine(Path.GetTempPath(), $"nao-permission-expiry-{Guid.NewGuid():N}")

        try
            let stored =
                grant
                    PermissionGrantScope.Session
                    (ResourceAccess.web WebAction.Get "https://example.com/path")
                    (Some(now.AddMinutes 1.0))

            let store = PermissionGrantStores.file root
            store.SaveAsync direct stored |> _.Wait()

            Assert.AreEqual(0, store.GetActiveAsync direct (now.AddMinutes 1.0) |> _.Result.Length)
            Assert.AreEqual(1, store.DeleteExpiredAsync direct (now.AddMinutes 1.0) |> _.Result)

            Assert.AreEqual(
                0,
                PermissionGrantStores.file root
                |> fun reloaded -> reloaded.GetActiveAsync direct now |> _.Result.Length
            )

            let replacement =
                grant PermissionGrantScope.Session (ResourceAccess.tool "replacement") None

            store.SaveAsync direct replacement |> _.Wait()
            Assert.AreEqual(1, store.DeleteScopeAsync direct PermissionGrantScope.Session |> _.Result)

            Assert.AreEqual(
                0,
                PermissionGrantStores.file root
                |> fun reloaded -> reloaded.GetActiveAsync direct now |> _.Result.Length
            )
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
