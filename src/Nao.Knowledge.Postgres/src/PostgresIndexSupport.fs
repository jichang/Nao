namespace Nao.Knowledge.Postgres

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Threading
open Nao.Agents
open Nao.Knowledge
open Npgsql
open NpgsqlTypes

module internal PostgresIndexSupport =
    let quoted (value: string) = "\"" + value + "\""
    let qualified schema table = quoted schema + "." + quoted table

    let addParameter (command: NpgsqlCommand) (name: string) (value: obj) =
        command.Parameters.AddWithValue(name, value) |> ignore

    let addNullableParameter (command: NpgsqlCommand) (name: string) (value: 'value option) =
        command.Parameters.AddWithValue(name, value |> Option.map box |> Option.defaultValue (box DBNull.Value))
        |> ignore

    let executeNonQueryAsync (command: NpgsqlCommand) (cancellationToken: CancellationToken) =
        task {
            let! _ = command.ExecuteNonQueryAsync cancellationToken
            return ()
        }

    let sourceStateTableName dataTableName = dataTableName + "_sources"

    let initializeSourceStateSql dataTable sourceStateTable =
        $"""
        CREATE TABLE IF NOT EXISTS {sourceStateTable} (
            owner_key text NOT NULL,
            namespace text NOT NULL,
            source_id text NOT NULL,
            retired_at timestamptz NULL,
            PRIMARY KEY (owner_key, namespace, source_id)
        );

        INSERT INTO {sourceStateTable} (owner_key, namespace, source_id, retired_at)
        SELECT DISTINCT owner_key, namespace, source_id, NULL::timestamptz
        FROM {dataTable}
        ON CONFLICT (owner_key, namespace, source_id) DO NOTHING;
        """

    let lockSourceStatesAsync
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        sourceStateTable
        (identity: AuthorizationIdentity)
        namespaceName
        (upsertSourceIds: Set<KnowledgeSourceId>)
        (retireSourceIds: Set<KnowledgeSourceId>)
        (cancellationToken: CancellationToken)
        =
        task {
            let sourceIds = Set.union upsertSourceIds retireSourceIds

            if not sourceIds.IsEmpty then
                use command = connection.CreateCommand()
                command.Transaction <- transaction

                command.CommandText <-
                    $"""
                    INSERT INTO {sourceStateTable} (owner_key, namespace, source_id, retired_at)
                    SELECT @owner_key, @namespace, source_id, NULL
                    FROM unnest(@source_ids) AS source_id
                    ON CONFLICT (owner_key, namespace, source_id) DO NOTHING;

                    SELECT source_id
                    FROM {sourceStateTable}
                    WHERE owner_key = @owner_key
                      AND namespace = @namespace
                      AND source_id = ANY(@source_ids)
                    ORDER BY source_id
                    FOR UPDATE;
                    """

                addParameter command "owner_key" (AuthorizationIdentity.storageKey identity)
                addParameter command "namespace" (KnowledgeIndexNamespace.value namespaceName)
                addParameter command "source_ids" (sourceIds |> Seq.map KnowledgeSourceId.value |> Seq.toArray)
                use! reader = command.ExecuteReaderAsync cancellationToken
                let mutable reading = true

                while reading do
                    let! found = reader.ReadAsync cancellationToken
                    reading <- found

                do! reader.CloseAsync()

            if not upsertSourceIds.IsEmpty then
                use command = connection.CreateCommand()
                command.Transaction <- transaction

                command.CommandText <-
                    $"""
                    SELECT EXISTS (
                        SELECT 1
                        FROM {sourceStateTable}
                        WHERE owner_key = @owner_key
                          AND namespace = @namespace
                          AND source_id = ANY(@upsert_source_ids)
                          AND retired_at IS NOT NULL
                    )
                    """

                addParameter command "owner_key" (AuthorizationIdentity.storageKey identity)
                addParameter command "namespace" (KnowledgeIndexNamespace.value namespaceName)

                addParameter
                    command
                    "upsert_source_ids"
                    (upsertSourceIds |> Seq.map KnowledgeSourceId.value |> Seq.toArray)

                let! hasRetiredSource = command.ExecuteScalarAsync cancellationToken

                if Convert.ToBoolean hasRetiredSource then
                    invalidOp "Retired knowledge sources cannot be reintroduced into a searchable index."

            if not retireSourceIds.IsEmpty then
                use command = connection.CreateCommand()
                command.Transaction <- transaction

                command.CommandText <-
                    $"""
                    UPDATE {sourceStateTable}
                    SET retired_at = COALESCE(retired_at, statement_timestamp())
                    WHERE owner_key = @owner_key
                      AND namespace = @namespace
                      AND source_id = ANY(@retire_source_ids)
                    """

                addParameter command "owner_key" (AuthorizationIdentity.storageKey identity)
                addParameter command "namespace" (KnowledgeIndexNamespace.value namespaceName)

                addParameter
                    command
                    "retire_source_ids"
                    (retireSourceIds |> Seq.map KnowledgeSourceId.value |> Seq.toArray)

                do! executeNonQueryAsync command cancellationToken
        }

    let activeSourcePredicate sourceStateTable recordAlias =
        $"""
        EXISTS (
            SELECT 1
            FROM {sourceStateTable} AS source_state
            WHERE source_state.owner_key = {recordAlias}.owner_key
              AND source_state.namespace = {recordAlias}.namespace
              AND source_state.source_id = {recordAlias}.source_id
              AND source_state.retired_at IS NULL
        )
        """

    let measureRelationStorageBytesAsync
        (dataSource: NpgsqlDataSource)
        schema
        relationNames
        (cancellationToken: CancellationToken)
        =
        task {
            use! connection = dataSource.OpenConnectionAsync cancellationToken
            let mutable total = 0L

            for relationName in relationNames do
                use command = connection.CreateCommand()
                command.CommandText <- "SELECT pg_total_relation_size(to_regclass(@relation_name))"
                addParameter command "relation_name" (schema + "." + relationName)
                let! measured = command.ExecuteScalarAsync cancellationToken

                if isNull measured || Convert.IsDBNull measured then
                    invalidOp $"PostgreSQL relation '{schema}.{relationName}' does not exist."

                total <- total + Convert.ToInt64 measured

            return total
        }

    let metadataJson (metadata: Map<string, string>) =
        JsonSerializer.Serialize(Dictionary<string, string>(metadata |> Map.toSeq |> dict))

    let addIdentityParameters (command: NpgsqlCommand) (authorization: AuthorizationScope) =
        let identity = AuthorizationScope.identity authorization
        let principal = AuthorizationScope.principal authorization
        addParameter command "tenant_id" (TenantId.value identity.TenantId)
        addParameter command "workspace_id" (WorkspaceId.value identity.WorkspaceId)
        addParameter command "user_id" (UserId.value identity.UserId)
        addNullableParameter command "identity_group_id" (identity.GroupId |> Option.map GroupId.value)
        addNullableParameter command "identity_session_id" (identity.SessionId |> Option.map SessionId.value)

        addParameter
            command
            "principal_groups"
            (SecurityPrincipal.groupIds principal |> Seq.map GroupId.value |> Seq.toArray)

        addParameter command "principal_roles" (SecurityPrincipal.roles principal |> Seq.toArray)

    let appendFilter (sql: StringBuilder) (command: NpgsqlCommand) (filter: KnowledgeIndexFilter) =
        let sourceIds = KnowledgeIndexFilter.sourceIds filter

        if not sourceIds.IsEmpty then
            sql.Append(" AND source_id = ANY(@source_ids)") |> ignore
            addParameter command "source_ids" (sourceIds |> Seq.map KnowledgeSourceId.value |> Seq.toArray)

        let versionIds = KnowledgeIndexFilter.documentVersionIds filter

        if not versionIds.IsEmpty then
            sql.Append(" AND document_version_id = ANY(@version_ids)") |> ignore

            addParameter command "version_ids" (versionIds |> Seq.map KnowledgeDocumentVersionId.value |> Seq.toArray)

        let metadata = KnowledgeIndexFilter.metadataEquals filter

        if not metadata.IsEmpty then
            sql.Append(" AND metadata @> @metadata") |> ignore
            let parameter = command.Parameters.Add("metadata", NpgsqlDbType.Jsonb)
            parameter.Value <- metadataJson metadata

        let addLower name column value =
            value
            |> Option.iter (fun timestamp ->
                sql.Append($" AND {column} >= @{name}") |> ignore
                addParameter command name timestamp)

        let addUpper name column value =
            value
            |> Option.iter (fun timestamp ->
                sql.Append($" AND {column} <= @{name}") |> ignore
                addParameter command name timestamp)

        addLower "source_modified_from" "source_modified_at" (KnowledgeIndexFilter.sourceModifiedFrom filter)
        addUpper "source_modified_until" "source_modified_at" (KnowledgeIndexFilter.sourceModifiedUntil filter)
        addLower "ingested_from" "ingested_at" (KnowledgeIndexFilter.ingestedFrom filter)
        addUpper "ingested_until" "ingested_at" (KnowledgeIndexFilter.ingestedUntil filter)

    let private getOptionalString (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull ordinal then
            None
        else
            Some(reader.GetString ordinal)

    let private getOptionalTimestamp (reader: NpgsqlDataReader) ordinal =
        if reader.IsDBNull ordinal then
            None
        else
            Some(reader.GetFieldValue<DateTimeOffset> ordinal)

    let readDocument (reader: NpgsqlDataReader) =
        let owner =
            { TenantId = reader.GetString(0) |> TenantId.parse
              GroupId = getOptionalString reader 3 |> Option.map GroupId.parse
              UserId = reader.GetString(2) |> UserId.parse
              WorkspaceId = reader.GetString(1) |> WorkspaceId.parse
              SessionId = getOptionalString reader 4 |> Option.map SessionId.parse }

        let access =
            KnowledgeIndexAccess.create
                (reader.GetFieldValue<string array>(5) |> Seq.map UserId.parse)
                (reader.GetFieldValue<string array>(6) |> Seq.map GroupId.parse)
                (reader.GetFieldValue<string array>(7))

        let metadata =
            JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString 13)
            |> Seq.map (fun pair -> pair.Key, pair.Value)
            |> Map.ofSeq

        KnowledgeIndexDocument.restore
            (reader.GetString(8) |> KnowledgeIndexNamespace.parse)
            owner
            access
            (reader.GetString(9) |> KnowledgeSourceId.parse)
            (reader.GetString(10) |> KnowledgeDocumentVersionId.parse)
            (reader.GetString(11) |> KnowledgeChunkId.parse)
            (reader.GetString(12) |> ContentHash.parse)
            (reader.GetString(14))
            metadata
            (getOptionalTimestamp reader 15)
            (reader.GetFieldValue<DateTimeOffset> 16)
            (reader.GetFieldValue<DateTimeOffset> 17)

    let selectColumns =
        """
        tenant_id, workspace_id, owner_user_id, owner_group_id, owner_session_id,
        acl_users, acl_groups, acl_roles, namespace, source_id, document_version_id,
        chunk_id, content_hash, metadata::text, content, source_modified_at, ingested_at, indexed_at
        """

    let authorizationPredicate =
        """
        tenant_id = @tenant_id
        AND workspace_id = @workspace_id
        AND namespace = @namespace
        AND (owner_session_id IS NULL OR owner_session_id = @identity_session_id)
        AND (
              (
                  owner_user_id = @user_id
                  AND owner_group_id IS NOT DISTINCT FROM @identity_group_id
                  AND owner_session_id IS NOT DISTINCT FROM @identity_session_id
              )
              OR @user_id = ANY(acl_users)
              OR acl_groups && @principal_groups
              OR acl_roles && @principal_roles
        )
        """
