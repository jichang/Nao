namespace Nao.Knowledge.Postgres

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Nao.Agents
open Nao.Knowledge
open Npgsql
open NpgsqlTypes
open Pgvector
open PostgresIndexSupport

[<RequireQualifiedAccess>]
module PgvectorKnowledgeIndex =
    let private qualified (config: PgvectorIndexConfig) (table: string) =
        PostgresIndexSupport.qualified (PgvectorIndexConfig.schema config) table

    let private normalizationName =
        function
        | EmbeddingNormalization.None -> "none"
        | EmbeddingNormalization.UnitLength -> "unit-length"

    let private metricName =
        function
        | VectorDistanceMetric.Cosine -> "cosine"
        | VectorDistanceMetric.DotProduct -> "dot-product"
        | VectorDistanceMetric.Euclidean -> "euclidean"

    let private metricSql =
        function
        | VectorDistanceMetric.Cosine -> "<=>", "1 - (embedding <=> @query)", "vector_cosine_ops"
        | VectorDistanceMetric.DotProduct -> "<#>", "-(embedding <#> @query)", "vector_ip_ops"
        | VectorDistanceMetric.Euclidean -> "<->", "-(embedding <-> @query)", "vector_l2_ops"

    let private profileValues profile =
        let profile = EmbeddingCapabilityDescriptor.validate profile

        profile,
        EmbeddingProfileId.value profile.ProfileId,
        profile.Model.ComponentId,
        profile.Model.Version,
        profile.Dimensions,
        normalizationName profile.Normalization,
        metricName profile.DistanceMetric,
        profile.Preprocessing.ComponentId,
        profile.Preprocessing.Version

    let private initializeAsync (dataSource: NpgsqlDataSource) profile config (cancellationToken: CancellationToken) =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            let (profile,
                 profileId,
                 modelId,
                 modelVersion,
                 dimensions,
                 normalization,
                 distanceMetric,
                 preprocessingId,
                 preprocessingVersion) =
                profileValues profile

            let schema = quoted (PgvectorIndexConfig.schema config)
            let tableName = PgvectorIndexConfig.tableName profile
            let table = qualified config tableName
            let sourceStateTable = qualified config (sourceStateTableName tableName)
            let markerTable = qualified config "vector_profiles"
            let _, _, operatorClass = metricSql profile.DistanceMetric

            use! connection = dataSource.OpenConnectionAsync cancellationToken

            use! setupTransaction = connection.BeginTransactionAsync cancellationToken
            use setup = connection.CreateCommand()
            setup.Transaction <- setupTransaction

            setup.CommandText <-
                $"""
                SELECT pg_advisory_xact_lock(hashtext('nao-knowledge-pgvector-extension-v1'));
                CREATE EXTENSION IF NOT EXISTS vector;
                CREATE SCHEMA IF NOT EXISTS {schema};
                """

            do! executeNonQueryAsync setup cancellationToken
            do! setupTransaction.CommitAsync cancellationToken
            let! _ = connection.ReloadTypesAsync cancellationToken

            use! transaction = connection.BeginTransactionAsync cancellationToken
            use command = connection.CreateCommand()
            command.Transaction <- transaction

            command.CommandText <-
                $"""
                SELECT pg_advisory_xact_lock(hashtext('nao-knowledge-pgvector-v1'));

                CREATE TABLE IF NOT EXISTS {markerTable} (
                    profile_id text PRIMARY KEY,
                    table_name text NOT NULL UNIQUE,
                    contract_version integer NOT NULL,
                    model_id text NOT NULL,
                    model_version text NOT NULL,
                    dimensions integer NOT NULL,
                    normalization text NOT NULL,
                    distance_metric text NOT NULL,
                    preprocessing_id text NOT NULL,
                    preprocessing_version text NOT NULL
                );

                INSERT INTO {markerTable} (
                    profile_id, table_name, contract_version, model_id, model_version, dimensions,
                    normalization, distance_metric, preprocessing_id, preprocessing_version
                ) VALUES (
                    @profile_id, @table_name, @contract_version, @model_id, @model_version, @dimensions,
                    @normalization, @distance_metric, @preprocessing_id, @preprocessing_version
                )
                ON CONFLICT (profile_id) DO NOTHING;

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
                    chunk_role text NOT NULL CHECK (chunk_role IN ('parent', 'leaf')),
                    parent_chunk_id text NULL,
                    content_hash text NOT NULL,
                    content text NOT NULL,
                    metadata jsonb NOT NULL,
                    source_modified_at timestamptz NULL,
                    ingested_at timestamptz NOT NULL,
                    indexed_at timestamptz NOT NULL,
                    embedding vector({dimensions}) NOT NULL,
                    CHECK (chunk_role <> 'parent' OR parent_chunk_id IS NULL),
                    PRIMARY KEY (owner_key, namespace, chunk_id)
                );

                {initializeSourceStateSql table sourceStateTable}

                CREATE INDEX IF NOT EXISTS {quoted (tableName + "_hnsw")}
                    ON {table} USING hnsw (embedding {operatorClass})
                    WITH (m = {PgvectorIndexConfig.hnswM config}, ef_construction = {PgvectorIndexConfig.hnswEfConstruction config});
                CREATE INDEX IF NOT EXISTS {quoted (tableName + "_boundary")}
                    ON {table} (tenant_id, workspace_id, namespace);
                CREATE INDEX IF NOT EXISTS {quoted (tableName + "_source")}
                    ON {table} (source_id, document_version_id);
                CREATE INDEX IF NOT EXISTS {quoted (tableName + "_parent")}
                    ON {table} (parent_chunk_id) WHERE parent_chunk_id IS NOT NULL;
                CREATE INDEX IF NOT EXISTS {quoted (tableName + "_metadata")}
                    ON {table} USING gin (metadata);
                """

            addParameter command "profile_id" profileId
            addParameter command "table_name" tableName
            addParameter command "contract_version" (EmbeddingContractVersion.value profile.ContractVersion)
            addParameter command "model_id" modelId
            addParameter command "model_version" modelVersion
            addParameter command "dimensions" dimensions
            addParameter command "normalization" normalization
            addParameter command "distance_metric" distanceMetric
            addParameter command "preprocessing_id" preprocessingId
            addParameter command "preprocessing_version" preprocessingVersion
            do! executeNonQueryAsync command cancellationToken

            use verify = connection.CreateCommand()
            verify.Transaction <- transaction

            verify.CommandText <-
                $"""
                SELECT table_name, contract_version, model_id, model_version, dimensions, normalization,
                       distance_metric, preprocessing_id, preprocessing_version
                FROM {markerTable}
                WHERE profile_id = @profile_id
                """

            addParameter verify "profile_id" profileId
            use! reader = verify.ExecuteReaderAsync cancellationToken
            let! found = reader.ReadAsync cancellationToken

            if
                not found
                || reader.GetString(0) <> tableName
                || reader.GetInt32(1) <> EmbeddingContractVersion.value profile.ContractVersion
                || reader.GetString(2) <> modelId
                || reader.GetString(3) <> modelVersion
                || reader.GetInt32(4) <> dimensions
                || reader.GetString(5) <> normalization
                || reader.GetString(6) <> distanceMetric
                || reader.GetString(7) <> preprocessingId
                || reader.GetString(8) <> preprocessingVersion
            then
                invalidOp "PostgreSQL vector profile marker does not match the requested embedding profile."

            do! reader.CloseAsync()
            do! transaction.CommitAsync cancellationToken
        }

    let private applyAsync
        (dataSource: NpgsqlDataSource)
        (profile: EmbeddingCapabilityDescriptor)
        (config: PgvectorIndexConfig)
        (authorization: AuthorizationScope)
        (mutation: KnowledgeIndexMutation<VectorIndexRecord>)
        (cancellationToken: CancellationToken)
        =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            let namespaceName = KnowledgeIndexMutation.namespaceName mutation
            let records = KnowledgeIndexMutation.upserts mutation
            let retireSourceIds = KnowledgeIndexMutation.retireSourceIds mutation
            let identity = AuthorizationScope.identity authorization

            records
            |> List.iter (fun record ->
                if VectorIndexRecord.profile record <> profile then
                    invalidArg (nameof mutation) "Vector record does not match the adapter embedding profile."

                let document = VectorIndexRecord.document record

                if KnowledgeIndexDocument.owner document <> identity then
                    raise (UnauthorizedAccessException "Only the owning authorization identity may upsert index rows.")

                if KnowledgeIndexDocument.namespaceName document <> namespaceName then
                    invalidArg (nameof mutation) "Vector mutation records must belong to the mutation namespace.")

            let upsertIds =
                records
                |> List.map (VectorIndexRecord.document >> KnowledgeIndexDocument.chunkId)

            if (upsertIds |> Set.ofList |> Set.count) <> upsertIds.Length then
                invalidArg (nameof mutation) "Vector mutation contains duplicate upsert chunk identities."

            let upsertSourceIds =
                records
                |> Seq.map (VectorIndexRecord.document >> KnowledgeIndexDocument.sourceId)
                |> Set.ofSeq

            if not (Set.intersect upsertSourceIds retireSourceIds).IsEmpty then
                invalidArg (nameof mutation) "A vector mutation cannot upsert and retire the same source."

            use! connection = dataSource.OpenConnectionAsync cancellationToken
            use! transaction = connection.BeginTransactionAsync cancellationToken
            let tableName = PgvectorIndexConfig.tableName profile
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

            for record in records do
                let document = VectorIndexRecord.document record
                let owner = KnowledgeIndexDocument.owner document
                let access = KnowledgeIndexDocument.access document
                use command = connection.CreateCommand()
                command.Transaction <- transaction

                command.CommandText <-
                    $"""
                    INSERT INTO {table} (
                        owner_key, namespace, tenant_id, workspace_id, owner_user_id, owner_group_id,
                        owner_session_id, acl_users, acl_groups, acl_roles, source_id, document_version_id,
                        chunk_id, chunk_role, parent_chunk_id, content_hash, content, metadata,
                        source_modified_at, ingested_at, indexed_at, embedding
                    ) VALUES (
                        @owner_key, @namespace, @tenant_id, @workspace_id, @owner_user_id, @owner_group_id,
                        @owner_session_id, @acl_users, @acl_groups, @acl_roles, @source_id, @document_version_id,
                        @chunk_id, @chunk_role, @parent_chunk_id, @content_hash, @content, @metadata,
                        @source_modified_at, @ingested_at, @indexed_at, @embedding
                    )
                    ON CONFLICT (owner_key, namespace, chunk_id) DO UPDATE SET
                        acl_users = EXCLUDED.acl_users,
                        acl_groups = EXCLUDED.acl_groups,
                        acl_roles = EXCLUDED.acl_roles,
                        source_id = EXCLUDED.source_id,
                        document_version_id = EXCLUDED.document_version_id,
                        chunk_role = EXCLUDED.chunk_role,
                        parent_chunk_id = EXCLUDED.parent_chunk_id,
                        content_hash = EXCLUDED.content_hash,
                        content = EXCLUDED.content,
                        metadata = EXCLUDED.metadata,
                        source_modified_at = EXCLUDED.source_modified_at,
                        ingested_at = EXCLUDED.ingested_at,
                        indexed_at = EXCLUDED.indexed_at,
                        embedding = EXCLUDED.embedding
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
                addParameter command "chunk_role" (KnowledgeIndexDocument.role document |> chunkRoleValue)

                addNullableParameter
                    command
                    "parent_chunk_id"
                    (KnowledgeIndexDocument.parentChunkId document
                     |> Option.map KnowledgeChunkId.value)

                addParameter command "content_hash" (KnowledgeIndexDocument.contentHash document |> ContentHash.value)
                addParameter command "content" (KnowledgeIndexDocument.text document)
                let metadataParameter = command.Parameters.Add("metadata", NpgsqlDbType.Jsonb)
                metadataParameter.Value <- metadataJson (KnowledgeIndexDocument.metadata document)

                addNullableParameter command "source_modified_at" (KnowledgeIndexDocument.sourceModifiedAt document)

                addParameter command "ingested_at" (KnowledgeIndexDocument.ingestedAt document)
                addParameter command "indexed_at" (KnowledgeIndexDocument.indexedAt document)

                addParameter command "embedding" (Vector(VectorIndexRecord.values record |> Array.map float32))

                do! executeNonQueryAsync command cancellationToken

            do! transaction.CommitAsync cancellationToken

            return
                KnowledgeIndexMutationResult.create
                    namespaceName
                    records.Length
                    deletedRecords
                    retireSourceIds.Count
                    DateTimeOffset.UtcNow
        }

    let private queryAsync
        (dataSource: NpgsqlDataSource)
        (profile: EmbeddingCapabilityDescriptor)
        (config: PgvectorIndexConfig)
        (authorization: AuthorizationScope)
        (query: VectorIndexQuery)
        (cancellationToken: CancellationToken)
        =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            if VectorIndexQuery.profile query <> profile then
                invalidArg (nameof query) "Vector query does not match the adapter embedding profile."

            use! connection = dataSource.OpenConnectionAsync cancellationToken
            use command = connection.CreateCommand()
            let distanceOperator, scoreExpression, _ = metricSql profile.DistanceMetric
            let tableName = PgvectorIndexConfig.tableName profile
            let sourceStateTable = qualified config (sourceStateTableName tableName)

            let sql =
                StringBuilder(
                    $"""
                    SELECT {selectColumns}, {scoreExpression} AS score
                    FROM {qualified config tableName} AS records
                    WHERE {activeSourcePredicate sourceStateTable "records"}
                      AND {authorizationPredicate}
                    """
                )

            addIdentityParameters command authorization
            addParameter command "namespace" (VectorIndexQuery.namespaceName query |> KnowledgeIndexNamespace.value)
            appendFilter sql command (VectorIndexQuery.filter query)

            sql.Append($" ORDER BY embedding {distanceOperator} @query LIMIT @limit")
            |> ignore

            command.CommandText <- sql.ToString()
            addParameter command "query" (Vector(VectorIndexQuery.values query |> Array.map float32))
            addParameter command "limit" (VectorIndexQuery.limit query)

            use! reader = command.ExecuteReaderAsync cancellationToken
            let hits = ResizeArray<VectorIndexHit>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync cancellationToken

                if found then
                    hits.Add(VectorIndexHit.create (readDocument reader) profile.ProfileId (reader.GetDouble 20))
                else
                    reading <- false

            return List.ofSeq hits
        }

    let measureStorageBytesAsync
        (dataSource: NpgsqlDataSource)
        (profile: EmbeddingCapabilityDescriptor)
        (config: PgvectorIndexConfig)
        (cancellationToken: CancellationToken)
        =
        if isNull dataSource then
            nullArg (nameof dataSource)

        let profile = EmbeddingCapabilityDescriptor.validate profile
        let tableName = PgvectorIndexConfig.tableName profile

        measureRelationStorageBytesAsync
            dataSource
            (PgvectorIndexConfig.schema config)
            [ tableName; sourceStateTableName tableName ]
            cancellationToken

    let createAsync
        (dataSource: NpgsqlDataSource)
        (profile: EmbeddingCapabilityDescriptor)
        (config: PgvectorIndexConfig)
        (cancellationToken: CancellationToken)
        : Task<KnowledgeVectorIndex> =
        task {
            if isNull dataSource then
                nullArg (nameof dataSource)

            let profile = EmbeddingCapabilityDescriptor.validate profile
            do! initializeAsync dataSource profile config cancellationToken

            return
                ({ ApplyAsync = applyAsync dataSource profile config
                   QueryAsync = queryAsync dataSource profile config }
                : KnowledgeVectorIndex)
        }
