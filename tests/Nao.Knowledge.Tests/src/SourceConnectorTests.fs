namespace Nao.Knowledge.Tests

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type FilesystemSourceConnectorTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

    let now = DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero)

    let trust =
        TrustAssessment.create KnowledgeTrust.OrganizationVerified "test fixture" now

    let createRoot () =
        let root =
            Path.Combine(Path.GetTempPath(), "nao-knowledge-connector-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore
        root

    let source root owner =
        KnowledgeSource.create
            (KnowledgeSourceId.parse "filesystem-docs")
            (Uri(root + string Path.DirectorySeparatorChar))
            owner
            DataClassification.Internal
            trust
            Map.empty
            now
            (KnowledgeLifecycle.active None false)

    let allowAll =
        { AuthorizeAsync = fun _ _ _ -> Task.FromResult true
          Network = SourceNetworkPolicy.Denied }

    let request source mode checkpoint policy =
        { Authorization = authorization
          Source = source
          Mode = mode
          Checkpoint = checkpoint
          Policy = policy
          RequestedAt = now }

    let connector root reader maximumBytes =
        FilesystemSourceConnector.create
            { RootPath = root
              Include = [ "*.md"; "docs/*.txt" ]
              Exclude = [ "private*" ]
              MaximumContentBytes = maximumBytes
              MaximumItems = 100
              Reader = reader }

    let sync connector request =
        connector.SynchronizeAsync request CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Snapshot filters files and incremental pull is idempotent``() =
        let root = createRoot ()

        try
            Directory.CreateDirectory(Path.Combine(root, "docs")) |> ignore
            File.WriteAllText(Path.Combine(root, "readme.md"), "readme")
            File.WriteAllText(Path.Combine(root, "docs", "guide.txt"), "guide")
            File.WriteAllText(Path.Combine(root, "private.md"), "secret")
            File.WriteAllText(Path.Combine(root, "ignored.bin"), "binary")

            let source = source root (AuthorizationScope.identity authorization)
            let connector = connector root FilesystemSourceReader.system 1024L

            let initial =
                sync connector (request source SourceConnectorMode.Snapshot None allowAll)

            Assert.AreEqual(2, initial.Changes.Length)
            Assert.AreEqual(0, initial.Failures.Length)

            let paths =
                initial.Changes
                |> List.map (function
                    | SourceChange.Added content -> content.RelativePath
                    | change -> failwithf "Expected added change, got %A." change)
                |> Set.ofList

            Assert.AreEqual(Set.ofList [ "docs/guide.txt"; "readme.md" ], paths)

            let unchanged =
                sync connector (request source SourceConnectorMode.Pull (Some initial.Checkpoint) allowAll)

            Assert.AreEqual(0, unchanged.Changes.Length)

            Assert.AreEqual(
                SourceConnectorCheckpoint.stateHash initial.Checkpoint,
                SourceConnectorCheckpoint.stateHash unchanged.Checkpoint
            )
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.``Changed moved and deleted files converge with stable move identity``() =
        let root = createRoot ()

        try
            let changedPath = Path.Combine(root, "changed.md")
            let movedFrom = Path.Combine(root, "before.md")
            let deletedPath = Path.Combine(root, "deleted.md")
            File.WriteAllText(changedPath, "v1")
            File.WriteAllText(movedFrom, "move")
            File.WriteAllText(deletedPath, "delete")

            let source = source root (AuthorizationScope.identity authorization)
            let connector = connector root FilesystemSourceReader.system 1024L

            let initial =
                sync connector (request source SourceConnectorMode.Snapshot None allowAll)

            let movedId =
                initial.Changes
                |> List.pick (function
                    | SourceChange.Added content when content.RelativePath = "before.md" -> Some content.ItemId
                    | _ -> None)

            File.WriteAllText(changedPath, "v2")
            File.Move(movedFrom, Path.Combine(root, "after.md"))
            File.Delete deletedPath

            let next =
                sync connector (request source SourceConnectorMode.Pull (Some initial.Checkpoint) allowAll)

            Assert.AreEqual(3, next.Changes.Length)

            Assert.IsTrue(
                next.Changes
                |> List.exists (function
                    | SourceChange.Updated content ->
                        content.RelativePath = "changed.md"
                        && Encoding.UTF8.GetString(content.Content) = "v2"
                    | _ -> false)
            )

            Assert.IsTrue(
                next.Changes
                |> List.exists (function
                    | SourceChange.Moved(previousUri, content) ->
                        previousUri.LocalPath = movedFrom
                        && content.RelativePath = "after.md"
                        && content.ItemId = movedId
                    | _ -> false)
            )

            Assert.IsTrue(
                next.Changes
                |> List.exists (function
                    | SourceChange.Deleted(_, previousUri, _) -> previousUri.LocalPath = deletedPath
                    | _ -> false)
            )

            let converged =
                sync connector (request source SourceConnectorMode.Pull (Some next.Checkpoint) allowAll)

            Assert.AreEqual(0, converged.Changes.Length)
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.``Failed item does not corrupt successful changes or checkpoint``() =
        let root = createRoot ()

        try
            let stablePath = Path.Combine(root, "stable.md")
            let failingPath = Path.Combine(root, "failing.md")
            File.WriteAllText(stablePath, "v1")
            File.WriteAllText(failingPath, "old")

            let source = source root (AuthorizationScope.identity authorization)
            let initialConnector = connector root FilesystemSourceReader.system 1024L

            let initial =
                sync initialConnector (request source SourceConnectorMode.Snapshot None allowAll)

            File.WriteAllText(stablePath, "v2")
            File.WriteAllText(failingPath, "new")

            let reader =
                { ReadAllBytesAsync =
                    fun path cancellationToken ->
                        if path = failingPath then
                            Task.FromException<byte array>(IOException "injected read failure")
                        else
                            File.ReadAllBytesAsync(path, cancellationToken) }

            let failed =
                sync
                    (connector root reader 1024L)
                    (request source SourceConnectorMode.Pull (Some initial.Checkpoint) allowAll)

            Assert.AreEqual(1, failed.Failures.Length)

            Assert.IsTrue(
                failed.Changes
                |> List.exists (function
                    | SourceChange.Updated content -> content.RelativePath = "stable.md"
                    | _ -> false)
            )

            Assert.IsFalse(
                failed.Changes
                |> List.exists (function
                    | SourceChange.Deleted(_, uri, _) -> uri.LocalPath = failingPath
                    | _ -> false)
            )

            let recovered =
                sync initialConnector (request source SourceConnectorMode.Pull (Some failed.Checkpoint) allowAll)

            Assert.AreEqual(1, recovered.Changes.Length)

            match recovered.Changes |> List.exactlyOne with
            | SourceChange.Updated content ->
                Assert.AreEqual("failing.md", content.RelativePath)
                Assert.AreEqual("new", Encoding.UTF8.GetString content.Content)
            | change -> Assert.Fail($"Expected recovered update, got {change}.")
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.``Authorization denial and owner mismatch fail before file reads``() =
        let root = createRoot ()

        try
            File.WriteAllText(Path.Combine(root, "readme.md"), "content")
            let reads = ConcurrentQueue<string>()

            let reader =
                { ReadAllBytesAsync =
                    fun path _ ->
                        reads.Enqueue path
                        Task.FromResult(Array.empty<byte>) }

            let connector = connector root reader 1024L
            let validSource = source root (AuthorizationScope.identity authorization)

            let deny =
                { AuthorizeAsync = fun _ _ _ -> Task.FromResult false
                  Network = SourceNetworkPolicy.Denied }

            Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
                sync connector (request validSource SourceConnectorMode.Pull None deny)
                |> ignore)
            |> ignore

            let otherOwner =
                { AuthorizationScope.identity authorization with
                    TenantId = TenantId.parse "other" }

            Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
                sync connector (request (source root otherOwner) SourceConnectorMode.Pull None allowAll)
                |> ignore)
            |> ignore

            Assert.AreEqual(0, reads.Count)
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.``Cancellation propagates before authorization or enumeration``() =
        let root = createRoot ()

        try
            let calls = ref 0

            let policy =
                { AuthorizeAsync =
                    fun _ _ _ ->
                        calls.Value <- calls.Value + 1
                        Task.FromResult true
                  Network = SourceNetworkPolicy.Denied }

            use cancellation = new CancellationTokenSource()
            cancellation.Cancel()
            let connector = connector root FilesystemSourceReader.system 1024L

            Assert.ThrowsExactly<OperationCanceledException>(fun () ->
                connector.SynchronizeAsync
                    (request
                        (source root (AuthorizationScope.identity authorization))
                        SourceConnectorMode.Pull
                        None
                        policy)
                    cancellation.Token
                |> _.GetAwaiter().GetResult()
                |> ignore)
            |> ignore

            Assert.AreEqual(0, calls.Value)
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.``Maximum item overflow fails instead of returning a partial checkpoint``() =
        let root = createRoot ()

        try
            File.WriteAllText(Path.Combine(root, "one.md"), "one")
            File.WriteAllText(Path.Combine(root, "two.md"), "two")

            let connector =
                FilesystemSourceConnector.create
                    { RootPath = root
                      Include = [ "*.md" ]
                      Exclude = []
                      MaximumContentBytes = 1024L
                      MaximumItems = 1
                      Reader = FilesystemSourceReader.system }

            Assert.ThrowsExactly<InvalidOperationException>(fun () ->
                sync
                    connector
                    (request
                        (source root (AuthorizationScope.identity authorization))
                        SourceConnectorMode.Pull
                        None
                        allowAll)
                |> ignore)
            |> ignore
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.``Checkpoint from another connector fails closed``() =
        let root = createRoot ()

        try
            let connector = connector root FilesystemSourceReader.system 1024L

            let foreignCheckpoint =
                SourceConnectorCheckpoint.create
                    (ComponentVersion.create "foreign.connector" "1")
                    """{"Version":1,"Items":[]}"""

            Assert.ThrowsExactly<ArgumentException>(fun () ->
                sync
                    connector
                    (request
                        (source root (AuthorizationScope.identity authorization))
                        SourceConnectorMode.Pull
                        (Some foreignCheckpoint)
                        allowAll)
                |> ignore)
            |> ignore
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.``Source URI must match configured filesystem root``() =
        let root = createRoot ()
        let otherRoot = createRoot ()

        try
            let connector = connector root FilesystemSourceReader.system 1024L

            Assert.ThrowsExactly<ArgumentException>(fun () ->
                sync
                    connector
                    (request
                        (source otherRoot (AuthorizationScope.identity authorization))
                        SourceConnectorMode.Pull
                        None
                        allowAll)
                |> ignore)
            |> ignore
        finally
            Directory.Delete(root, true)
            Directory.Delete(otherRoot, true)
