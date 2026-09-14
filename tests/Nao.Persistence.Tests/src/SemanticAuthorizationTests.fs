namespace Nao.Persistence.Tests

open System
open System.IO
open Microsoft.Data.Sqlite
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type SemanticAuthorizationTests() =
    let scope tenant user group workspace session =
        let principal =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse tenant)
                (UserId.parse user)
                (Some(ServiceId.parse "private-service"))
                [ GroupId.parse "group" ]
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
          scope "other-tenant" "user" (Some "group") "workspace" (Some "session")
          scope "tenant" "other-user" (Some "group") "workspace" (Some "session")
          scope "tenant" "user" None "workspace" (Some "session")
          scope "tenant" "user" (Some "group") "other-workspace" (Some "session")
          scope "tenant" "user" (Some "group") "workspace" None
          scope "a/b" "c" None "workspace" None
          scope "a_b" "c" None "workspace" None ]

    let storageKey value =
        AuthorizationScope.identity value |> AuthorizationIdentity.storageKey

    let withStorage action =
        let directory =
            Path.Combine(Path.GetTempPath(), "nao-semantic-scope-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory directory |> ignore
        let database = Path.Combine(directory, "semantic.db")

        let factory =
            DbConnectionFactory.ofFunc (fun () ->
                new SqliteConnection(sprintf "Data Source=%s;Pooling=False" database) :> Data.Common.DbConnection)

        try
            action directory factory
        finally
            Directory.Delete(directory, true)

    [<TestMethod>]
    member _.AdoSemanticMemoryRejectsIncompatibleVersionOneTableBeforeMutation() =
        withStorage (fun _ factory ->
            Ado.executeNonQuery
                factory
                "CREATE TABLE nao_schema_versions (component TEXT NOT NULL PRIMARY KEY, schema_version INTEGER NOT NULL)"
                []
            |> _.GetAwaiter().GetResult()
            |> ignore

            Ado.executeNonQuery factory "INSERT INTO nao_schema_versions VALUES ('semantic-memory', 1)" []
            |> _.GetAwaiter().GetResult()
            |> ignore

            Ado.executeNonQuery
                factory
                "CREATE TABLE nao_semantic (agent TEXT NOT NULL, sem_key TEXT NOT NULL, sem_content TEXT NOT NULL, sem_embedding TEXT NOT NULL, sem_ts TEXT NOT NULL, sem_tags TEXT NOT NULL, PRIMARY KEY (agent, sem_key))"
                []
            |> _.GetAwaiter().GetResult()
            |> ignore

            let memory = SemanticMemories.ado (SimpleEmbeddingProvider.create ()) factory

            let error =
                Assert.ThrowsExactlyAsync<InvalidDataException>(fun () ->
                    memory.StoreAsync owner "key" "value" :> Threading.Tasks.Task)
                |> _.Result

            StringAssert.Contains(error.Message, "incompatible shape")

            let columns =
                Ado.query factory "PRAGMA table_info(nao_semantic)" [] (fun row -> Ado.getString row "name")
                |> _.Result

            Assert.IsTrue(columns |> List.contains "agent")
            Assert.IsFalse(columns |> List.contains "owner_key"))

    [<TestMethod>]
    member _.SemanticRetrievalIsolatedByCompleteScopeAndReloadsPersistentStores() =
        withStorage (fun root factory ->
            let provider = SimpleEmbeddingProvider.create ()
            let memory = InMemorySemanticMemory.create provider

            let backends =
                [ (fun () -> memory)
                  (fun () -> SemanticMemories.file provider (Path.Combine(root, "files")))
                  (fun () -> SemanticMemories.ado provider factory) ]

            for make in backends do
                for index, scope in owners |> List.indexed do
                    (make ()).StoreAsync scope "same" (sprintf "scope %d private fact" index)
                    |> _.GetAwaiter().GetResult()

                for index, scope in owners |> List.indexed do
                    let entries = (make ()).RetrieveAsync scope "private fact" 10 |> _.Result

                    Assert.AreEqual(
                        [ sprintf "scope %d private fact" index ],
                        entries |> List.map (fun item -> item.Content)
                    )

                    Assert.AreEqual(Ok 1, (make ()).DeleteOwnerAsync scope |> _.Result)
                    Assert.IsTrue(((make ()).RetrieveAsync scope "private fact" 10).Result.IsEmpty))

    [<TestMethod>]
    member _.PersistentSemanticStorageExcludesAuthenticationEvidenceAndScopesCorruption() =
        withStorage (fun root factory ->
            let provider = SimpleEmbeddingProvider.create ()
            let fileMemory = SemanticMemories.file provider root
            let adoMemory = SemanticMemories.ado provider factory

            fileMemory.StoreAsync owner "own" "own private fact"
            |> _.GetAwaiter().GetResult()

            adoMemory.StoreAsync owner "own" "own private fact"
            |> _.GetAwaiter().GetResult()

            let document = Path.Combine(root, storageKey owner + ".json")
            Assert.IsTrue(File.Exists document)
            let serialized = File.ReadAllText document
            use json = System.Text.Json.JsonDocument.Parse serialized
            Assert.AreEqual(1, json.RootElement.GetProperty("schemaVersion").GetInt32())

            for evidence in [ "private-service"; "private-role"; "private-claim"; "MultiFactor" ] do
                Assert.IsFalse(serialized.Contains evidence, sprintf "Semantic persistence leaked %s." evidence)

            let other = owners.[1]

            Ado.executeNonQuery
                factory
                "INSERT INTO nao_semantic (owner_key, sem_key, sem_content, sem_embedding, sem_ts, sem_tags) VALUES (@owner, 'foreign-private-key', 'foreign', 'not-json', 'invalid-time', '[]')"
                [ "@owner", box (storageKey other) ]
            |> _.GetAwaiter().GetResult()
            |> ignore

            adoMemory.RemoveAsync owner "own" |> _.GetAwaiter().GetResult()
            Assert.IsTrue((adoMemory.RetrieveAsync owner "own" 10).Result.IsEmpty)

            let error =
                Assert.ThrowsExactlyAsync<InvalidDataException>(fun () ->
                    adoMemory.StoreAsync other "new" "new fact" :> Threading.Tasks.Task)
                |> _.Result

            StringAssert.Contains(error.Message, "foreign-private-key"))
