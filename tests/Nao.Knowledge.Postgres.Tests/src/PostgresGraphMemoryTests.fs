namespace Nao.Knowledge.Postgres.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence.Postgres
open Npgsql

[<TestClass>]
type PostgresGraphMemoryTests() =
    let connectionString =
        Environment.GetEnvironmentVariable "NAO_TEST_POSTGRES"
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let tenant = TenantId.parse "graph-tenant"
    let workspace = WorkspaceId.parse "graph-workspace"
    let now = DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero)

    let scope user =
        let principal = SecurityPrincipal.create tenant (UserId.parse user) []
        AuthorizationScope.tryCreate principal None workspace None |> Option.get

    let node id =
        { Id = id
          EntityType = "service"
          Aliases = Set.empty
          Properties = Map.ofList [ "environment", "production" ]
          Provenance = []
          CreatedAt = now
          UpdatedAt = now }

    let relation operation subject object' =
        let provenance =
            { SourceVersionId = "inventory-v1"
              ExtractionOperationId = operation
              EvidenceAddresses = [ $"inventory:{operation}" ] }

        { Id = GraphContracts.relationId subject "depends-on" object' "inventory-v1" operation
          Subject = subject
          Predicate = "depends-on"
          Object = object'
          Direction = GraphRelationDirection.Directed
          Properties = Map.empty
          Validity = { ValidFrom = Some now; ValidTo = None }
          Confidence = 1.0
          Provenance = provenance
          AssertedAt = now }

    [<TestMethod>]
    [<TestCategory("ExternalDatabase")>]
    [<TestCategory("Container")>]
    member _.``PostgreSQL graph enforces isolation lifecycle restore and physical deletion``() =
        match connectionString with
        | None -> Assert.Inconclusive "Set NAO_TEST_POSTGRES to run PostgreSQL graph integration tests."
        | Some connectionString ->
            let schema = "nao_graph_" + Guid.NewGuid().ToString("N").Substring(0, 16)
            let config = PostgresGraphConfig.create schema
            use dataSource = NpgsqlDataSource.Create connectionString
            let owner = scope "owner"
            let outsider = scope "outsider"

            try
                let graph =
                    PostgresGraphMemory.createAsync dataSource config None CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                for id in [ "api"; "database"; "queue" ] do
                    graph.UpsertNodeAsync owner (node id) |> _.GetAwaiter().GetResult()

                let database = relation "database-edge" "api" "database"
                let queue = relation "queue-edge" "api" "queue"
                graph.AddRelationAsync owner database |> _.GetAwaiter().GetResult()
                graph.AddRelationAsync owner queue |> _.GetAwaiter().GetResult()

                Assert.AreEqual(
                    2,
                    (graph.QueryAsync owner (GraphQuery.ByPredicate "depends-on")).Result.Relations.Length
                )

                Assert.IsTrue(
                    (graph.QueryAsync outsider (GraphQuery.ByPredicate "depends-on")).Result.Relations.IsEmpty
                )

                let backup = graph.SnapshotAsync owner |> _.Result

                graph.MergeEntityAsync
                    owner
                    { SourceIds = Set.ofList [ "database"; "queue" ]
                      Target =
                        { node "storage" with
                            Aliases = Set.ofList [ "database"; "queue" ] } }
                |> _.GetAwaiter().GetResult()

                let merged = graph.QueryAsync owner (GraphQuery.ByEntity "storage") |> _.Result

                CollectionAssert.AreEquivalent(
                    [| database.Id; queue.Id |],
                    merged.Relations |> List.map _.Id |> List.toArray
                )

                graph.ReplaceAsync owner backup |> _.GetAwaiter().GetResult()

                let health =
                    PostgresGraphMemory.checkHealthAsync dataSource config owner CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                Assert.AreEqual(1, health.SchemaRevision)
                Assert.IsTrue(health.OwnerRevision >= 6L)
                Assert.AreEqual(3L, health.NodeCount)
                Assert.AreEqual(2L, health.RelationCount)
                Assert.AreEqual(Ok 5, graph.DeleteOwnerAsync owner |> _.Result)
                Assert.AreEqual(Ok 0, graph.DeleteOwnerAsync owner |> _.Result)

                let erased =
                    PostgresGraphMemory.checkHealthAsync dataSource config owner CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                Assert.AreEqual(0L, erased.NodeCount)
                Assert.AreEqual(0L, erased.RelationCount)
            finally
                use connection = dataSource.OpenConnection()
                use command = connection.CreateCommand()
                command.CommandText <- $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"
                command.ExecuteNonQuery() |> ignore

    [<TestMethod>]
    [<TestCategory("ExternalDatabase")>]
    [<TestCategory("Container")>]
    member _.``PostgreSQL graph serializes concurrent owner mutations without lost updates``() =
        match connectionString with
        | None -> Assert.Inconclusive "Set NAO_TEST_POSTGRES to run PostgreSQL graph integration tests."
        | Some connectionString ->
            let schema = "nao_graph_" + Guid.NewGuid().ToString("N").Substring(0, 16)
            let config = PostgresGraphConfig.create schema
            use dataSource = NpgsqlDataSource.Create connectionString
            let owner = scope "concurrent-owner"

            try
                let graph =
                    PostgresGraphMemory.createAsync dataSource config None CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                [ 1..12 ]
                |> List.map (fun index -> graph.UpsertNodeAsync owner (node $"node-{index}"))
                |> Task.WhenAll
                |> _.GetAwaiter().GetResult()
                |> ignore

                let snapshot = graph.SnapshotAsync owner |> _.Result
                Assert.AreEqual(12, snapshot.Nodes.Length)
                Assert.AreEqual(12, snapshot.Nodes |> List.map _.Id |> List.distinct |> List.length)

                let health =
                    PostgresGraphMemory.checkHealthAsync dataSource config owner CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                Assert.AreEqual(12L, health.OwnerRevision)
                Assert.AreEqual(12L, health.NodeCount)
            finally
                use connection = dataSource.OpenConnection()
                use command = connection.CreateCommand()
                command.CommandText <- $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"
                command.ExecuteNonQuery() |> ignore
