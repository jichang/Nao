namespace Nao.Knowledge.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass>]
type IndexManagementContractTests() =
    let now = DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero)
    let namespaceName = KnowledgeIndexNamespace.parse "primary"

    let identity user =
        { TenantId = TenantId.parse "tenant"
          GroupId = None
          UserId = UserId.parse user
          WorkspaceId = WorkspaceId.parse "workspace"
          SessionId = None }

    let authorization user =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse user) []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    [<TestMethod>]
    member _.``Healthy index cannot contain issues``() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeIndexHealth.create
                KnowledgeIndexHealthStatus.Healthy
                (Some 1)
                (Some(ContentHash.computeText "revision"))
                (Some 10L)
                now
                [ "unexpected issue" ]
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Unavailable index requires an explicit issue``() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeIndexHealth.create KnowledgeIndexHealthStatus.Unavailable None None None now []
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Migration target must differ from current revision``() =
        let revision = ContentHash.computeText "revision"

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeIndexMigrationPlan.create (Some revision) revision false now |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Backup restoration validates version and owner``() =
        let owner = identity "owner"

        let backup = KnowledgeIndexBackup.create namespaceName owner now [ "record" ]

        Assert.AreEqual(KnowledgeIndexBackup.currentVersion, KnowledgeIndexBackup.version backup)
        Assert.AreEqual([ "record" ], KnowledgeIndexBackup.records backup)

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeIndexBackup.restore 2 namespaceName owner now [] |> ignore)
        |> ignore

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            KnowledgeIndexBackup.requireOwner (authorization "other") backup |> ignore)
        |> ignore

        Assert.AreSame(backup, KnowledgeIndexBackup.requireOwner (authorization "owner") backup)

    [<TestMethod>]
    member _.``Rebuild counts cannot be negative``() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeIndexRebuildResult.create namespaceName -1L 0L now |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Lexical rebuild replaces the complete namespace through one mutation``() =
        let mutable captured = None

        let index: KnowledgeLexicalIndex =
            { ApplyAsync =
                fun _ mutation _ ->
                    captured <- Some mutation

                    Task.FromResult(KnowledgeIndexMutationResult.create namespaceName 0 3 0 now)
              QueryAsync = fun _ _ _ -> Task.FromResult [] }

        let result =
            KnowledgeIndexRebuild.lexicalAsync index (authorization "owner") namespaceName [] CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(3L, KnowledgeIndexRebuildResult.previousRecords result)
        Assert.AreEqual(0L, KnowledgeIndexRebuildResult.currentRecords result)

        let mutation = captured |> Option.get
        Assert.AreEqual(Some KnowledgeIndexFilter.none, KnowledgeIndexMutation.deleteFilter mutation)
        Assert.AreEqual([], KnowledgeIndexMutation.upserts mutation)
