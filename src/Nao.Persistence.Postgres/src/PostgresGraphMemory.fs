namespace Nao.Persistence.Postgres

open System
open System.Data
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Nao.Agents
open Nao.Persistence
open Npgsql

type PostgresGraphConfig = private { Schema: string }

[<RequireQualifiedAccess>]
module PostgresGraphConfig =
    let create schema =
        if isNull schema || not (Regex.IsMatch(schema, "^[a-z][a-z0-9_]{0,62}$")) then
            invalidArg (nameof schema) "PostgreSQL graph schema must be a canonical SQL identifier."

        { Schema = schema }

    let internal schema value = value.Schema

type PostgresGraphHealth =
    { SchemaRevision: int
      OwnerRevision: int64
      NodeCount: int64
      RelationCount: int64
      CheckedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module PostgresGraphMemory =
    let private quoted value = "\"" + value + "\""
    let private table schema name = $"{quoted schema}.{quoted name}"

    let private parameter (command: NpgsqlCommand) (name: string) value =
        command.Parameters.AddWithValue(name, box value) |> ignore

    let private executeAsync (command: NpgsqlCommand) cancellationToken =
        task {
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            return ()
        }

    let private ownerKey scope =
        AuthorizationScope.identity scope |> AuthorizationIdentity.storageKey

    let private initializeAsync (dataSource: NpgsqlDataSource) schema cancellationToken =
        task {
            use! connection = dataSource.OpenConnectionAsync cancellationToken
            use command = connection.CreateCommand()

            command.CommandText <-
                $"""
                CREATE SCHEMA IF NOT EXISTS {quoted schema};
                CREATE TABLE IF NOT EXISTS {table schema "graph_owners"} (
                    owner_key text PRIMARY KEY,
                    revision bigint NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS {table schema "graph_nodes"} (
                    owner_key text NOT NULL REFERENCES {table schema "graph_owners"}(owner_key) ON DELETE CASCADE,
                    node_id text NOT NULL,
                    entity_type text NOT NULL,
                    payload jsonb NOT NULL,
                    PRIMARY KEY (owner_key, node_id)
                );
                CREATE TABLE IF NOT EXISTS {table schema "graph_aliases"} (
                    owner_key text NOT NULL,
                    alias text NOT NULL,
                    node_id text NOT NULL,
                    PRIMARY KEY (owner_key, alias),
                    FOREIGN KEY (owner_key, node_id) REFERENCES {table schema "graph_nodes"}(owner_key, node_id) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS {table schema "graph_properties"} (
                    owner_key text NOT NULL,
                    node_id text NOT NULL,
                    name text NOT NULL,
                    value text NOT NULL,
                    PRIMARY KEY (owner_key, node_id, name),
                    FOREIGN KEY (owner_key, node_id) REFERENCES {table schema "graph_nodes"}(owner_key, node_id) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS {table schema "graph_relations"} (
                    owner_key text NOT NULL REFERENCES {table schema "graph_owners"}(owner_key) ON DELETE CASCADE,
                    relation_id text NOT NULL,
                    subject_id text NOT NULL,
                    predicate text NOT NULL,
                    object_id text NOT NULL,
                    payload jsonb NOT NULL,
                    PRIMARY KEY (owner_key, relation_id),
                    FOREIGN KEY (owner_key, subject_id) REFERENCES {table schema "graph_nodes"}(owner_key, node_id) ON DELETE CASCADE,
                    FOREIGN KEY (owner_key, object_id) REFERENCES {table schema "graph_nodes"}(owner_key, node_id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS graph_nodes_type_idx ON {table schema "graph_nodes"} (owner_key, entity_type);
                CREATE INDEX IF NOT EXISTS graph_properties_idx ON {table schema "graph_properties"} (owner_key, name, value);
                CREATE INDEX IF NOT EXISTS graph_relations_predicate_idx ON {table schema "graph_relations"} (owner_key, predicate);
                CREATE INDEX IF NOT EXISTS graph_relations_subject_idx ON {table schema "graph_relations"} (owner_key, subject_id);
                CREATE INDEX IF NOT EXISTS graph_relations_object_idx ON {table schema "graph_relations"} (owner_key, object_id);
                """

            do! executeAsync command cancellationToken
        }

    let checkHealthAsync (dataSource: NpgsqlDataSource) config scope (cancellationToken: CancellationToken) =
        task {
            let schema = PostgresGraphConfig.schema config
            do! initializeAsync dataSource schema cancellationToken
            let ownersTable = table schema "graph_owners"
            let nodesTable = table schema "graph_nodes"
            let relationsTable = table schema "graph_relations"
            use! connection = dataSource.OpenConnectionAsync cancellationToken
            use command = connection.CreateCommand()

            command.CommandText <-
                $"""
                SELECT
                    COALESCE((SELECT revision FROM {ownersTable} WHERE owner_key = @owner), 0),
                    (SELECT COUNT(*) FROM {nodesTable} WHERE owner_key = @owner),
                    (SELECT COUNT(*) FROM {relationsTable} WHERE owner_key = @owner)
                """

            parameter command "owner" (ownerKey scope)
            use! reader = command.ExecuteReaderAsync cancellationToken
            let! found = reader.ReadAsync cancellationToken

            if not found then
                invalidOp "PostgreSQL graph health query returned no row."

            return
                { SchemaRevision = 1
                  OwnerRevision = reader.GetInt64 0
                  NodeCount = reader.GetInt64 1
                  RelationCount = reader.GetInt64 2
                  CheckedAt = DateTimeOffset.UtcNow }
        }

    let private readSnapshotAsync
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction option)
        schema
        scope
        (cancellationToken: CancellationToken)
        =
        task {
            let nodesTable = table schema "graph_nodes"
            let relationsTable = table schema "graph_relations"

            let read (tableName: string) (deserialize: string -> 'value) =
                task {
                    use command = connection.CreateCommand()
                    transaction |> Option.iter (fun value -> command.Transaction <- value)
                    command.CommandText <- $"SELECT payload::text FROM {tableName} WHERE owner_key = @owner ORDER BY 1"
                    parameter command "owner" (ownerKey scope)
                    use! reader = command.ExecuteReaderAsync cancellationToken
                    let values = ResizeArray<_>()

                    while! reader.ReadAsync cancellationToken do
                        values.Add(deserialize (reader.GetString 0))

                    return List.ofSeq values
                }

            let! nodes = read nodesTable FSharpJson.deserialize<GraphNode>
            let! relations = read relationsTable FSharpJson.deserialize<GraphRelation>

            return
                { Owner = AuthorizationScope.identity scope
                  CreatedAt = DateTimeOffset.UtcNow
                  Nodes = nodes
                  Relations = relations }
        }

    let private writeSnapshotAsync
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        schema
        scope
        snapshot
        cancellationToken
        =
        task {
            GraphContracts.validateSnapshot scope snapshot
            let nodesTable = table schema "graph_nodes"
            let aliasesTable = table schema "graph_aliases"
            let propertiesTable = table schema "graph_properties"
            let relationsTable = table schema "graph_relations"
            let ownersTable = table schema "graph_owners"
            use clear = connection.CreateCommand()
            clear.Transaction <- transaction
            clear.CommandText <- $"DELETE FROM {nodesTable} WHERE owner_key = @owner"
            parameter clear "owner" (ownerKey scope)
            do! executeAsync clear cancellationToken

            for node in snapshot.Nodes do
                use command = connection.CreateCommand()
                command.Transaction <- transaction

                command.CommandText <-
                    $"INSERT INTO {nodesTable} (owner_key,node_id,entity_type,payload) VALUES (@owner,@id,@type,@payload::jsonb)"

                parameter command "owner" (ownerKey scope)
                parameter command "id" node.Id
                parameter command "type" node.EntityType
                parameter command "payload" (FSharpJson.serialize node)
                do! executeAsync command cancellationToken

                for alias in node.Aliases do
                    use aliasCommand = connection.CreateCommand()
                    aliasCommand.Transaction <- transaction

                    aliasCommand.CommandText <-
                        $"INSERT INTO {aliasesTable} (owner_key,alias,node_id) VALUES (@owner,@alias,@id)"

                    parameter aliasCommand "owner" (ownerKey scope)
                    parameter aliasCommand "alias" alias
                    parameter aliasCommand "id" node.Id
                    do! executeAsync aliasCommand cancellationToken

                for KeyValue(name, value) in node.Properties do
                    use property = connection.CreateCommand()
                    property.Transaction <- transaction

                    property.CommandText <-
                        $"INSERT INTO {propertiesTable} (owner_key,node_id,name,value) VALUES (@owner,@id,@name,@value)"

                    parameter property "owner" (ownerKey scope)
                    parameter property "id" node.Id
                    parameter property "name" name
                    parameter property "value" value
                    do! executeAsync property cancellationToken

            for relation in snapshot.Relations do
                use command = connection.CreateCommand()
                command.Transaction <- transaction

                command.CommandText <-
                    $"INSERT INTO {relationsTable} (owner_key,relation_id,subject_id,predicate,object_id,payload) VALUES (@owner,@id,@subject,@predicate,@object,@payload::jsonb)"

                parameter command "owner" (ownerKey scope)
                parameter command "id" relation.Id
                parameter command "subject" relation.Subject
                parameter command "predicate" relation.Predicate
                parameter command "object" relation.Object
                parameter command "payload" (FSharpJson.serialize relation)
                do! executeAsync command cancellationToken

            use revision = connection.CreateCommand()
            revision.Transaction <- transaction
            revision.CommandText <- $"UPDATE {ownersTable} SET revision = revision + 1 WHERE owner_key = @owner"
            parameter revision "owner" (ownerKey scope)
            do! executeAsync revision cancellationToken
        }

    let createAsync
        (dataSource: NpgsqlDataSource)
        config
        (extractor: (string -> Task<GraphExtraction>) option)
        (cancellationToken: CancellationToken)
        =
        task {
            let schema = PostgresGraphConfig.schema config
            let ownersTable = table schema "graph_owners"
            do! initializeAsync dataSource schema cancellationToken

            let mutate scope operation =
                task {
                    use! connection = dataSource.OpenConnectionAsync()
                    use! transaction = connection.BeginTransactionAsync(IsolationLevel.ReadCommitted)
                    use lockOwner = connection.CreateCommand()
                    lockOwner.Transaction <- transaction

                    lockOwner.CommandText <-
                        $"INSERT INTO {ownersTable} (owner_key) VALUES (@owner) ON CONFLICT DO NOTHING; SELECT revision FROM {ownersTable} WHERE owner_key = @owner FOR UPDATE"

                    parameter lockOwner "owner" (ownerKey scope)
                    let! _ = lockOwner.ExecuteScalarAsync()
                    let! snapshot = readSnapshotAsync connection (Some transaction) schema scope CancellationToken.None
                    let inner = InMemoryGraphMemory.create extractor
                    do! inner.ReplaceAsync scope snapshot
                    let! result = operation inner
                    let! updated = inner.SnapshotAsync scope
                    do! writeSnapshotAsync connection transaction schema scope updated CancellationToken.None
                    do! transaction.CommitAsync()
                    return result
                }

            let read scope operation =
                task {
                    use! connection = dataSource.OpenConnectionAsync()
                    let! snapshot = readSnapshotAsync connection None schema scope CancellationToken.None
                    let inner = InMemoryGraphMemory.create None
                    do! inner.ReplaceAsync scope snapshot
                    return! operation inner
                }

            return
                { UpsertNodeAsync = fun scope node -> mutate scope (fun graph -> graph.UpsertNodeAsync scope node)
                  AddRelationAsync =
                    fun scope relation -> mutate scope (fun graph -> graph.AddRelationAsync scope relation)
                  QueryAsync = fun scope query -> read scope (fun graph -> graph.QueryAsync scope query)
                  RemoveNodeAsync = fun scope id -> mutate scope (fun graph -> graph.RemoveNodeAsync scope id)
                  RemoveRelationAsync = fun scope id -> mutate scope (fun graph -> graph.RemoveRelationAsync scope id)
                  MergeEntityAsync = fun scope merge -> mutate scope (fun graph -> graph.MergeEntityAsync scope merge)
                  SplitEntityAsync = fun scope split -> mutate scope (fun graph -> graph.SplitEntityAsync scope split)
                  SnapshotAsync = fun scope -> read scope (fun graph -> graph.SnapshotAsync scope)
                  ReplaceAsync = fun scope snapshot -> mutate scope (fun graph -> graph.ReplaceAsync scope snapshot)
                  GetByTypeAsync =
                    fun scope entityType -> read scope (fun graph -> graph.GetByTypeAsync scope entityType)
                  ExtractAsync = fun scope text -> mutate scope (fun graph -> graph.ExtractAsync scope text)
                  DeleteOwnerAsync =
                    fun scope ->
                        task {
                            use! connection = dataSource.OpenConnectionAsync()
                            use! transaction = connection.BeginTransactionAsync(IsolationLevel.ReadCommitted)
                            use lockOwner = connection.CreateCommand()
                            lockOwner.Transaction <- transaction

                            lockOwner.CommandText <-
                                $"SELECT revision FROM {ownersTable} WHERE owner_key = @owner FOR UPDATE"

                            parameter lockOwner "owner" (ownerKey scope)
                            let! existing = lockOwner.ExecuteScalarAsync()

                            if isNull existing then
                                do! transaction.CommitAsync()
                                return Ok 0
                            else
                                let! snapshot =
                                    readSnapshotAsync connection (Some transaction) schema scope CancellationToken.None

                                use command = connection.CreateCommand()
                                command.Transaction <- transaction
                                command.CommandText <- $"DELETE FROM {ownersTable} WHERE owner_key = @owner"
                                parameter command "owner" (ownerKey scope)
                                do! executeAsync command CancellationToken.None
                                do! transaction.CommitAsync()
                                return Ok(snapshot.Nodes.Length + snapshot.Relations.Length)
                        }
                  DeleteExpiredAsync =
                    fun scope before -> mutate scope (fun graph -> graph.DeleteExpiredAsync scope before) }
        }
