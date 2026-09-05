namespace Nao.Persistence

open System
open System.IO
open System.Text.Json.Serialization
open System.Threading.Tasks
open Nao.Agents

module private TurnOperations =
    let private failure =
        PlatformFailure.fromException PlatformFailureBoundary.Storage None

    let protect (sessionId: string) operation =
        task {
            if String.IsNullOrWhiteSpace sessionId then
                return
                    Error(
                        PlatformFailure.create
                            PlatformErrorCategory.InvalidInput
                            "Turn session cannot be blank."
                            false
                            None
                    )
            else
                try
                    let! count = operation ()
                    return Ok count
                with ex ->
                    return Error(failure ex)
        }

module private FeedbackOperations =
    let private failure =
        PlatformFailure.fromException PlatformFailureBoundary.Storage None

    let protect (owner: string) operation =
        task {
            if String.IsNullOrWhiteSpace owner then
                return
                    Error(
                        PlatformFailure.create
                            PlatformErrorCategory.InvalidInput
                            "Feedback owner cannot be blank."
                            false
                            None
                    )
            else
                try
                    let! count = operation ()
                    return Ok count
                with ex ->
                    return Error(failure ex)
        }

// ─── Database (ADO.NET) implementations of the feedback stores ───

/// Generic JSON-payload table helpers shared by the ADO-backed feedback stores.
///
/// Each feedback artifact is stored as a single row: a string primary key plus a
/// JSON payload column serialized with <see cref="FeedbackJson"/>. Filtering and
/// status edits load the (small) artifact set and project in F#, mirroring the
/// JSONL file stores. The schema is portable (CREATE TABLE IF NOT EXISTS) and works
/// against any ADO.NET provider supplied via <see cref="DbConnectionFactory"/>.
module private AdoPayload =

    let schemaKey table =
        match table with
        | "nao_feedback_turns" -> "feedback-turns"
        | "nao_feedback_entries" -> "feedback-entries"
        | _ -> invalidArg (nameof table) (sprintf "Unknown feedback table '%s'." table)

    let ensure (factory: DbConnectionFactory) (table: string) : Task =
        AdoSchema.ensureVersionedTable
            factory
            (schemaKey table)
            table
            (sprintf "CREATE TABLE IF NOT EXISTS %s (item_id TEXT NOT NULL PRIMARY KEY, payload TEXT NOT NULL)" table)

    let getAll<'a> (factory: DbConnectionFactory) (table: string) : Task<'a list> =
        task {
            do! ensure factory table

            return!
                Ado.query factory (sprintf "SELECT item_id, payload FROM %s" table) [] (fun reader ->
                    let id = Ado.getString reader "item_id"

                    try
                        FeedbackJson.deserialize<'a> (Ado.getString reader "payload")
                    with ex ->
                        raise (
                            InvalidDataException(
                                sprintf
                                    "Feedback table '%s' row '%s' is invalid. Follow docs/migrations before writing."
                                    table
                                    id,
                                ex
                            )
                        ))
        }

    /// Insert-or-replace a single artifact by primary key (DELETE + INSERT in one tx).
    let upsert (factory: DbConnectionFactory) (table: string) (id: string) (item: 'a) : Task =
        task {
            let! _ = getAll<'a> factory table

            do!
                Ado.executeTransaction
                    factory
                    [ sprintf "DELETE FROM %s WHERE item_id = @id" table, [ "@id", box id ]
                      sprintf "INSERT INTO %s (item_id, payload) VALUES (@id, @p)" table,
                      [ "@id", box id; "@p", box (FeedbackJson.serialize item) ] ]
        }

    let delete (factory: DbConnectionFactory) (table: string) (id: string) : Task<bool> =
        task {
            do! ensure factory table
            let! n = Ado.executeNonQuery factory (sprintf "DELETE FROM %s WHERE item_id = @id" table) [ "@id", box id ]
            return n > 0
        }

/// Turns persisted in the nao_feedback_turns table (keyed by TurnId).
module AdoTurnStore =
    let create (factory: DbConnectionFactory) : TurnStore =
        let table = "nao_feedback_turns"

        let saveAsync (turn: TurnRecord) =
            AdoPayload.upsert factory table turn.TurnId turn

        let getAsync (turnId: string) =
            task {
                let! all = AdoPayload.getAll<TurnRecord> factory table
                return all |> List.tryFind (fun t -> t.TurnId = turnId)
            }

        let getForSessionAsync (sessionId: string) =
            task {
                let! all = AdoPayload.getAll<TurnRecord> factory table
                return all |> List.filter (fun t -> t.SessionId = sessionId)
            }

        let getForExecutionAsync executionId =
            task {
                let! all = AdoPayload.getAll<TurnRecord> factory table
                return all |> List.filter (fun turn -> turn.Correlation.ExecutionId = executionId)
            }

        let delete (predicate: TurnRecord -> bool) =
            task {
                let! all = AdoPayload.getAll<TurnRecord> factory table
                let matches = all |> List.filter predicate
                let mutable deleted = 0

                for turn in matches do
                    let! removed = AdoPayload.delete factory table turn.TurnId

                    if removed then
                        deleted <- deleted + 1

                return deleted
            }

        let deleteSessionAsync (sessionId: string) =
            TurnOperations.protect sessionId (fun () -> delete (fun turn -> turn.SessionId = sessionId))

        let deleteExpiredAsync (sessionId: string) before =
            TurnOperations.protect sessionId (fun () ->
                delete (fun turn -> turn.SessionId = sessionId && turn.CreatedAt < before))

        { SaveAsync = saveAsync
          GetAsync = getAsync
          GetForSessionAsync = getForSessionAsync
          GetForExecutionAsync = getForExecutionAsync
          DeleteSessionAsync = deleteSessionAsync
          DeleteExpiredAsync = deleteExpiredAsync }

/// Feedback persisted in the nao_feedback_entries table (keyed by feedback Id).
module AdoFeedbackStore =
    let create (factory: DbConnectionFactory) : FeedbackStore =
        let table = "nao_feedback_entries"

        let saveAsync (feedback: Feedback) =
            AdoPayload.upsert factory table (feedback.Id.ToString("D")) feedback

        let getForTurnAsync turnId =
            task {
                let! all = AdoPayload.getAll<Feedback> factory table
                return all |> List.filter (fun f -> f.TurnId = turnId)
            }

        let getForSessionAsync sessionId =
            task {
                let! all = AdoPayload.getAll<Feedback> factory table
                return all |> List.filter (fun f -> f.SessionId = sessionId)
            }

        let getAllAsync () =
            AdoPayload.getAll<Feedback> factory table

        let delete (predicate: Feedback -> bool) =
            task {
                let! all = getAllAsync ()
                let matches = all |> List.filter predicate
                let mutable deleted = 0

                for feedback in matches do
                    let! removed = AdoPayload.delete factory table (feedback.Id.ToString("D"))

                    if removed then
                        deleted <- deleted + 1

                return deleted
            }

        let deleteOwnerAsync (owner: string) =
            FeedbackOperations.protect owner (fun () -> delete (fun feedback -> feedback.UserId = owner))

        let deleteExpiredAsync (owner: string) before =
            FeedbackOperations.protect owner (fun () ->
                delete (fun feedback -> feedback.UserId = owner && feedback.CreatedAt < before))

        { SaveAsync = saveAsync
          GetForTurnAsync = getForTurnAsync
          GetForSessionAsync = getForSessionAsync
          GetAllAsync = getAllAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }

// ─── File (JSONL) implementations of the feedback stores (moved out of Nao.Agents) ───

/// Append-only JSONL helpers shared by the file-backed stores.
module private Jsonl =
    let private sync = obj ()

    let append (path: string) (line: string) =
        lock sync (fun () ->
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.AppendAllText(path, line + "\n"))

    let readAll<'a> (path: string) : 'a list =
        if not (File.Exists path) then
            []
        else
            File.ReadAllLines path
            |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
            |> Array.choose (fun l ->
                try
                    Some(FeedbackJson.deserialize<'a> l)
                with _ ->
                    None)
            |> Array.toList

    /// Rewrite the whole file from a list (used for update/delete on mutable stores).
    let writeAll<'a> (path: string) (items: 'a list) =
        lock sync (fun () ->
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            let lines = items |> List.map FeedbackJson.serialize
            File.WriteAllText(path, String.Join("\n", lines) + (if List.isEmpty lines then "" else "\n")))

[<CLIMutable>]
type TurnStoreEnvelope =
    { [<JsonPropertyName("schemaVersion")>]
      SchemaVersion: int
      [<JsonPropertyName("kind")>]
      Kind: string
      [<JsonPropertyName("record")>]
      Record: TurnRecord option
      [<JsonPropertyName("sessionId")>]
      SessionId: string option
      [<JsonPropertyName("before")>]
      Before: DateTimeOffset option }

[<CLIMutable>]
type FeedbackStoreEnvelope =
    { [<JsonPropertyName("schemaVersion")>]
      SchemaVersion: int
      [<JsonPropertyName("kind")>]
      Kind: string
      [<JsonPropertyName("record")>]
      Record: Feedback option
      [<JsonPropertyName("owner")>]
      Owner: string option
      [<JsonPropertyName("before")>]
      Before: DateTimeOffset option }

[<RequireQualifiedAccess>]
type private TurnStoreEvent =
    | Save of TurnRecord
    | DeleteSession of string
    | DeleteExpired of string * DateTimeOffset

[<RequireQualifiedAccess>]
type private FeedbackStoreEvent =
    | Save of Feedback
    | DeleteOwner of string
    | DeleteExpired of string * DateTimeOffset

module private LifecycleEnvelope =
    let readAt path lineNumber decode line =
        try
            decode lineNumber line
        with ex ->
            raise (
                InvalidDataException(
                    sprintf
                        "Lifecycle event file '%s' is invalid at line %d. Follow docs/migrations before writing."
                        path
                        lineNumber,
                    ex
                )
            )

    let turn event =
        match event with
        | TurnStoreEvent.Save record ->
            { SchemaVersion = 1
              Kind = "turn.upsert"
              Record = Some record
              SessionId = None
              Before = None }
        | TurnStoreEvent.DeleteSession sessionId ->
            { SchemaVersion = 1
              Kind = "turn.session-deleted"
              Record = None
              SessionId = Some sessionId
              Before = None }
        | TurnStoreEvent.DeleteExpired(sessionId, before) ->
            { SchemaVersion = 1
              Kind = "turn.expired-deleted"
              Record = None
              SessionId = Some sessionId
              Before = Some before }

    let readTurn lineNumber line =
        let envelope = FeedbackJson.deserialize<TurnStoreEnvelope> line

        if envelope.SchemaVersion <> 1 then
            raise (InvalidDataException(sprintf "Unsupported turn-store schema version at line %d." lineNumber))

        match envelope.Kind, envelope.Record, envelope.SessionId, envelope.Before with
        | "turn.upsert", Some record, _, _ -> TurnStoreEvent.Save record
        | "turn.session-deleted", _, Some sessionId, _ -> TurnStoreEvent.DeleteSession sessionId
        | "turn.expired-deleted", _, Some sessionId, Some before -> TurnStoreEvent.DeleteExpired(sessionId, before)
        | _ ->
            raise (InvalidDataException(sprintf "Invalid turn-store event '%s' at line %d." envelope.Kind lineNumber))

    let feedback event =
        match event with
        | FeedbackStoreEvent.Save record ->
            { SchemaVersion = 1
              Kind = "feedback.upsert"
              Record = Some record
              Owner = None
              Before = None }
        | FeedbackStoreEvent.DeleteOwner owner ->
            { SchemaVersion = 1
              Kind = "feedback.owner-deleted"
              Record = None
              Owner = Some owner
              Before = None }
        | FeedbackStoreEvent.DeleteExpired(owner, before) ->
            { SchemaVersion = 1
              Kind = "feedback.expired-deleted"
              Record = None
              Owner = Some owner
              Before = Some before }

    let readFeedback lineNumber line =
        let envelope = FeedbackJson.deserialize<FeedbackStoreEnvelope> line

        if envelope.SchemaVersion <> 1 then
            raise (InvalidDataException(sprintf "Unsupported feedback-store schema version at line %d." lineNumber))

        match envelope.Kind, envelope.Record, envelope.Owner, envelope.Before with
        | "feedback.upsert", Some record, _, _ -> FeedbackStoreEvent.Save record
        | "feedback.owner-deleted", _, Some owner, _ -> FeedbackStoreEvent.DeleteOwner owner
        | "feedback.expired-deleted", _, Some owner, Some before -> FeedbackStoreEvent.DeleteExpired(owner, before)
        | _ ->
            raise (
                InvalidDataException(sprintf "Invalid feedback-store event '%s' at line %d." envelope.Kind lineNumber)
            )

/// Turn records persisted as JSONL at <baseDir>/turns.jsonl.
module FileTurnStore =
    let create (baseDir: string) : TurnStore =
        let path = Path.Combine(baseDir, "turns.jsonl")

        let readEvents () =
            if not (File.Exists path) then
                []
            else
                File.ReadAllLines path
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
                |> Array.mapi (fun index line ->
                    LifecycleEnvelope.readAt path (index + 1) LifecycleEnvelope.readTurn line)
                |> Array.toList

        let load () =
            let turns = System.Collections.Generic.Dictionary<string, TurnRecord>()

            let remove (predicate: TurnRecord -> bool) =
                turns.Values
                |> Seq.filter predicate
                |> Seq.map (fun turn -> turn.TurnId)
                |> Seq.toArray
                |> Array.iter (turns.Remove >> ignore)

            for event in readEvents () do
                match event with
                | TurnStoreEvent.Save turn -> turns.[turn.TurnId] <- turn
                | TurnStoreEvent.DeleteSession sessionId -> remove (fun turn -> turn.SessionId = sessionId)
                | TurnStoreEvent.DeleteExpired(sessionId, before) ->
                    remove (fun turn -> turn.SessionId = sessionId && turn.CreatedAt < before)

            turns.Values |> Seq.toList

        let saveAsync (turn: TurnRecord) =
            readEvents () |> ignore
            Jsonl.append path (FeedbackJson.serialize (LifecycleEnvelope.turn (TurnStoreEvent.Save turn)))
            Task.CompletedTask

        let getAsync (turnId: string) =
            load () |> List.tryFind (fun turn -> turn.TurnId = turnId) |> Task.FromResult

        let getForSessionAsync (sessionId: string) =
            load ()
            |> List.filter (fun turn -> turn.SessionId = sessionId)
            |> Task.FromResult

        let getForExecutionAsync executionId =
            load ()
            |> List.filter (fun turn -> turn.Correlation.ExecutionId = executionId)
            |> Task.FromResult

        let delete event (predicate: TurnRecord -> bool) =
            let count = load () |> List.filter predicate |> List.length
            Jsonl.append path (FeedbackJson.serialize (LifecycleEnvelope.turn event))
            Task.FromResult count

        let deleteSessionAsync (sessionId: string) =
            TurnOperations.protect sessionId (fun () ->
                delete (TurnStoreEvent.DeleteSession sessionId) (fun turn -> turn.SessionId = sessionId))

        let deleteExpiredAsync (sessionId: string) before =
            TurnOperations.protect sessionId (fun () ->
                delete (TurnStoreEvent.DeleteExpired(sessionId, before)) (fun turn ->
                    turn.SessionId = sessionId && turn.CreatedAt < before))

        { SaveAsync = saveAsync
          GetAsync = getAsync
          GetForSessionAsync = getForSessionAsync
          GetForExecutionAsync = getForExecutionAsync
          DeleteSessionAsync = deleteSessionAsync
          DeleteExpiredAsync = deleteExpiredAsync }

/// Feedback persisted as JSONL at <baseDir>/feedback.jsonl.
module FileFeedbackStore =
    let create (baseDir: string) : FeedbackStore =
        let path = Path.Combine(baseDir, "feedback.jsonl")

        let readEvents () =
            if not (File.Exists path) then
                []
            else
                File.ReadAllLines path
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
                |> Array.mapi (fun index line ->
                    LifecycleEnvelope.readAt path (index + 1) LifecycleEnvelope.readFeedback line)
                |> Array.toList

        let load () =
            let entries = System.Collections.Generic.Dictionary<Guid, Feedback>()

            let remove (predicate: Feedback -> bool) =
                entries.Values
                |> Seq.filter predicate
                |> Seq.map (fun feedback -> feedback.Id)
                |> Seq.toArray
                |> Array.iter (entries.Remove >> ignore)

            for event in readEvents () do
                match event with
                | FeedbackStoreEvent.Save feedback -> entries.[feedback.Id] <- feedback
                | FeedbackStoreEvent.DeleteOwner owner -> remove (fun feedback -> feedback.UserId = owner)
                | FeedbackStoreEvent.DeleteExpired(owner, before) ->
                    remove (fun feedback -> feedback.UserId = owner && feedback.CreatedAt < before)

            entries.Values |> Seq.toList

        let saveAsync (feedback: Feedback) =
            readEvents () |> ignore
            Jsonl.append path (FeedbackJson.serialize (LifecycleEnvelope.feedback (FeedbackStoreEvent.Save feedback)))
            Task.CompletedTask

        let getForTurnAsync turnId =
            load ()
            |> List.filter (fun feedback -> feedback.TurnId = turnId)
            |> Task.FromResult

        let getForSessionAsync sessionId =
            load ()
            |> List.filter (fun feedback -> feedback.SessionId = sessionId)
            |> Task.FromResult

        let getAllAsync () = load () |> Task.FromResult

        let delete event (predicate: Feedback -> bool) =
            let count = load () |> List.filter predicate |> List.length
            Jsonl.append path (FeedbackJson.serialize (LifecycleEnvelope.feedback event))
            Task.FromResult count

        let deleteOwnerAsync (owner: string) =
            FeedbackOperations.protect owner (fun () ->
                delete (FeedbackStoreEvent.DeleteOwner owner) (fun feedback -> feedback.UserId = owner))

        let deleteExpiredAsync (owner: string) before =
            FeedbackOperations.protect owner (fun () ->
                delete (FeedbackStoreEvent.DeleteExpired(owner, before)) (fun feedback ->
                    feedback.UserId = owner && feedback.CreatedAt < before))

        { SaveAsync = saveAsync
          GetForTurnAsync = getForTurnAsync
          GetForSessionAsync = getForSessionAsync
          GetAllAsync = getAllAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }

/// Backend factories for <see cref="FeedbackService"/>, mirroring <see cref="PersistenceMode"/>:
///   File baseDir (local JSONL files) | Database factory (any ADO.NET store).
module FeedbackDb =

    /// File-backed service rooted at <baseDir> (e.g. <NAO_DATA_DIR>/feedback).
    let file (baseDir: string) : FeedbackService =
        Directory.CreateDirectory baseDir |> ignore
        FeedbackService.create (FileTurnStore.create baseDir) (FileFeedbackStore.create baseDir)

    /// Database-backed service over any ADO.NET provider (SQLite, PostgreSQL, SQL Server, ...).
    /// Tables are created on demand (CREATE TABLE IF NOT EXISTS).
    let database (factory: DbConnectionFactory) : FeedbackService =
        FeedbackService.create (AdoTurnStore.create factory) (AdoFeedbackStore.create factory)

    /// Select the backend with a single knob:
    ///   File baseDir (local files) | Database factory (any ADO.NET store).
    let create (mode: PersistenceMode) : FeedbackService =
        match mode with
        | PersistenceMode.File baseDir -> file baseDir
        | PersistenceMode.Database factory -> database factory
