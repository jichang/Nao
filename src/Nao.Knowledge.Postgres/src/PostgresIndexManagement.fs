namespace Nao.Knowledge.Postgres

open System
open System.Data
open System.Threading
open System.Threading.Tasks
open Nao.Agents
open Nao.Knowledge
open Npgsql
open PostgresIndexSupport

module internal PostgresIndexManagement =
    let ensureCreatedAsync
        (dataSource: NpgsqlDataSource)
        schema
        tableName
        initializeAsync
        (cancellationToken: CancellationToken)
        =
        task {
            use! connection = dataSource.OpenConnectionAsync cancellationToken
            use exists = connection.CreateCommand()
            exists.CommandText <- "SELECT to_regclass(@relation_name) IS NOT NULL"
            addParameter exists "relation_name" (schema + "." + tableName)
            let! existed = exists.ExecuteScalarAsync cancellationToken
            do! initializeAsync cancellationToken

            return
                if Convert.ToBoolean existed then
                    KnowledgeIndexCreationResult.AlreadyExists
                else
                    KnowledgeIndexCreationResult.Created
        }

    let checkHealthAsync
        (dataSource: NpgsqlDataSource)
        schema
        tableName
        sourceStateTableName
        schemaRevision
        (cancellationToken: CancellationToken)
        =
        task {
            use! connection = dataSource.OpenConnectionAsync cancellationToken
            use exists = connection.CreateCommand()
            exists.CommandText <- "SELECT to_regclass(@relation_name) IS NOT NULL"
            addParameter exists "relation_name" (schema + "." + tableName)
            let! relationExists = exists.ExecuteScalarAsync cancellationToken
            let checkedAt = DateTimeOffset.UtcNow

            if not (Convert.ToBoolean relationExists) then
                return
                    KnowledgeIndexHealth.create
                        KnowledgeIndexHealthStatus.Unavailable
                        None
                        None
                        None
                        checkedAt
                        [ $"PostgreSQL relation '{schema}.{tableName}' does not exist." ]
            else
                use sourceStateExists = connection.CreateCommand()
                sourceStateExists.CommandText <- "SELECT to_regclass(@relation_name) IS NOT NULL"
                addParameter sourceStateExists "relation_name" (schema + "." + sourceStateTableName)
                let! sourceStateRelationExists = sourceStateExists.ExecuteScalarAsync cancellationToken

                if not (Convert.ToBoolean sourceStateRelationExists) then
                    return
                        KnowledgeIndexHealth.create
                            KnowledgeIndexHealthStatus.Degraded
                            (Some 1)
                            (Some schemaRevision)
                            None
                            checkedAt
                            [ $"PostgreSQL source-state relation '{schema}.{sourceStateTableName}' does not exist." ]
                else
                    use count = connection.CreateCommand()

                    count.CommandText <-
                        $"""
                        SELECT COUNT(*)
                        FROM {qualified schema tableName} AS records
                        WHERE {activeSourcePredicate (qualified schema sourceStateTableName) "records"}
                        """

                    let! records = count.ExecuteScalarAsync cancellationToken

                    return
                        KnowledgeIndexHealth.create
                            KnowledgeIndexHealthStatus.Healthy
                            (Some 1)
                            (Some schemaRevision)
                            (Some(Convert.ToInt64 records))
                            checkedAt
                            []
        }

    let backupAsync
        (dataSource: NpgsqlDataSource)
        schema
        tableName
        sourceStateTableName
        selectColumns
        readRecord
        (authorization: AuthorizationScope)
        namespaceName
        (cancellationToken: CancellationToken)
        =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            let identity = AuthorizationScope.identity authorization
            use! connection = dataSource.OpenConnectionAsync cancellationToken
            use! transaction = connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            use command = connection.CreateCommand()
            command.Transaction <- transaction

            command.CommandText <-
                $"""
                SELECT {selectColumns}
                FROM {qualified schema tableName} AS records
                WHERE records.owner_key = @owner_key
                  AND records.namespace = @namespace
                  AND {activeSourcePredicate (qualified schema sourceStateTableName) "records"}
                ORDER BY records.chunk_id
                """

            addParameter command "owner_key" (AuthorizationIdentity.storageKey identity)
            addParameter command "namespace" (KnowledgeIndexNamespace.value namespaceName)
            use! reader = command.ExecuteReaderAsync cancellationToken
            let records = ResizeArray<_>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync cancellationToken

                if found then
                    records.Add(readRecord reader)
                else
                    reading <- false

            do! reader.CloseAsync()
            do! transaction.CommitAsync cancellationToken

            return KnowledgeIndexBackup.create namespaceName identity DateTimeOffset.UtcNow (List.ofSeq records)
        }

    let restoreAsync rebuildAsync authorization backup cancellationToken =
        task {
            let backup = KnowledgeIndexBackup.requireOwner authorization backup

            return!
                rebuildAsync
                    authorization
                    (KnowledgeIndexBackup.namespaceName backup)
                    (KnowledgeIndexBackup.records backup)
                    cancellationToken
        }

    let unsupportedMigrationAsync (_: KnowledgeIndexMigrationPlan) (_: CancellationToken) : Task =
        Task.FromException(
            InvalidOperationException "The PostgreSQL index schema is already at its configured revision."
        )
