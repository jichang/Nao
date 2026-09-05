namespace Nao.Persistence

open System
open System.Data.Common
open System.IO
open System.Threading.Tasks

/// Provider-agnostic factory that creates ADO.NET connections.
///
/// The persistence layer never references a concrete database provider — callers
/// supply a factory backed by any provider (Microsoft.Data.Sqlite, Npgsql,
/// Microsoft.Data.SqlClient, MySqlConnector, ...). This keeps a single unified
/// implementation that works against any ADO.NET-compatible database.
type DbConnectionFactory =
    /// Create a brand new (closed) connection.
    { Create: unit -> DbConnection }

/// Helpers for building connection factories.
module DbConnectionFactory =

    /// Build a factory from a plain function (e.g. fun () -> new SqliteConnection(cs)).
    let ofFunc (create: unit -> DbConnection) : DbConnectionFactory = { Create = create }

    /// Build a factory from a DbProviderFactory + connection string.
    let ofProvider (provider: DbProviderFactory) (connectionString: string) : DbConnectionFactory =
        { Create =
            fun () ->
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
    let executeNonQuery (factory: DbConnectionFactory) (sql: string) (parameters: (string * obj) list) : Task<int> =
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
    let executeTransaction
        (factory: DbConnectionFactory)
        (statements: (string * (string * obj) list) list)
        : Task<unit> =
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
    let query
        (factory: DbConnectionFactory)
        (sql: string)
        (parameters: (string * obj) list)
        (map: DbDataReader -> 'a)
        : Task<'a list> =
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
    let getString (r: DbDataReader) (col: string) : string = r.GetString(r.GetOrdinal col)

    /// Read a nullable string column by name.
    let getStringOpt (r: DbDataReader) (col: string) : string option =
        let o = r.GetOrdinal col
        if r.IsDBNull o then None else Some(r.GetString o)

    /// Read a boolean column (stored as integer 0/1 for portability).
    let getBool (r: DbDataReader) (col: string) : bool =
        let o = r.GetOrdinal col

        if r.IsDBNull o then
            false
        else
            match r.GetValue o with
            | :? bool as b -> b
            | v -> Convert.ToInt64(v) <> 0L

    /// Encode a boolean as a portable integer parameter value.
    let boolValue (b: bool) : obj = box (if b then 1 else 0)

/// Current-schema markers for provider-neutral ADO.NET tables.
module AdoSchema =
    [<Literal>]
    let CurrentVersion = 1

    [<Literal>]
    let private MarkerTable = "nao_schema_versions"

    let private tableExists (factory: DbConnectionFactory) tableName =
        task {
            try
                let! _ = Ado.query factory (sprintf "SELECT * FROM %s WHERE 1 = 0" tableName) [] ignore
                return true
            with :? DbException ->
                return false
        }

    let private invalid schemaKey message =
        InvalidDataException(
            sprintf
                "ADO.NET component '%s' %s Follow docs/migrations before accessing or mutating this database."
                schemaKey
                message
        )

    let ensureVersionedTableVersion factory schemaKey expectedVersion tableName createTableSql =
        task {
            let! markerExists = tableExists factory MarkerTable
            let! dataTableExists = tableExists factory tableName

            if not markerExists && dataTableExists then
                raise (invalid schemaKey (sprintf "has an unversioned '%s' table." tableName))

            if markerExists then
                let! versions =
                    try
                        Ado.query
                            factory
                            (sprintf "SELECT schema_version FROM %s WHERE component = @component" MarkerTable)
                            [ "@component", box schemaKey ]
                            (fun reader -> Convert.ToInt32(reader.["schema_version"]))
                    with ex ->
                        raise (invalid schemaKey (sprintf "has an invalid schema marker: %s." ex.Message))

                match versions with
                | [ version ] when version = expectedVersion && dataTableExists -> ()
                | [ version ] when version <> expectedVersion ->
                    raise (
                        invalid
                            schemaKey
                            (sprintf "uses unsupported schema version %d; expected %d." version expectedVersion)
                    )
                | [ _ ] -> raise (invalid schemaKey (sprintf "is missing its '%s' table." tableName))
                | [] when dataTableExists ->
                    raise (invalid schemaKey (sprintf "has an unversioned '%s' table." tableName))
                | [] ->
                    do!
                        Ado.executeTransaction
                            factory
                            [ sprintf
                                  "INSERT INTO %s (component, schema_version) VALUES (@component, @version)"
                                  MarkerTable,
                              [ "@component", box schemaKey; "@version", box expectedVersion ]
                              createTableSql, [] ]
                | _ -> raise (invalid schemaKey "has duplicate schema markers.")
            else
                do!
                    Ado.executeTransaction
                        factory
                        [ sprintf
                              "CREATE TABLE IF NOT EXISTS %s (component TEXT NOT NULL PRIMARY KEY, schema_version INTEGER NOT NULL)"
                              MarkerTable,
                          []
                          sprintf "INSERT INTO %s (component, schema_version) VALUES (@component, @version)" MarkerTable,
                          [ "@component", box schemaKey; "@version", box expectedVersion ]
                          createTableSql, [] ]
        }

    let ensureVersionedTable factory schemaKey tableName createTableSql =
        ensureVersionedTableVersion factory schemaKey CurrentVersion tableName createTableSql
