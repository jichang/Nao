namespace Nao.Persistence

open System
open System.Data.Common
open System.Threading.Tasks

/// Provider-agnostic factory that creates ADO.NET connections.
///
/// The persistence layer never references a concrete database provider — callers
/// supply a factory backed by any provider (Microsoft.Data.Sqlite, Npgsql,
/// Microsoft.Data.SqlClient, MySqlConnector, ...). This keeps a single unified
/// implementation that works against any ADO.NET-compatible database.
type IDbConnectionFactory =
    /// Create a brand new (closed) connection.
    abstract member Create: unit -> DbConnection

/// Helpers for building connection factories.
module DbConnectionFactory =

    /// Build a factory from a plain function (e.g. fun () -> new SqliteConnection(cs)).
    let ofFunc (create: unit -> DbConnection) : IDbConnectionFactory =
        { new IDbConnectionFactory with
            member _.Create() = create () }

    /// Build a factory from a DbProviderFactory + connection string.
    let ofProvider (provider: DbProviderFactory) (connectionString: string) : IDbConnectionFactory =
        { new IDbConnectionFactory with
            member _.Create() =
                let conn = provider.CreateConnection()
                conn.ConnectionString <- connectionString
                conn }

/// Low-level, provider-agnostic ADO.NET helpers built on System.Data.Common.
///
/// All SQL uses '@name' parameters (supported by SQLite, SQL Server, PostgreSQL
/// and MySQL providers) and portable DDL (CREATE TABLE IF NOT EXISTS).
module Ado =

    /// Add a parameter to a command, mapping null to DBNull.
    let addParam (cmd: DbCommand) (name: string) (value: obj) =
        let p = cmd.CreateParameter()
        p.ParameterName <- name
        p.Value <- (if isNull value then box DBNull.Value else value)
        cmd.Parameters.Add(p) |> ignore

    /// Execute a non-query statement, returning affected row count.
    let executeNonQuery (factory: IDbConnectionFactory) (sql: string) (parameters: (string * obj) list) : Task<int> =
        task {
            use conn = factory.Create()
            do! conn.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql
            for (n, v) in parameters do
                addParam cmd n v
            return! cmd.ExecuteNonQueryAsync()
        }

    /// Execute several statements inside a single transaction.
    let executeTransaction (factory: IDbConnectionFactory) (statements: (string * (string * obj) list) list) : Task<unit> =
        task {
            use conn = factory.Create()
            do! conn.OpenAsync()
            use tx = conn.BeginTransaction()
            for (sql, parameters) in statements do
                use cmd = conn.CreateCommand()
                cmd.Transaction <- tx
                cmd.CommandText <- sql
                for (n, v) in parameters do
                    addParam cmd n v
                let! _ = cmd.ExecuteNonQueryAsync()
                ()
            do! tx.CommitAsync()
        }

    /// Run a query and project each row with the supplied mapper.
    let query (factory: IDbConnectionFactory) (sql: string) (parameters: (string * obj) list) (map: DbDataReader -> 'a) : Task<'a list> =
        task {
            use conn = factory.Create()
            do! conn.OpenAsync()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql
            for (n, v) in parameters do
                addParam cmd n v
            use! reader = cmd.ExecuteReaderAsync()
            let results = ResizeArray<'a>()
            let mutable go = true
            while go do
                let! has = reader.ReadAsync()
                if has then results.Add(map reader) else go <- false
            return List.ofSeq results
        }

    /// Read a non-null string column by name.
    let getString (r: DbDataReader) (col: string) : string =
        r.GetString(r.GetOrdinal col)

    /// Read a nullable string column by name.
    let getStringOpt (r: DbDataReader) (col: string) : string option =
        let o = r.GetOrdinal col
        if r.IsDBNull o then None else Some(r.GetString o)

    /// Read a boolean column (stored as integer 0/1 for portability).
    let getBool (r: DbDataReader) (col: string) : bool =
        let o = r.GetOrdinal col
        if r.IsDBNull o then false
        else
            match r.GetValue o with
            | :? bool as b -> b
            | v -> Convert.ToInt64(v) <> 0L

    /// Encode a boolean as a portable integer parameter value.
    let boolValue (b: bool) : obj = box (if b then 1 else 0)
