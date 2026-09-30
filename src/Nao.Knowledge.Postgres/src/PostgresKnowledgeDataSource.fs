namespace Nao.Knowledge.Postgres

open System
open Npgsql
open Pgvector

[<RequireQualifiedAccess>]
module PostgresKnowledgeDataSource =
    let create connectionString =
        if String.IsNullOrWhiteSpace connectionString then
            invalidArg (nameof connectionString) "PostgreSQL connection string must be non-blank."

        let builder = NpgsqlDataSourceBuilder connectionString
        builder.UseVector() |> ignore
        builder.Build()
