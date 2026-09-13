namespace Nao.Persistence.Tests

open System
open System.IO
open Microsoft.Data.Sqlite
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass>]
type MemoryAuthorizationTests() =
    let scope tenant user group workspace session =
        let principal =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse tenant)
                (UserId.parse user)
                (Some(ServiceId.parse "private-service"))
                [ GroupId.parse "group"; GroupId.parse "other-group" ]
                [ "private-role" ]
                (Map.ofList [ "private-claim", "private-claim-value" ])
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
          scope "other-tenant" "user" (Some "group") "workspace" (Some "session")
          scope "tenant" "other-user" (Some "group") "workspace" (Some "session")
          scope "tenant" "user" (Some "other-group") "workspace" (Some "session")
          scope "tenant" "user" None "workspace" (Some "session")
          scope "tenant" "user" (Some "group") "other-workspace" (Some "session")
          scope "tenant" "user" (Some "group") "workspace" (Some "other-session")
          scope "tenant" "user" (Some "group") "workspace" None
          scope "a/b" "c" None "workspace" None
          scope "a_b" "c" None "workspace" None
          scope "a" "b/c" None "workspace" None
          scope "a\nb" "c" None "workspace" None
          scope "a" "b\nc" None "workspace" None ]

    let withStorage action =
        let root =
            Path.Combine(Path.GetTempPath(), "nao-scoped-memory-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore
        let database = Path.Combine(root, "memory.db")

        let factory =
            DbConnectionFactory.ofFunc (fun () ->
                new SqliteConnection(sprintf "Data Source=%s;Pooling=False" database) :> Data.Common.DbConnection)

        try
            action root factory
        finally
            Directory.Delete(root, true)

    let key authorization =
        AuthorizationScope.identity authorization |> AuthorizationIdentity.storageKey

    let entry name value timestamp =
        { Key = name
          Value = value
          Timestamp = timestamp
          Tags = [ "tag" ] }

    [<TestMethod>]
    member _.AuthenticationEvidenceDoesNotChangeStorageOwnership() =
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

            for store in [ InMemoryStore.create (); MemoryStores.file root; MemoryStores.ado factory ] do
                let saved = entry "key" "value" DateTimeOffset.UtcNow
                store.SaveAsync owner saved |> _.GetAwaiter().GetResult()
                Assert.AreEqual([ saved ], store.RecallAllAsync sameOwner |> _.Result)
                Assert.AreEqual(Ok 1, store.DeleteOwnerAsync sameOwner |> _.Result)
                Assert.IsTrue((store.RecallAllAsync owner).Result.IsEmpty))

    [<TestMethod>]
    member _.EveryOperationAndReloadPreservesAllScopeDimensionsAcrossBackends() =
        withStorage (fun root factory ->
            let memory = InMemoryStore.create ()

            let backends =
                [ (fun () -> memory)
                  (fun () -> MemoryStores.file (Path.Combine(root, "files")))
                  (fun () -> MemoryStores.ado factory) ]

            let cutoff = DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)

            for make in backends do
                let store = make ()

                for index, scope in owners |> List.indexed do
                    store.SaveAsync scope (entry "old" "expired" (cutoff.AddTicks(-1)))
                    |> _.GetAwaiter().GetResult()

                    store.SaveAsync scope (entry "same" (string index) cutoff)
                    |> _.GetAwaiter().GetResult()

                    store.SaveAsync scope (entry "recent" "recent" (cutoff.AddTicks(1)))
                    |> _.GetAwaiter().GetResult()

                for index, scope in owners |> List.indexed do
                    let current = make ()
                    Assert.AreEqual(3, (current.RecallAllAsync scope).Result.Length)
                    let found = current.RecallAsync scope "AM" |> _.GetAwaiter().GetResult()
                    Assert.AreEqual([ entry "same" (string index) cutoff ], found)

                    current.SaveAsync scope (entry "same" "updated" cutoff)
                    |> _.GetAwaiter().GetResult()

                    Assert.AreEqual([ entry "same" "updated" cutoff ], (make ()).RecallAsync scope "same" |> _.Result)
                    current.ForgetAsync scope "recent" |> _.GetAwaiter().GetResult()
                    Assert.AreEqual(Ok 1, current.DeleteExpiredAsync scope cutoff |> _.Result)
                    Assert.AreEqual([ entry "same" "updated" cutoff ], (make ()).RecallAllAsync scope |> _.Result)
                    Assert.AreEqual(Ok 1, current.DeleteOwnerAsync scope |> _.Result)
                    Assert.AreEqual(Ok 0, current.DeleteOwnerAsync scope |> _.Result)
                    Assert.IsTrue(((make ()).RecallAllAsync scope).Result.IsEmpty)

                    for pending in owners |> List.skip (index + 1) do
                        Assert.AreEqual(3, ((make ()).RecallAllAsync pending).Result.Length)

                for scope in owners do
                    Assert.IsTrue(((make ()).RecallAllAsync scope).Result.IsEmpty))

    [<TestMethod>]
    member _.PersistentPartitionsContainNoAuthenticationEvidenceAndUseSchemaOne() =
        withStorage (fun root factory ->
            let directory = Path.Combine(root, "files")
            let stores = [ MemoryStores.file directory; MemoryStores.ado factory ]

            for store in stores do
                store.SaveAsync owner (entry "key" "value" DateTimeOffset.UtcNow)
                |> _.GetAwaiter().GetResult()

            let path = Path.Combine(directory, key owner + ".json")
            Assert.IsTrue(File.Exists path)
            let text = File.ReadAllText path
            use document = System.Text.Json.JsonDocument.Parse text
            Assert.AreEqual(1, document.RootElement.GetProperty("schemaVersion").GetInt32())

            let rows =
                Ado.query factory "SELECT owner_key, mem_key, mem_value, mem_tags FROM nao_memory" [] (fun row ->
                    [ "owner_key"; "mem_key"; "mem_value"; "mem_tags" ]
                    |> List.map (Ado.getString row))
                |> _.Result

            Assert.AreEqual(key owner, rows.Head.Head)

            let marker =
                Ado.query
                    factory
                    "SELECT schema_version FROM nao_schema_versions WHERE component = 'memory'"
                    []
                    (fun row -> Convert.ToInt32(row.GetValue(0)))
                |> _.Result

            Assert.AreEqual([ 1 ], marker)
            let stored = text + (rows |> List.collect id |> String.concat "\n")

            for excluded in [ "private-service"; "private-role"; "private-claim"; "MultiFactor" ] do
                Assert.IsFalse(stored.Contains excluded, sprintf "Memory persistence leaked %s." excluded))

    [<TestMethod>]
    member _.AdoRejectsIncompatibleVersionOneTableWithoutRewritingIt() =
        withStorage (fun _ factory ->
            Ado.executeNonQuery
                factory
                "CREATE TABLE nao_schema_versions (component TEXT NOT NULL PRIMARY KEY, schema_version INTEGER NOT NULL)"
                []
            |> _.GetAwaiter().GetResult()
            |> ignore

            Ado.executeNonQuery factory "INSERT INTO nao_schema_versions VALUES ('memory', 1)" []
            |> _.GetAwaiter().GetResult()
            |> ignore

            Ado.executeNonQuery
                factory
                "CREATE TABLE nao_memory (agent TEXT NOT NULL, mem_key TEXT NOT NULL, mem_value TEXT NOT NULL, mem_ts TEXT NOT NULL, mem_tags TEXT NOT NULL, PRIMARY KEY (agent, mem_key))"
                []
            |> _.GetAwaiter().GetResult()
            |> ignore

            let store = MemoryStores.ado factory

            let error =
                Assert.ThrowsExactlyAsync<InvalidDataException>(fun () ->
                    store.SaveAsync owner (entry "key" "value" DateTimeOffset.UtcNow) :> System.Threading.Tasks.Task)
                |> _.Result

            StringAssert.Contains(error.Message, "incompatible shape")

            let columns =
                Ado.query factory "PRAGMA table_info(nao_memory)" [] (fun row -> Ado.getString row "name")
                |> _.Result

            Assert.IsTrue(columns |> List.contains "agent")
            Assert.IsFalse(columns |> List.contains "owner_key"))

    [<TestMethod>]
    member _.CorruptionInAnotherScopeCannotExposeItsKeysOrBlockOwnMutations() =
        withStorage (fun _ factory ->
            let store = MemoryStores.ado factory
            let other = owners.[1]
            store.RecallAllAsync owner |> _.GetAwaiter().GetResult() |> ignore

            Ado.executeNonQuery
                factory
                "INSERT INTO nao_memory (owner_key, mem_key, mem_value, mem_ts, mem_tags) VALUES (@owner, 'foreign-private-key', 'foreign', 'invalid-time', '[]')"
                [ "@owner", box (key other) ]
            |> _.GetAwaiter().GetResult()
            |> ignore

            store.SaveAsync owner (entry "own" "own" DateTimeOffset.UtcNow)
            |> _.GetAwaiter().GetResult()

            Assert.AreEqual(1, (store.RecallAllAsync owner).Result.Length)
            Assert.AreEqual(1, (store.RecallAsync owner "own").Result.Length)
            store.ForgetAsync owner "own" |> _.GetAwaiter().GetResult()
            Assert.IsTrue((store.RecallAllAsync owner).Result.IsEmpty)

            store.SaveAsync owner (entry "old" "old" DateTimeOffset.MinValue)
            |> _.GetAwaiter().GetResult()

            Assert.AreEqual(Ok 1, store.DeleteExpiredAsync owner DateTimeOffset.UtcNow |> _.Result)

            store.SaveAsync owner (entry "own" "own" DateTimeOffset.UtcNow)
            |> _.GetAwaiter().GetResult()

            Assert.AreEqual(Ok 1, store.DeleteOwnerAsync owner |> _.Result)

            Assert.ThrowsExactlyAsync<InvalidDataException>(fun () ->
                store.SaveAsync other (entry "new" "value" DateTimeOffset.UtcNow) :> System.Threading.Tasks.Task)
            |> _.GetAwaiter().GetResult()
            |> ignore

            for result in
                [ store.DeleteOwnerAsync other |> _.Result
                  store.DeleteExpiredAsync other DateTimeOffset.UtcNow |> _.Result ] do
                match result with
                | Ok _ -> Assert.Fail("Corrupt owner data must prevent destructive writes.")
                | Error failure -> StringAssert.Contains(failure.Message, "foreign-private-key")

            let keys =
                Ado.query factory "SELECT mem_key FROM nao_memory" [] (fun row -> Ado.getString row "mem_key")
                |> _.Result

            Assert.AreEqual([ "foreign-private-key" ], keys))
