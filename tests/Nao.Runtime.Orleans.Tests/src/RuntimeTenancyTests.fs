namespace Nao.Runtime.Orleans.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Runtime.Orleans

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type RuntimeTenancyTests() =
    [<TestMethod>]
    member _.CanonicalKeysRoundTripDelimiterSafeIdentities() =
        let tenantId = TenantId.parse "tenant/with/slashes"

        let workspaceId =
            WorkspaceVersionId.create "workspace" "version-with-symbols-+_"
            |> WorkspaceVersionId.workspaceId

        let userId = UserId.parse "user/with/slashes"
        let sessionId = SessionId.parse "session/with/slashes"
        let groupId = GroupId.parse "group/with/slashes"

        let sessionAddress = RuntimeAddress.session tenantId workspaceId userId sessionId

        let parsedSession =
            sessionAddress
            |> RuntimeAddress.sessionKey
            |> RuntimeAddress.tryParseSessionKey
            |> Option.get

        Assert.AreEqual(tenantId, RuntimeAddress.sessionTenant parsedSession)
        Assert.AreEqual(workspaceId, RuntimeAddress.sessionWorkspace parsedSession)
        Assert.AreEqual(userId, RuntimeAddress.sessionUser parsedSession)
        Assert.AreEqual(sessionId, RuntimeAddress.sessionId parsedSession)

        let directoryAddress = RuntimeAddress.directory tenantId workspaceId userId

        let parsedDirectory =
            directoryAddress
            |> RuntimeAddress.directoryKey
            |> RuntimeAddress.tryParseDirectoryKey
            |> Option.get

        Assert.AreEqual(tenantId, RuntimeAddress.directoryTenant parsedDirectory)
        Assert.AreEqual(workspaceId, RuntimeAddress.directoryWorkspace parsedDirectory)
        Assert.AreEqual(userId, RuntimeAddress.directoryUser parsedDirectory)

        let parsedGroup =
            RuntimeAddress.group tenantId groupId
            |> RuntimeAddress.groupKey
            |> RuntimeAddress.tryParseGroupKey
            |> Option.get

        Assert.AreEqual(tenantId, RuntimeAddress.groupTenant parsedGroup)
        Assert.AreEqual(groupId, RuntimeAddress.groupId parsedGroup)

    [<TestMethod>]
    member _.WrongVersionMalformedEncodingAndWrongSegmentsAreRejected() =
        for key in
            [ ""
              "v2/dGVuYW50/d29ya3NwYWNl/dXNlcg/c2Vzc2lvbg"
              "v1/%%%%/d29ya3NwYWNl/dXNlcg/c2Vzc2lvbg"
              "v1/dGVuYW50/d29ya3NwYWNl/dXNlcg"
              "v1/dGVuYW50/d29ya3NwYWNl/dXNlcg/c2Vzc2lvbg/ZXh0cmE" ] do
            Assert.IsTrue((RuntimeAddress.tryParseSessionKey key).IsNone)

        Assert.ThrowsExactly<System.ArgumentException>(fun () ->
            RuntimeAddress.session
                (TenantId.parse "tenant")
                (WorkspaceId.parse "logical-only")
                (UserId.parse "user")
                (SessionId.parse "session")
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.AddressKindsCannotCollideOrCrossParse() =
        let tenantId = TenantId.parse "tenant"

        let workspaceId =
            WorkspaceVersionId.create "workspace" "1" |> WorkspaceVersionId.workspaceId

        let userId = UserId.parse "user"
        let sessionId = SessionId.parse "session"
        let groupId = GroupId.parse "group"

        let sessionKey =
            RuntimeAddress.session tenantId workspaceId userId sessionId
            |> RuntimeAddress.sessionKey

        let directoryKey =
            RuntimeAddress.directory tenantId workspaceId userId
            |> RuntimeAddress.directoryKey

        let groupKey = RuntimeAddress.group tenantId groupId |> RuntimeAddress.groupKey

        Assert.AreNotEqual(sessionKey, directoryKey)
        Assert.AreNotEqual(sessionKey, groupKey)
        Assert.AreNotEqual(directoryKey, groupKey)
        Assert.IsTrue((RuntimeAddress.tryParseDirectoryKey sessionKey).IsNone)
        Assert.IsTrue((RuntimeAddress.tryParseGroupKey sessionKey).IsNone)
        Assert.IsTrue((RuntimeAddress.tryParseSessionKey directoryKey).IsNone)

[<TestClass; TestCategory("Security"); TestCategory("Isolation")>]
type WorkspaceCatalogTenancyTests() =
    [<TestMethod>]
    member _.SameWorkspaceVersionIsImmutableAndPartitionedByTenant() =
        let catalog = WorkspaceCatalog.create Set.empty
        let tenantA = TenantId.parse "tenant-a"
        let tenantB = TenantId.parse "tenant-b"
        let sharedWorkspace = WorkspaceVersionId.create "shared" "2026.1"
        let onlyA = WorkspaceVersionId.create "only-a" "2026.1"
        let definitionsA = WorkspaceDefinitions.create [] [] []
        let definitionsB = WorkspaceDefinitions.create [] [] []

        let registration identity digest definitions =
            WorkspaceRegistration.create identity digest "1" [] definitions

        let sharedA =
            registration
                sharedWorkspace
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                definitionsA

        let onlyARegistration =
            registration onlyA "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" definitionsA

        let sharedB =
            registration
                sharedWorkspace
                "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                definitionsB

        catalog.Register(tenantA, sharedA)
        catalog.Register(tenantA, onlyARegistration)
        catalog.Register(tenantB, sharedB)

        Assert.AreSame(sharedA, catalog.Get(tenantA, sharedWorkspace))
        Assert.AreSame(sharedB, catalog.Get(tenantB, sharedWorkspace))
        CollectionAssert.AreEqual([| sharedWorkspace |], catalog.ListVersions(tenantA, "shared") |> List.toArray)
        CollectionAssert.AreEqual([| sharedWorkspace |], catalog.ListVersions(tenantB, "shared") |> List.toArray)

        Assert.ThrowsExactly<System.InvalidOperationException>(fun () -> catalog.Register(tenantA, sharedA))
        |> ignore

        Assert.AreSame(sharedA, catalog.Get(tenantA, sharedWorkspace))

    [<TestMethod>]
    member _.InvalidWorkspaceNeverEntersTheCatalog() =
        let catalog = WorkspaceCatalog.create Set.empty
        let tenant = TenantId.parse "tenant-a"
        let identity = WorkspaceVersionId.create "workspace" "2026.1"

        let registration =
            WorkspaceRegistration.create identity "not-a-content-digest" "1" [] (WorkspaceDefinitions.create [] [] [])

        Assert.ThrowsExactly<System.ArgumentException>(fun () -> catalog.Register(tenant, registration))
        |> ignore

        Assert.IsTrue(catalog.TryGet(tenant, identity).IsNone)
