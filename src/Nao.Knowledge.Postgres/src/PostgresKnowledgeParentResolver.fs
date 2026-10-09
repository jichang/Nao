namespace Nao.Knowledge.Postgres

open System
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Nao.Knowledge
open Npgsql
open PostgresIndexSupport

[<RequireQualifiedAccess>]
module PostgresKnowledgeParentResolver =
    let private resolveAsync
        (dataSource: NpgsqlDataSource)
        (config: PostgresLexicalIndexConfig)
        (authorization: Nao.Agents.AuthorizationScope)
        (request: KnowledgeParentResolutionRequest)
        (cancellationToken: CancellationToken)
        =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            use! connection = dataSource.OpenConnectionAsync cancellationToken
            use command = connection.CreateCommand()
            let tableName = PostgresLexicalIndexConfig.tableName config
            let table = qualified (PostgresLexicalIndexConfig.schema config) tableName

            let sourceStateTable =
                qualified (PostgresLexicalIndexConfig.schema config) (sourceStateTableName tableName)

            let sql =
                StringBuilder(
                    $"""
                    SELECT {selectColumns}
                    FROM {table} AS records
                    WHERE {activeSourcePredicate sourceStateTable "records"}
                      AND {authorizationPredicate}
                      AND chunk_role = 'parent'
                      AND chunk_id = ANY(@parent_chunk_ids)
                    """
                )

            addIdentityParameters command authorization

            addParameter
                command
                "namespace"
                (KnowledgeParentResolutionRequest.namespaceName request
                 |> KnowledgeIndexNamespace.value)

            addParameter
                command
                "parent_chunk_ids"
                (KnowledgeParentResolutionRequest.parentChunkIds request
                 |> Seq.map KnowledgeChunkId.value
                 |> Seq.toArray)

            appendFilter sql command (KnowledgeParentResolutionRequest.filter request)
            sql.Append(" ORDER BY chunk_id LIMIT @limit") |> ignore
            command.CommandText <- sql.ToString()
            addParameter command "limit" (KnowledgeParentResolutionRequest.maximumParents request)
            use! reader = command.ExecuteReaderAsync cancellationToken
            let parents = ResizeArray<KnowledgeIndexDocument>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync cancellationToken

                if found then
                    parents.Add(readDocument reader)
                else
                    reading <- false

            return List.ofSeq parents
        }

    let create (dataSource: NpgsqlDataSource) (config: PostgresLexicalIndexConfig) =
        if isNull dataSource then
            nullArg (nameof dataSource)

        { ResolveAsync = resolveAsync dataSource config }
