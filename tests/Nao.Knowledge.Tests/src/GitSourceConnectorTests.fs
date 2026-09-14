namespace Nao.Knowledge.Tests

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type GitSourceConnectorTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

    let now = DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero)

    let commit value =
        String.replicate 40 value |> GitCommitId.parse

    let source uri =
        KnowledgeSource.create
            (KnowledgeSourceId.parse "git-docs")
            uri
            (AuthorizationScope.identity authorization)
            DataClassification.Internal
            (TrustAssessment.create KnowledgeTrust.OrganizationVerified "repository history" now)
            Map.empty
            now
            (KnowledgeLifecycle.active None false)

    let file (path: string) (content: string) =
        { Path = path
          Content = Encoding.UTF8.GetBytes content
          MediaType = None }

    let snapshot commitId files =
        GitSourceReadResult.Snapshot
            { CommitId = commitId
              CommittedAt = now
              Files = files }

    let connector repository =
        GitSourceConnector.create
            { Reference = "refs/heads/main"
              Include = [ "*.md"; "docs/*.txt" ]
              Exclude = [ "private*" ]
              MaximumBlobBytes = 1024L
              MaximumItems = 100
              Repository = repository }

    let request source checkpoint policy =
        { Authorization = authorization
          Source = source
          Mode = SourceConnectorMode.Pull
          Checkpoint = checkpoint
          Policy = policy
          RequestedAt = now }

    let synchronize connector request =
        connector.SynchronizeAsync request CancellationToken.None
        |> _.GetAwaiter().GetResult()

    let remotePolicy uri =
        { AuthorizeAsync = fun _ _ _ -> Task.FromResult true
          Network = SourceNetworkPolicy.Allowlist [ uri ] }

    [<TestMethod>]
    member _.``Snapshot filters files and matching commit short-circuits unchanged pull``() =
        let uri = Uri "https://git.example.test/team/docs.git"
        let reads = ResizeArray<GitSourceReadRequest>()
        let commitId = commit "a"

        let repository =
            { ReadAsync =
                fun request _ ->
                    reads.Add request

                    match request.PreviousCommit with
                    | Some previous -> Task.FromResult(GitSourceReadResult.Unchanged previous)
                    | None ->
                        Task.FromResult(
                            snapshot
                                commitId
                                [ file "README.md" "readme"
                                  file "docs/guide.txt" "guide"
                                  file "private.md" "private"
                                  file "image.bin" "binary" ]
                        ) }

        let connector = connector repository
        let initial = synchronize connector (request (source uri) None (remotePolicy uri))

        Assert.AreEqual(2, initial.Changes.Length)

        let unchanged =
            synchronize connector (request (source uri) (Some initial.Checkpoint) (remotePolicy uri))

        Assert.AreEqual(0, unchanged.Changes.Length)
        Assert.AreEqual(Some commitId, reads.[1].PreviousCommit)

        initial.Changes
        |> List.iter (function
            | SourceChange.Added content ->
                Assert.AreEqual(GitCommitId.value commitId, content.Metadata.["git.commit"])
                Assert.AreEqual("refs/heads/main", content.Metadata.["git.reference"])
            | change -> Assert.Fail($"Expected added Git content, got {change}."))

    [<TestMethod>]
    member _.``New commit converges updates moves and deletes``() =
        let uri = Uri "https://git.example.test/team/docs.git"
        let firstCommit = commit "a"
        let secondCommit = commit "b"
        let mutable call = 0

        let repository =
            { ReadAsync =
                fun _ _ ->
                    call <- call + 1

                    if call = 1 then
                        Task.FromResult(
                            snapshot
                                firstCommit
                                [ file "changed.md" "v1"; file "before.md" "move"; file "deleted.md" "delete" ]
                        )
                    else
                        Task.FromResult(snapshot secondCommit [ file "changed.md" "v2"; file "after.md" "move" ]) }

        let connector = connector repository
        let initial = synchronize connector (request (source uri) None (remotePolicy uri))

        let movedId =
            initial.Changes
            |> List.pick (function
                | SourceChange.Added content when content.RelativePath = "before.md" -> Some content.ItemId
                | _ -> None)

        let next =
            synchronize connector (request (source uri) (Some initial.Checkpoint) (remotePolicy uri))

        Assert.AreEqual(3, next.Changes.Length)

        Assert.IsTrue(
            next.Changes
            |> List.exists (function
                | SourceChange.Updated content -> content.RelativePath = "changed.md"
                | _ -> false)
        )

        Assert.IsTrue(
            next.Changes
            |> List.exists (function
                | SourceChange.Moved(_, content) -> content.RelativePath = "after.md" && content.ItemId = movedId
                | _ -> false)
        )

        Assert.IsTrue(
            next.Changes
            |> List.exists (function
                | SourceChange.Deleted(_, uri, _) -> uri.Fragment = "#deleted.md"
                | _ -> false)
        )

    [<TestMethod>]
    member _.``Remote network denial fails before repository access``() =
        let uri = Uri "https://git.example.test/team/docs.git"
        let mutable reads = 0

        let repository =
            { ReadAsync =
                fun _ _ ->
                    reads <- reads + 1
                    Task.FromResult(snapshot (commit "a") []) }

        let policy =
            { AuthorizeAsync = fun _ _ _ -> Task.FromResult true
              Network = SourceNetworkPolicy.Denied }

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            synchronize (connector repository) (request (source uri) None policy) |> ignore)
        |> ignore

        Assert.AreEqual(0, reads)

    [<TestMethod>]
    member _.``Local repository requires list and per-file read authorization``() =
        let root =
            Path.Combine(Path.GetTempPath(), "nao-git-source-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore

        try
            let uri = Uri(root + string Path.DirectorySeparatorChar)

            let repository =
                { ReadAsync = fun _ _ -> Task.FromResult(snapshot (commit "a") [ file "README.md" "readme" ]) }

            let accesses = ResizeArray<ResourceAccess>()

            let policy =
                { AuthorizeAsync =
                    fun _ access _ ->
                        accesses.Add access

                        Task.FromResult(
                            match access with
                            | ResourceAccess.File(FileAction.Read, _) -> false
                            | _ -> true
                        )
                  Network = SourceNetworkPolicy.Denied }

            Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
                synchronize (connector repository) (request (source uri) None policy) |> ignore)
            |> ignore

            Assert.IsTrue(
                accesses
                |> Seq.exists (function
                    | ResourceAccess.File(FileAction.List, path) -> path = Path.GetFullPath root
                    | _ -> false)
            )

            Assert.IsTrue(
                accesses
                |> Seq.exists (function
                    | ResourceAccess.File(FileAction.Read, path) ->
                        path = Path.Combine(Path.GetFullPath root, "README.md")
                    | _ -> false)
            )
        finally
            Directory.Delete(root, true)
