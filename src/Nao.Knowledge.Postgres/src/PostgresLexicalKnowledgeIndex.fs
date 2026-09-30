namespace Nao.Knowledge.Postgres

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Nao.Agents
open Nao.Knowledge
open Npgsql
open NpgsqlTypes
open PostgresIndexSupport

[<RequireQualifiedAccess>]
module PostgresLexicalKnowledgeIndex =
    let private qualified config table =
        PostgresIndexSupport.qualified (PostgresLexicalIndexConfig.schema config) table

    let private initializeAsync
        (dataSource: NpgsqlDataSource)
        (config: PostgresLexicalIndexConfig)
        (cancellationToken: CancellationToken)
        =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            let schema = PostgresLexicalIndexConfig.schema config
            let tableName = PostgresLexicalIndexConfig.tableName config
            let table = qualified config tableName
            let sourceStateTable = qualified config (sourceStateTableName tableName)
            let markerTable = qualified config "lexical_indexes"

            let textSearchConfiguration =
                PostgresLexicalIndexConfig.textSearchConfiguration config

            let configurationKey =
                ContentHash.computeText textSearchConfiguration |> ContentHash.value

            use! connection = dataSource.OpenConnectionAsync cancellationToken
            use validate = connection.CreateCommand()
            validate.CommandText <- "SELECT CAST(@text_search_configuration AS regconfig)::text"
            addParameter validate "text_search_configuration" textSearchConfiguration
            let! resolved = validate.ExecuteScalarAsync cancellationToken

            if isNull resolved || Convert.IsDBNull resolved then
                invalidArg
                    (nameof config)
                    $"PostgreSQL text-search configuration '{textSearchConfiguration}' does not exist."

            use setup = connection.CreateCommand()
            setup.CommandText <- $"CREATE SCHEMA IF NOT EXISTS {quoted schema};"
            do! executeNonQueryAsync setup cancellationToken

            use! transaction = connection.BeginTransactionAsync cancellationToken
            use command = connection.CreateCommand()
            command.Transaction <- transaction

            command.CommandText <-
                $"""
                SELECT pg_advisory_xact_lock(hashtext('nao-knowledge-lexical-v1'));

                CREATE TABLE IF NOT EXISTS {markerTable} (
                    configuration_key text PRIMARY KEY,
                    table_name text NOT NULL UNIQUE,
                    text_search_configuration text NOT NULL
                );

                INSERT INTO {markerTable} (
                    configuration_key, table_name, text_search_configuration
                ) VALUES (
                    @configuration_key, @table_name, @text_search_configuration
                )
                ON CONFLICT (configuration_key) DO NOTHING;

                CREATE TABLE IF NOT EXISTS {table} (
                    owner_key text NOT NULL,
                    namespace text NOT NULL,
                    tenant_id text NOT NULL,
                    workspace_id text NOT NULL,
                    owner_user_id text NOT NULL,
                    owner_group_id text NULL,
                    owner_session_id text NULL,
                    acl_users text[] NOT NULL,
                    acl_groups text[] NOT NULL,
                    acl_roles text[] NOT NULL,
                    source_id text NOT NULL,
                    document_version_id text NOT NULL,
                    chunk_id text NOT NULL,
                    content_hash text NOT NULL,
                    content text NOT NULL,
                    metadata jsonb NOT NULL,
                    source_modified_at timestamptz NULL,
                    ingested_at timestamptz NOT NULL,
                    indexed_at timestamptz NOT NULL,
                    search_vector tsvector NOT NULL,
                    PRIMARY KEY (owner_key, namespace, chunk_id)
                );

                {initializeSourceStateSql table sourceStateTable}

                CREATE INDEX IF NOT EXISTS {quoted (tableName + "_search")}
                    ON {table} USING gin (search_vector);
                CREATE INDEX IF NOT EXISTS {quoted (tableName + "_boundary")}
                    ON {table} (tenant_id, workspace_id, namespace);
                CREATE INDEX IF NOT EXISTS {quoted (tableName + "_source")}
                    ON {table} (source_id, document_version_id);
                CREATE INDEX IF NOT EXISTS {quoted (tableName + "_metadata")}
                    ON {table} USING gin (metadata);
                """

            addParameter command "configuration_key" configurationKey
            addParameter command "table_name" tableName
            addParameter command "text_search_configuration" textSearchConfiguration
            do! executeNonQueryAsync command cancellationToken

            use verify = connection.CreateCommand()
            verify.Transaction <- transaction

            verify.CommandText <-
                $"""
                SELECT table_name, text_search_configuration
                FROM {markerTable}
                WHERE configuration_key = @configuration_key
                """

            addParameter verify "configuration_key" configurationKey
            use! reader = verify.ExecuteReaderAsync cancellationToken
            let! found = reader.ReadAsync cancellationToken

            if
                not found
                || reader.GetString(0) <> tableName
                || reader.GetString(1) <> textSearchConfiguration
            then
                invalidOp "PostgreSQL lexical index marker does not match the requested configuration."

            do! reader.CloseAsync()
            do! transaction.CommitAsync cancellationToken
        }

    let private applyAsync
        (dataSource: NpgsqlDataSource)
        (config: PostgresLexicalIndexConfig)
        (authorization: AuthorizationScope)
        (mutation: KnowledgeIndexMutation<KnowledgeIndexDocument>)
        (cancellationToken: CancellationToken)
        =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            let namespaceName = KnowledgeIndexMutation.namespaceName mutation
            let documents = KnowledgeIndexMutation.upserts mutation
            let retireSourceIds = KnowledgeIndexMutation.retireSourceIds mutation
            let identity = AuthorizationScope.identity authorization

            documents
            |> List.iter (fun document ->
                if KnowledgeIndexDocument.owner document <> identity then
                    raise (UnauthorizedAccessException "Only the owning authorization identity may upsert index rows.")

                if KnowledgeIndexDocument.namespaceName document <> namespaceName then
                    invalidArg (nameof mutation) "Lexical mutation records must belong to the mutation namespace.")

            let upsertIds = documents |> List.map KnowledgeIndexDocument.chunkId

            if (upsertIds |> Set.ofList |> Set.count) <> upsertIds.Length then
                invalidArg (nameof mutation) "Lexical mutation contains duplicate upsert chunk identities."

            let upsertSourceIds =
                documents |> Seq.map KnowledgeIndexDocument.sourceId |> Set.ofSeq

            if not (Set.intersect upsertSourceIds retireSourceIds).IsEmpty then
                invalidArg (nameof mutation) "A lexical mutation cannot upsert and retire the same source."

            use! connection = dataSource.OpenConnectionAsync cancellationToken
            use! transaction = connection.BeginTransactionAsync cancellationToken
            let tableName = PostgresLexicalIndexConfig.tableName config
            let table = qualified config tableName
            let sourceStateTable = qualified config (sourceStateTableName tableName)
            let mutable deletedRecords = 0

            do!
                lockSourceStatesAsync
                    connection
                    transaction
                    sourceStateTable
                    identity
                    namespaceName
                    upsertSourceIds
                    retireSourceIds
                    cancellationToken

            let executeDeleteAsync (chunkIds: Set<KnowledgeChunkId>) (filter: KnowledgeIndexFilter option) =
                task {
                    use command = connection.CreateCommand()
                    command.Transaction <- transaction

                    let sql =
                        StringBuilder($"DELETE FROM {table} WHERE owner_key = @owner_key AND namespace = @namespace")

                    addParameter command "owner_key" (AuthorizationIdentity.storageKey identity)
                    addParameter command "namespace" (KnowledgeIndexNamespace.value namespaceName)

                    if not chunkIds.IsEmpty then
                        sql.Append(" AND chunk_id = ANY(@delete_chunk_ids)") |> ignore

                        addParameter
                            command
                            "delete_chunk_ids"
                            (chunkIds |> Seq.map KnowledgeChunkId.value |> Seq.toArray)

                    filter |> Option.iter (appendFilter sql command)
                    command.CommandText <- sql.ToString()
                    return! command.ExecuteNonQueryAsync cancellationToken
                }

            let deleteChunkIds = KnowledgeIndexMutation.deleteChunkIds mutation

            if not deleteChunkIds.IsEmpty then
                let! deleted = executeDeleteAsync deleteChunkIds None
                deletedRecords <- deletedRecords + deleted

            match KnowledgeIndexMutation.deleteFilter mutation with
            | Some filter ->
                let! deleted = executeDeleteAsync Set.empty (Some filter)
                deletedRecords <- deletedRecords + deleted
            | None -> ()

            if not retireSourceIds.IsEmpty then
                let retirementFilter =
                    KnowledgeIndexFilter.create retireSourceIds [] Map.empty None None None None

                let! deleted = executeDeleteAsync Set.empty (Some retirementFilter)
                deletedRecords <- deletedRecords + deleted

            for document in documents do
                let owner = KnowledgeIndexDocument.owner document
                let access = KnowledgeIndexDocument.access document
                use command = connection.CreateCommand()
                command.Transaction <- transaction

                command.CommandText <-
                    $"""
                    INSERT INTO {table} (
                        owner_key, namespace, tenant_id, workspace_id, owner_user_id, owner_group_id,
                        owner_session_id, acl_users, acl_groups, acl_roles, source_id, document_version_id,
                        chunk_id, content_hash, content, metadata, source_modified_at, ingested_at, indexed_at,
                        search_vector
                    ) VALUES (
                        @owner_key, @namespace, @tenant_id, @workspace_id, @owner_user_id, @owner_group_id,
                        @owner_session_id, @acl_users, @acl_groups, @acl_roles, @source_id, @document_version_id,
                        @chunk_id, @content_hash, @content, @metadata, @source_modified_at, @ingested_at, @indexed_at,
                        to_tsvector(CAST(@text_search_configuration AS regconfig), @content)
                    )
                    ON CONFLICT (owner_key, namespace, chunk_id) DO UPDATE SET
                        acl_users = EXCLUDED.acl_users,
                        acl_groups = EXCLUDED.acl_groups,
                        acl_roles = EXCLUDED.acl_roles,
                        source_id = EXCLUDED.source_id,
                        document_version_id = EXCLUDED.document_version_id,
                        content_hash = EXCLUDED.content_hash,
                        content = EXCLUDED.content,
                        metadata = EXCLUDED.metadata,
                        source_modified_at = EXCLUDED.source_modified_at,
                        ingested_at = EXCLUDED.ingested_at,
                        indexed_at = EXCLUDED.indexed_at,
                        search_vector = EXCLUDED.search_vector
                    """

                addParameter command "owner_key" (AuthorizationIdentity.storageKey owner)

                addParameter
                    command
                    "namespace"
                    (KnowledgeIndexDocument.namespaceName document |> KnowledgeIndexNamespace.value)

                addParameter command "tenant_id" (TenantId.value owner.TenantId)
                addParameter command "workspace_id" (WorkspaceId.value owner.WorkspaceId)
                addParameter command "owner_user_id" (UserId.value owner.UserId)
                addNullableParameter command "owner_group_id" (owner.GroupId |> Option.map GroupId.value)
                addNullableParameter command "owner_session_id" (owner.SessionId |> Option.map SessionId.value)

                addParameter
                    command
                    "acl_users"
                    (KnowledgeIndexAccess.users access |> Seq.map UserId.value |> Seq.toArray)

                addParameter
                    command
                    "acl_groups"
                    (KnowledgeIndexAccess.groups access |> Seq.map GroupId.value |> Seq.toArray)

                addParameter command "acl_roles" (KnowledgeIndexAccess.roles access |> Seq.toArray)
                addParameter command "source_id" (KnowledgeIndexDocument.sourceId document |> KnowledgeSourceId.value)

                addParameter
                    command
                    "document_version_id"
                    (KnowledgeIndexDocument.documentVersionId document
                     |> KnowledgeDocumentVersionId.value)

                addParameter command "chunk_id" (KnowledgeIndexDocument.chunkId document |> KnowledgeChunkId.value)
                addParameter command "content_hash" (KnowledgeIndexDocument.contentHash document |> ContentHash.value)
                addParameter command "content" (KnowledgeIndexDocument.text document)
                let metadataParameter = command.Parameters.Add("metadata", NpgsqlDbType.Jsonb)
                metadataParameter.Value <- metadataJson (KnowledgeIndexDocument.metadata document)

                addNullableParameter command "source_modified_at" (KnowledgeIndexDocument.sourceModifiedAt document)

                addParameter command "ingested_at" (KnowledgeIndexDocument.ingestedAt document)
                addParameter command "indexed_at" (KnowledgeIndexDocument.indexedAt document)

                addParameter
                    command
                    "text_search_configuration"
                    (PostgresLexicalIndexConfig.textSearchConfiguration config)

                do! executeNonQueryAsync command cancellationToken

            do! transaction.CommitAsync cancellationToken

            return
                KnowledgeIndexMutationResult.create
                    namespaceName
                    documents.Length
                    deletedRecords
                    retireSourceIds.Count
                    DateTimeOffset.UtcNow
        }

    let private queryAsync
        (dataSource: NpgsqlDataSource)
        (config: PostgresLexicalIndexConfig)
        (authorization: AuthorizationScope)
        (query: LexicalIndexQuery)
        (cancellationToken: CancellationToken)
        =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            use! connection = dataSource.OpenConnectionAsync cancellationToken
            use command = connection.CreateCommand()
            let tableName = PostgresLexicalIndexConfig.tableName config
            let sourceStateTable = qualified config (sourceStateTableName tableName)

            let sql =
                StringBuilder(
                    $"""
                    WITH lexical_query AS (
                        SELECT websearch_to_tsquery(
                            CAST(@text_search_configuration AS regconfig),
                            @query
                        ) AS value
                    )
                    SELECT {selectColumns},
                           ts_rank_cd(search_vector, lexical_query.value, 32)::double precision AS score
                    FROM {qualified config tableName} AS records
                    CROSS JOIN lexical_query
                    WHERE {activeSourcePredicate sourceStateTable "records"}
                      AND {authorizationPredicate}
                      AND search_vector @@ lexical_query.value
                    """
                )

            addIdentityParameters command authorization
            addParameter command "namespace" (LexicalIndexQuery.namespaceName query |> KnowledgeIndexNamespace.value)
            addParameter command "query" (LexicalIndexQuery.query query)

            addParameter command "text_search_configuration" (PostgresLexicalIndexConfig.textSearchConfiguration config)

            appendFilter sql command (LexicalIndexQuery.filter query)

            sql.Append(" ORDER BY score DESC, indexed_at DESC, chunk_id LIMIT @limit")
            |> ignore

            command.CommandText <- sql.ToString()
            addParameter command "limit" (LexicalIndexQuery.limit query)

            use! reader = command.ExecuteReaderAsync cancellationToken
            let hits = ResizeArray<LexicalIndexHit>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync cancellationToken

                if found then
                    hits.Add(LexicalIndexHit.create (readDocument reader) (reader.GetDouble 18))
                else
                    reading <- false

            return List.ofSeq hits
        }

    let measureStorageBytesAsync
        (dataSource: NpgsqlDataSource)
        (config: PostgresLexicalIndexConfig)
        (cancellationToken: CancellationToken)
        =
        if isNull dataSource then
            nullArg (nameof dataSource)

        let tableName = PostgresLexicalIndexConfig.tableName config

        measureRelationStorageBytesAsync
            dataSource
            (PostgresLexicalIndexConfig.schema config)
            [ tableName; sourceStateTableName tableName ]
            cancellationToken

    let createAsync
        (dataSource: NpgsqlDataSource)
        (config: PostgresLexicalIndexConfig)
        (cancellationToken: CancellationToken)
        : Task<KnowledgeLexicalIndex> =
        task {
            if isNull dataSource then
                nullArg (nameof dataSource)

            do! initializeAsync dataSource config cancellationToken

            return
                ({ ApplyAsync = applyAsync dataSource config
                   QueryAsync = queryAsync dataSource config }
                : KnowledgeLexicalIndex)
        }
