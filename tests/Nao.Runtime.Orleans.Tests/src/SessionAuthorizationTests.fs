namespace Nao.Runtime.Orleans.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type SessionAuthorizationTests() =
    let tenantId = TenantId.parse "tenant-a"

    let workspaceId =
        WorkspaceVersionId.create "workspace-a" "1" |> WorkspaceVersionId.workspaceId

    let userId = UserId.parse "user-a"
    let sessionId = SessionId.parse "session-a"
    let allowedGroupId = GroupId.parse "group-a"
    let principal = SecurityPrincipal.create tenantId userId [ allowedGroupId ]

    let address tenant workspace user session =
        RuntimeAddress.session
            (TenantId.parse tenant)
            (WorkspaceId.parse workspace)
            (UserId.parse user)
            (SessionId.parse session)

    let createInfo () =
        let info = SessionInfo()
        info.TenantId <- TenantId.value tenantId
        info.WorkspaceKey <- WorkspaceId.value workspaceId
        info.UserId <- UserId.value userId
        info.SessionId <- SessionId.value sessionId
        info.GroupId <- GroupId.value allowedGroupId
        info

    [<TestMethod>]
    member _.ScopeRequiresKeyTenantWorkspaceUserSessionAndAllowedGroup() =
        let allowedAddress = RuntimeAddress.session tenantId workspaceId userId sessionId

        let allowed = SessionAuthorization.tryCreateScope principal allowedAddress "group-a"

        let deniedAddresses =
            [ address "tenant-b" "workspace-a@1" "user-a" "session-a"
              address "tenant-a" "workspace-a@1" "user-b" "session-a" ]

        Assert.IsTrue(allowed.IsSome)

        for deniedAddress in deniedAddresses do
            Assert.IsTrue((SessionAuthorization.tryCreateScope principal deniedAddress "group-a").IsNone)

        Assert.IsTrue((SessionAuthorization.tryCreateScope principal allowedAddress "group-b").IsNone)

    [<TestMethod>]
    member _.PersistedLineageRejectsAnotherTenantWorkspaceUserGroupOrSession() =
        let scope =
            RuntimeAddress.session tenantId workspaceId userId sessionId
            |> fun runtimeAddress -> SessionAuthorization.tryCreateScope principal runtimeAddress "group-a"
            |> Option.get

        let info = createInfo ()
        Assert.IsTrue(SessionAuthorization.matchesPersisted info scope)

        let mutations =
            [ (fun () -> info.TenantId <- "tenant-b")
              (fun () -> info.WorkspaceKey <- "workspace-b@1")
              (fun () -> info.UserId <- "user-b")
              (fun () -> info.GroupId <- "group-b")
              (fun () -> info.SessionId <- "session-b") ]

        for mutate in mutations do
            let original =
                info.TenantId, info.WorkspaceKey, info.UserId, info.GroupId, info.SessionId

            mutate ()
            Assert.IsFalse(SessionAuthorization.matchesPersisted info scope)

            let tenant, workspace, user, group, session = original
            info.TenantId <- tenant
            info.WorkspaceKey <- workspace
            info.UserId <- user
            info.GroupId <- group
            info.SessionId <- session
