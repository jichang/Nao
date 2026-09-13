namespace Nao.Persistence.Tests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass>]
type GraphAuthorizationTests() =
    let scope tenant user group workspace session =
        let principal =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse tenant)
                (UserId.parse user)
                (Some(ServiceId.parse "private-service"))
                [ GroupId.parse "group"; GroupId.parse "other" ]
                [ "private-role" ]
                (Map.ofList [ "private-claim", "private-value" ])
                AuthenticationStrength.MultiFactor

        AuthorizationScope.tryCreate
            principal
            (group |> Option.map GroupId.parse)
            (WorkspaceId.parse workspace)
            (session |> Option.map SessionId.parse)
        |> Option.get

    let owner = scope "tenant" "user" (Some "group") "workspace" (Some "session")

    let owners =
        [ owner
          scope "other" "user" (Some "group") "workspace" (Some "session")
          scope "tenant" "other" (Some "group") "workspace" (Some "session")
          scope "tenant" "user" (Some "other") "workspace" (Some "session")
          scope "tenant" "user" None "workspace" (Some "session")
          scope "tenant" "user" (Some "group") "other" (Some "session")
          scope "tenant" "user" (Some "group") "workspace" (Some "other")
          scope "tenant" "user" (Some "group") "workspace" None
          scope "a/b" "c" None "workspace" None
          scope "a_b" "c" None "workspace" None
          scope "a" "b/c" None "workspace" None ]

    let key scope =
        AuthorizationScope.identity scope |> AuthorizationIdentity.storageKey

    let cutoff = DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)

    let node id value timestamp : GraphNode =
        { Id = id
          EntityType = "thing"
          Properties = Map.ofList [ "value", value ]
          CreatedAt = timestamp
          LastAccessed = timestamp
          AccessCount = 0 }

    let relation subject object' timestamp : GraphRelation =
        { Subject = subject
          Object = object'
          Predicate = "uses"
          Confidence = 1.0
          Source = None
          Timestamp = timestamp
          Metadata = Map.empty }

    let withStorage action =
        let root =
            Path.Combine(Path.GetTempPath(), "nao-graph-scope-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore

        let factory =
            DbConnectionFactory.ofFunc (fun () ->
                new SqliteConnection(sprintf "Data Source=%s;Pooling=False" (Path.Combine(root, "graph.db")))
                :> Data.Common.DbConnection)

        try
            action root factory
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.EveryGraphOperationIsScopeBoundAcrossBackendsAndReloads() =
        withStorage (fun root factory ->
            let memory = InMemoryGraphMemory.create None

            for make in
                [ (fun () -> memory)
                  (fun () -> GraphMemories.file root None)
                  (fun () -> GraphMemories.ado factory None) ] do
                for index, scope in List.indexed owners do
                    let graph = make ()

                    for id in [ "a"; "b"; "c" ] do
                        graph.UpsertNodeAsync scope (node id (string index) cutoff)
                        |> _.GetAwaiter().GetResult()

                    graph.AddRelationAsync scope (relation "a" "b" cutoff)
                    |> _.GetAwaiter().GetResult()

                    graph.AddRelationAsync scope (relation "b" "c" cutoff)
                    |> _.GetAwaiter().GetResult()

                for index, scope in List.indexed owners do
                    let graph = make ()
                    let all = graph.GetByTypeAsync scope "thing" |> _.Result
                    Assert.AreEqual(3, all.Length)
                    Assert.IsTrue(all |> List.forall (fun item -> item.Properties.["value"] = string index))

                    let byProperties =
                        graph.QueryAsync scope (GraphQuery.ByProperties [ "value", string index ])
                        |> _.Result

                    Assert.AreEqual(3, byProperties.Nodes.Length)

                    Assert.AreEqual(
                        2,
                        (graph.QueryAsync scope (GraphQuery.ByPredicate "uses")).Result.Relations.Length
                    )

                    Assert.AreEqual(2, (graph.QueryAsync scope (GraphQuery.ByEntity "b")).Result.Relations.Length)
                    Assert.AreEqual(Some 2, (graph.QueryAsync scope (GraphQuery.Path("a", "c", 2))).Result.PathLength)
                    Assert.AreEqual(None, (graph.QueryAsync scope (GraphQuery.Path("a", "c", 1))).Result.PathLength)

                    for hops in 0..2 do
                        Assert.AreEqual(
                            hops + 1,
                            (graph.QueryAsync scope (GraphQuery.Neighborhood("a", hops))).Result.Nodes.Length
                        )

                    graph.UpsertNodeAsync scope (node "a" "updated" (cutoff.AddDays 1))
                    |> _.GetAwaiter().GetResult()

                    let updated =
                        (make ()).GetByTypeAsync scope "thing"
                        |> _.Result
                        |> List.find (fun item -> item.Id = "a")

                    Assert.AreEqual(cutoff, updated.CreatedAt)
                    Assert.AreEqual(1, updated.AccessCount)
                    graph.RemoveRelationAsync scope "b" "uses" "c" |> _.GetAwaiter().GetResult()
                    graph.RemoveNodeAsync scope "a" |> _.GetAwaiter().GetResult()

                    Assert.IsTrue(
                        ((make ()).QueryAsync scope (GraphQuery.ByPredicate "uses")).Result.Relations.IsEmpty
                    )

                    graph.UpsertNodeAsync scope (node "expired" "expired" (cutoff.AddTicks(-1)))
                    |> _.GetAwaiter().GetResult()

                    graph.AddRelationAsync scope (relation "expired" "b" cutoff)
                    |> _.GetAwaiter().GetResult()

                    Assert.AreEqual(Ok 2, graph.DeleteExpiredAsync scope cutoff |> _.Result)
                    Assert.AreEqual(2, ((make ()).GetByTypeAsync scope "thing").Result.Length)
                    Assert.AreEqual(Ok 2, graph.DeleteOwnerAsync scope |> _.Result)
                    Assert.AreEqual(Ok 0, (make ()).DeleteOwnerAsync scope |> _.Result)

                    for pending in List.skip (index + 1) owners do
                        Assert.AreEqual(3, ((make ()).GetByTypeAsync pending "thing").Result.Length))

    [<TestMethod>]
    member _.ExtractionPersistsOneBatchWithoutPrincipalEvidenceOrCallbackReplay() =
        withStorage (fun root factory ->
            let seen = ResizeArray<string>()

            let extractor text =
                seen.Add text
                Task.FromResult [ relation "a" "b" cutoff; relation "b" "c" cutoff ]

            for make in
                [ (fun () -> GraphMemories.file root (Some extractor))
                  (fun () -> GraphMemories.ado factory (Some extractor)) ] do
                let graph = make ()
                let count = seen.Count
                Assert.AreEqual(2, (graph.ExtractRelationsAsync owner "a uses b").Result.Length)
                Assert.AreEqual(count + 1, seen.Count)

                Assert.AreEqual(
                    2,
                    ((make ()).QueryAsync owner (GraphQuery.ByPredicate "uses")).Result.Relations.Length
                )

                Assert.AreEqual(count + 1, seen.Count)

                Assert.IsTrue(
                    ((make ()).QueryAsync owners.[1] (GraphQuery.ByPredicate "uses")).Result.Relations.IsEmpty
                )

            let file = Path.Combine(root, "graph", key owner + ".jsonl")
            let lines = File.ReadAllLines file
            Assert.AreEqual(1, lines.Length)

            let rows =
                Ado.query factory "SELECT stream, payload FROM nao_events" [] (fun row ->
                    Ado.getString row "stream", Ado.getString row "payload")
                |> _.Result

            Assert.AreEqual([ "graph/" + key owner ], rows |> List.map fst)

            for payload in (Array.toList lines) @ (rows |> List.map snd) do
                Assert.AreEqual(1, (FSharpJson.deserialize<GraphDocument> payload).Version)

                for secret in
                    [ "private-service"
                      "private-role"
                      "private-claim"
                      "private-value"
                      "MultiFactor"
                      "Owner" ] do
                    Assert.IsFalse(payload.Contains secret)

            Assert.IsTrue(seen |> Seq.forall ((=) "a uses b")))

    [<TestMethod>]
    member _.ForeignCorruptionCannotBlockOwnReadsOrWrites() =
        withStorage (fun root factory ->
            let other = owners.[1]

            for make, corrupt in
                [ (fun () -> GraphMemories.file root None),
                  (fun () -> File.AppendAllText(Path.Combine(root, "graph", key other + ".jsonl"), "{invalid\n"))
                  (fun () -> GraphMemories.ado factory None),
                  (fun () ->
                      Ado.executeNonQuery
                          factory
                          "UPDATE nao_events SET payload = 'invalid' WHERE stream = @stream"
                          [ "@stream", box ("graph/" + key other) ]
                      |> _.GetAwaiter().GetResult()
                      |> ignore) ] do
                let graph = make ()

                graph.UpsertNodeAsync other (node "foreign-private" "foreign" cutoff)
                |> _.GetAwaiter().GetResult()

                corrupt ()

                graph.UpsertNodeAsync owner (node "own" "own" cutoff)
                |> _.GetAwaiter().GetResult()

                Assert.AreEqual(1, ((make ()).GetByTypeAsync owner "thing").Result.Length)
                Assert.AreEqual(Ok 1, graph.DeleteOwnerAsync owner |> _.Result)

                Assert.ThrowsExactlyAsync<InvalidDataException>(fun () -> graph.GetByTypeAsync other "thing" :> Task)
                |> _.GetAwaiter().GetResult()
                |> ignore

                Assert.IsTrue(graph.DeleteOwnerAsync other |> _.Result |> Result.isError))

    [<TestMethod>]
    member _.AuthenticationEvidenceDoesNotChangePartitionAndDefaultExtractionIsScoped() =
        withStorage (fun root factory ->
            let principal =
                SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") [ GroupId.parse "group" ]

            let sameOwner =
                AuthorizationScope.tryCreate
                    principal
                    (Some(GroupId.parse "group"))
                    (WorkspaceId.parse "workspace")
                    (Some(SessionId.parse "session"))
                |> Option.get

            for graph in
                [ InMemoryGraphMemory.create None
                  GraphMemories.file root None
                  GraphMemories.ado factory None ] do
                let extracted = graph.ExtractRelationsAsync owner "a uses b" |> _.Result
                Assert.AreEqual(1, extracted.Length)

                Assert.AreEqual(
                    extracted,
                    (graph.QueryAsync sameOwner (GraphQuery.ByPredicate "uses")).Result.Relations
                )

                Assert.IsTrue((graph.QueryAsync owners.[1] (GraphQuery.ByPredicate "uses")).Result.Relations.IsEmpty)
                Assert.AreEqual(Ok 1, graph.DeleteOwnerAsync sameOwner |> _.Result)
                Assert.IsTrue((graph.QueryAsync owner (GraphQuery.ByPredicate "uses")).Result.Relations.IsEmpty))

    [<TestMethod>]
    member _.UnsupportedEventsRejectBeforeReadsWritesOrExtractionCallbacks() =
        let mutable appended = false
        let mutable extracted = false

        let unsupported: GraphDocument =
            { Version = 99
              Event = GraphEvent.DeleteOwner }

        let store =
            { Append = fun _ -> appended <- true
              LoadAll = fun () -> [ FSharpJson.serialize unsupported ] }

        let graph =
            PersistentGraphMemory.create
                (fun _ -> store)
                (Some(fun _ ->
                    extracted <- true
                    Task.FromResult []))

        Assert.ThrowsExactlyAsync<InvalidDataException>(fun () -> graph.GetByTypeAsync owner "thing" :> Task)
        |> _.GetAwaiter().GetResult()
        |> ignore

        Assert.ThrowsExactlyAsync<InvalidDataException>(fun () ->
            graph.UpsertNodeAsync owner (node "a" "a" cutoff) :> Task)
        |> _.GetAwaiter().GetResult()
        |> ignore

        Assert.ThrowsExactlyAsync<InvalidDataException>(fun () -> graph.ExtractRelationsAsync owner "a uses b" :> Task)
        |> _.GetAwaiter().GetResult()
        |> ignore

        Assert.IsFalse appended
        Assert.IsFalse extracted

    [<TestMethod>]
    member _.FailedAppendLeavesNoSpeculativeNodesRelationsOrDeletion() =
        let lines = ResizeArray<string>()
        let mutable failAppend = false

        let store =
            { Append =
                fun line ->
                    if failAppend then
                        raise (IOException "write failed")
                    else
                        lines.Add line
              LoadAll = fun () -> List.ofSeq lines }

        let make () =
            PersistentGraphMemory.create (fun _ -> store) None

        let graph = make ()

        graph.UpsertNodeAsync owner (node "original" "original" cutoff)
        |> _.GetAwaiter().GetResult()

        failAppend <- true

        Assert.ThrowsExactlyAsync<IOException>(fun () ->
            graph.UpsertNodeAsync owner (node "added" "added" cutoff) :> Task)
        |> _.GetAwaiter().GetResult()
        |> ignore

        Assert.ThrowsExactlyAsync<IOException>(fun () -> graph.ExtractRelationsAsync owner "a uses b" :> Task)
        |> _.GetAwaiter().GetResult()
        |> ignore

        Assert.IsTrue(graph.DeleteOwnerAsync owner |> _.Result |> Result.isError)

        for current in [ graph; make () ] do
            Assert.AreEqual([ "original" ], current.GetByTypeAsync owner "thing" |> _.Result |> List.map _.Id)
            Assert.IsTrue((current.QueryAsync owner (GraphQuery.ByPredicate "uses")).Result.Relations.IsEmpty)

        Assert.AreEqual(1, lines.Count)
