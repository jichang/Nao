namespace Nao.Runtime.Orleans.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Runtime.Orleans.Grains

[<TestClass>]
type SessionAuthorizationTests() =
    let tenantId = TenantId.parse "tenant-a"
    let userId = UserId.parse "user-a"
    let allowedGroupId = GroupId.parse "group-a"
    let principal = SecurityPrincipal.create tenantId userId [ allowedGroupId ]

    [<TestMethod>]
    member _.ScopeRequiresThePrincipalUserAndAllowedGroup() =
        let allowed =
            SessionAuthorization.tryCreateScope principal "user-a" "session-a" "group-a" "workspace-a"

        let wrongUser =
            SessionAuthorization.tryCreateScope principal "user-b" "session-a" "group-a" "workspace-a"

        let wrongGroup =
            SessionAuthorization.tryCreateScope principal "user-a" "session-a" "group-b" "workspace-a"

        Assert.IsTrue(allowed.IsSome)
        Assert.IsTrue(wrongUser.IsNone)
        Assert.IsTrue(wrongGroup.IsNone)

    [<TestMethod>]
    member _.PersistedLineageRejectsAnotherTenant() =
        let scope =
            SessionAuthorization.tryCreateScope principal "user-a" "session-a" "group-a" "workspace-a"
            |> Option.get

        let info = SessionInfo()
        info.TenantId <- "tenant-a"
        info.UserId <- "user-a"
        info.SessionId <- "session-a"
        info.GroupId <- "group-a"
        Assert.IsTrue(SessionAuthorization.matchesPersisted info scope)

        let otherPrincipal =
            SecurityPrincipal.create (TenantId.parse "tenant-b") userId [ allowedGroupId ]

        let otherScope =
            SessionAuthorization.tryCreateScope otherPrincipal "user-a" "session-a" "group-a" "workspace-a"
            |> Option.get

        Assert.IsFalse(SessionAuthorization.matchesPersisted info otherScope)

    [<TestMethod>]
    member _.PersistedLineageRejectsAnotherUserGroupOrSession() =
        let scope =
            SessionAuthorization.tryCreateScope principal "user-a" "session-a" "group-a" "workspace-a"
            |> Option.get

        let info = SessionInfo()
        info.TenantId <- "tenant-a"
        info.UserId <- "user-a"
        info.SessionId <- "session-a"
        info.GroupId <- "group-a"

        let mutations =
            [ (fun () -> info.UserId <- "user-b")
              (fun () -> info.GroupId <- "group-b")
              (fun () -> info.SessionId <- "session-b") ]

        for mutate in mutations do
            let originalUserId, originalGroupId, originalSessionId =
                info.UserId, info.GroupId, info.SessionId

            mutate ()
            Assert.IsFalse(SessionAuthorization.matchesPersisted info scope)
            info.UserId <- originalUserId
            info.GroupId <- originalGroupId
            info.SessionId <- originalSessionId
