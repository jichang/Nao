namespace Nao.Persistence

open System
open System.IO
open System.Threading.Tasks
open Nao.Agents

// ─── Database (ADO.NET) implementations of the feedback stores ───

/// Generic JSON-payload table helpers shared by the ADO-backed feedback stores.
///
/// Each feedback artifact is stored as a single row: a string primary key plus a
/// JSON payload column serialized with <see cref="FeedbackJson"/>. Filtering and
/// status edits load the (small) artifact set and project in F#, mirroring the
/// JSONL file stores. The schema is portable (CREATE TABLE IF NOT EXISTS) and works
/// against any ADO.NET provider supplied via <see cref="IDbConnectionFactory"/>.
module private AdoPayload =

    let ensure (factory: IDbConnectionFactory) (table: string) : Task =
        Ado.executeNonQuery
            factory
            (sprintf "CREATE TABLE IF NOT EXISTS %s (item_id TEXT NOT NULL PRIMARY KEY, payload TEXT NOT NULL)" table)
            []
        :> Task

    let getAll<'a> (factory: IDbConnectionFactory) (table: string) : Task<'a list> =
        task {
            do! ensure factory table
            return!
                Ado.query
                    factory
                    (sprintf "SELECT payload FROM %s" table)
                    []
                    (fun r -> FeedbackJson.deserialize<'a> (Ado.getString r "payload"))
        }

    /// Insert-or-replace a single artifact by primary key (DELETE + INSERT in one tx).
    let upsert (factory: IDbConnectionFactory) (table: string) (id: string) (item: 'a) : Task =
        task {
            do! ensure factory table
            do!
                Ado.executeTransaction
                    factory
                    [ sprintf "DELETE FROM %s WHERE item_id = @id" table, [ "@id", box id ]
                      sprintf "INSERT INTO %s (item_id, payload) VALUES (@id, @p)" table,
                      [ "@id", box id; "@p", box (FeedbackJson.serialize item) ] ]
        }

    let delete (factory: IDbConnectionFactory) (table: string) (id: string) : Task<bool> =
        task {
            do! ensure factory table
            let! n =
                Ado.executeNonQuery factory (sprintf "DELETE FROM %s WHERE item_id = @id" table) [ "@id", box id ]
            return n > 0
        }

/// Turns persisted in the nao_feedback_turns table (keyed by TurnId).
type AdoTurnStore(factory: IDbConnectionFactory) =
    let table = "nao_feedback_turns"
    interface ITurnStore with
        member _.SaveAsync(turn) = AdoPayload.upsert factory table turn.TurnId turn
        member _.GetAsync(turnId) =
            task {
                let! all = AdoPayload.getAll<TurnRecord> factory table
                return all |> List.tryFind (fun t -> t.TurnId = turnId)
            }
        member _.GetForSessionAsync(sessionId) =
            task {
                let! all = AdoPayload.getAll<TurnRecord> factory table
                return all |> List.filter (fun t -> t.SessionId = sessionId)
            }

/// Feedback persisted in the nao_feedback_entries table (keyed by feedback Id).
type AdoFeedbackStore(factory: IDbConnectionFactory) =
    let table = "nao_feedback_entries"
    interface IFeedbackStore with
        member _.SaveAsync(feedback) = AdoPayload.upsert factory table (feedback.Id.ToString("D")) feedback
        member _.GetForTurnAsync(turnId) =
            task {
                let! all = AdoPayload.getAll<Feedback> factory table
                return all |> List.filter (fun f -> f.TurnId = turnId)
            }
        member _.GetForSessionAsync(sessionId) =
            task {
                let! all = AdoPayload.getAll<Feedback> factory table
                return all |> List.filter (fun f -> f.SessionId = sessionId)
            }
        member _.GetAllAsync() = AdoPayload.getAll<Feedback> factory table

// ─── File (JSONL) implementations of the feedback stores (moved out of Nao.Agents) ───

/// Append-only JSONL helpers shared by the file-backed stores.
module private Jsonl =
    let private sync = obj ()

    let append (path: string) (line: string) =
        lock sync (fun () ->
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.AppendAllText(path, line + "\n"))

    let readAll<'a> (path: string) : 'a list =
        if not (File.Exists path) then []
        else
            File.ReadAllLines path
            |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
            |> Array.choose (fun l ->
                try Some (FeedbackJson.deserialize<'a> l) with _ -> None)
            |> Array.toList

    /// Rewrite the whole file from a list (used for update/delete on mutable stores).
    let writeAll<'a> (path: string) (items: 'a list) =
        lock sync (fun () ->
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            let lines = items |> List.map FeedbackJson.serialize
            File.WriteAllText(path, String.Join("\n", lines) + (if List.isEmpty lines then "" else "\n")))

/// Turn records persisted as JSONL at <baseDir>/turns.jsonl.
type FileTurnStore(baseDir: string) =
    let path = Path.Combine(baseDir, "turns.jsonl")
    interface ITurnStore with
        member _.SaveAsync(turn) =
            Jsonl.append path (FeedbackJson.serialize turn)
            Task.CompletedTask
        member _.GetAsync(turnId) =
            Jsonl.readAll<TurnRecord> path
            |> List.rev
            |> List.tryFind (fun t -> t.TurnId = turnId)
            |> Task.FromResult
        member _.GetForSessionAsync(sessionId) =
            Jsonl.readAll<TurnRecord> path
            |> List.filter (fun t -> t.SessionId = sessionId)
            |> Task.FromResult

/// Feedback persisted as JSONL at <baseDir>/feedback.jsonl.
type FileFeedbackStore(baseDir: string) =
    let path = Path.Combine(baseDir, "feedback.jsonl")
    interface IFeedbackStore with
        member _.SaveAsync(feedback) =
            Jsonl.append path (FeedbackJson.serialize feedback)
            Task.CompletedTask
        member _.GetForTurnAsync(turnId) =
            Jsonl.readAll<Feedback> path
            |> List.filter (fun f -> f.TurnId = turnId)
            |> Task.FromResult
        member _.GetForSessionAsync(sessionId) =
            Jsonl.readAll<Feedback> path
            |> List.filter (fun f -> f.SessionId = sessionId)
            |> Task.FromResult
        member _.GetAllAsync() =
            Jsonl.readAll<Feedback> path |> Task.FromResult

/// Backend factories for <see cref="FeedbackService"/>, mirroring <see cref="PersistenceMode"/>:
///   File baseDir (local JSONL files) | Database factory (any ADO.NET store).
module FeedbackDb =

    /// File-backed service rooted at <baseDir> (e.g. <NAO_DATA_DIR>/feedback).
    let file (baseDir: string) : FeedbackService =
        Directory.CreateDirectory baseDir |> ignore
        FeedbackService(
            FileTurnStore baseDir,
            FileFeedbackStore baseDir)

    /// Database-backed service over any ADO.NET provider (SQLite, PostgreSQL, SQL Server, ...).
    /// Tables are created on demand (CREATE TABLE IF NOT EXISTS).
    let database (factory: IDbConnectionFactory) : FeedbackService =
        FeedbackService(
            AdoTurnStore factory,
            AdoFeedbackStore factory)

    /// Select the backend with a single knob:
    ///   File baseDir (local files) | Database factory (any ADO.NET store).
    let create (mode: PersistenceMode) : FeedbackService =
        match mode with
        | PersistenceMode.File baseDir -> file baseDir
        | PersistenceMode.Database factory -> database factory
