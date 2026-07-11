namespace Nao.Persistence

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

/// Shared System.Text.Json options with full F# support (discriminated unions,
/// options, lists, maps, single-case unions). Used by the event-sourced stores
/// so the rich domain types serialize without bespoke DTOs.
module FSharpJson =
    let options =
        let o = JsonSerializerOptions()
        o.Converters.Add(JsonFSharpConverter())
        o

    let serialize (value: 'a) : string = JsonSerializer.Serialize(value, options)
    let deserialize<'a> (s: string) : 'a = JsonSerializer.Deserialize<'a>(s, options)

/// Append-only event log used for event-sourced persistence of the richer stores.
/// The store records each mutating call as a serialized event and replays them in
/// order to rebuild in-memory state, reusing the existing in-memory query logic.
type IEventStore =
    /// Append one serialized event.
    abstract member Append: string -> unit
    /// Load all events for this stream in insertion order.
    abstract member LoadAll: unit -> string list

/// FileSystem event store: newline-delimited JSON, one event per line.
type FileEventStore(path: string) =
    let sync = obj ()

    do
        let dir = Path.GetDirectoryName(path: string)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory dir |> ignore

    interface IEventStore with
        member _.Append(json: string) =
            // Collapse newlines to keep one event per physical line.
            let oneLine = json.Replace("\r", " ").Replace("\n", " ")
            lock sync (fun () -> File.AppendAllText(path, oneLine + "\n"))

        member _.LoadAll() =
            lock sync (fun () ->
                if File.Exists path then
                    File.ReadAllLines path
                    |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
                    |> List.ofArray
                else
                    [])

/// ADO.NET event store: a single portable table holding ordered JSON events,
/// partitioned by a logical stream name. Provider-agnostic via IDbConnectionFactory.
type DbEventStore(factory: IDbConnectionFactory, stream: string) =
    let sync = obj ()
    let mutable nextOrd = 0L
    let mutable initialized = false

    let exec (conn: System.Data.Common.DbConnection) (sql: string) (parameters: (string * obj) list) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        for (n, v) in parameters do
            let p = cmd.CreateParameter()
            p.ParameterName <- n
            p.Value <- (if isNull v then box DBNull.Value else v)
            cmd.Parameters.Add p |> ignore
        cmd

    let init () =
        if not initialized then
            lock sync (fun () ->
                if not initialized then
                    use conn = factory.Create()
                    conn.Open()
                    use create =
                        exec
                            conn
                            "CREATE TABLE IF NOT EXISTS nao_events (stream TEXT NOT NULL, ord INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY (stream, ord))"
                            []
                    create.ExecuteNonQuery() |> ignore
                    use maxCmd =
                        exec conn "SELECT COALESCE(MAX(ord), -1) FROM nao_events WHERE stream = @s" [ "@s", box stream ]
                    nextOrd <- Convert.ToInt64(maxCmd.ExecuteScalar()) + 1L
                    initialized <- true)

    interface IEventStore with
        member _.Append(json: string) =
            init ()
            lock sync (fun () ->
                use conn = factory.Create()
                conn.Open()
                use cmd =
                    exec
                        conn
                        "INSERT INTO nao_events (stream, ord, payload) VALUES (@s, @o, @p)"
                        [ "@s", box stream; "@o", box nextOrd; "@p", box json ]
                cmd.ExecuteNonQuery() |> ignore
                nextOrd <- nextOrd + 1L)

        member _.LoadAll() =
            init ()
            lock sync (fun () ->
                use conn = factory.Create()
                conn.Open()
                use cmd =
                    exec conn "SELECT payload FROM nao_events WHERE stream = @s ORDER BY ord ASC" [ "@s", box stream ]
                use reader = cmd.ExecuteReader()
                let results = ResizeArray<string>()
                while reader.Read() do
                    results.Add(reader.GetString 0)
                List.ofSeq results)

/// Factory helpers for event stores.
module EventStore =
    /// FileSystem-backed event log at the given path.
    let file (path: string) : IEventStore = FileEventStore(path) :> IEventStore

    /// ADO.NET-backed event log over any provider, partitioned by stream name.
    let db (factory: IDbConnectionFactory) (stream: string) : IEventStore =
        DbEventStore(factory, stream) :> IEventStore
