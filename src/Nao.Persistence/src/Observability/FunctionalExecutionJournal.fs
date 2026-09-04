namespace Nao.Persistence

open System
open System.Data.Common
open System.IO
open System.Threading.Tasks
open Nao.Agents

module private JournalOperations =
    let private failure =
        PlatformFailure.fromException PlatformFailureBoundary.Storage None

    let requireOwner owner =
        if String.IsNullOrWhiteSpace owner then
            invalidArg (nameof owner) "Execution journal owner cannot be blank."

    let protect owner operation =
        task {
            if String.IsNullOrWhiteSpace owner then
                return
                    Error(
                        PlatformFailure.create
                            PlatformErrorCategory.InvalidInput
                            "Execution journal owner cannot be blank."
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

module InMemoryExecutionJournal =
    let create () : ExecutionJournal =
        let entries = System.Collections.Generic.List<ExecutionRecord>()

        let recordAsync (record: ExecutionRecord) =
            JournalOperations.requireOwner record.Owner
            lock entries (fun () -> entries.Insert(0, record))
            Task.CompletedTask

        let getHistoryAsync () =
            lock entries (fun () -> entries |> Seq.toList) |> Task.FromResult

        let getRevertibleAsync () =
            lock entries (fun () -> entries |> Seq.filter (fun entry -> not entry.Reverted) |> Seq.toList)
            |> Task.FromResult

        let markRevertedAsync (recordId: Guid) =
            lock entries (fun () ->
                match entries |> Seq.tryFindIndex (fun entry -> entry.Id = recordId) with
                | Some index -> entries.[index] <- { entries.[index] with Reverted = true }
                | None -> ())

            Task.CompletedTask

        let delete (predicate: ExecutionRecord -> bool) =
            lock entries (fun () -> entries.RemoveAll(fun record -> predicate record))
            |> Task.FromResult

        let deleteOwnerAsync owner =
            JournalOperations.protect owner (fun () -> delete (fun record -> record.Owner = owner))

        let deleteExpiredAsync owner before =
            JournalOperations.protect owner (fun () ->
                delete (fun record -> record.Owner = owner && record.ExecutedAt < before))

        { RecordAsync = recordAsync
          GetHistoryAsync = getHistoryAsync
          GetRevertibleAsync = getRevertibleAsync
          MarkRevertedAsync = markRevertedAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }

module AdoExecutionJournal =
    let create (factory: DbConnectionFactory) : ExecutionJournal =
        let ensureAsync () =
            task {
                let! _ =
                    Ado.executeNonQuery
                        factory
                        "CREATE TABLE IF NOT EXISTS nao_execution_journal (record_id TEXT NOT NULL PRIMARY KEY, owner TEXT NOT NULL, turn_id TEXT NOT NULL, tool_name TEXT NOT NULL, tool_input TEXT NOT NULL, tool_output TEXT NOT NULL, executed_at TEXT NOT NULL, reverted INTEGER NOT NULL, metadata TEXT NOT NULL)"
                        []

                let! _ =
                    Ado.executeNonQuery
                        factory
                        "CREATE INDEX IF NOT EXISTS nao_execution_journal_owner_time ON nao_execution_journal (owner, executed_at)"
                        []

                return ()
            }
            :> Task

        let mapRecord (reader: DbDataReader) : ExecutionRecord =
            { Id = Guid.Parse(Ado.getString reader "record_id")
              Owner = Ado.getString reader "owner"
              TurnId = Ado.getString reader "turn_id"
              ToolName = Ado.getString reader "tool_name"
              Input = Ado.getString reader "tool_input"
              Output = Ado.getString reader "tool_output"
              ExecutedAt = Time.fromIso (Ado.getString reader "executed_at")
              Reverted = Ado.getBool reader "reverted"
              Metadata = Json.mapFromJson (Ado.getString reader "metadata") }

        let recordAsync (record: ExecutionRecord) =
            task {
                JournalOperations.requireOwner record.Owner
                do! ensureAsync ()

                let! _ =
                    Ado.executeNonQuery
                        factory
                        "INSERT INTO nao_execution_journal (record_id, owner, turn_id, tool_name, tool_input, tool_output, executed_at, reverted, metadata) VALUES (@id, @ow, @tr, @tn, @ti, @to, @ea, @rv, @md)"
                        [ "@id", box (record.Id.ToString("D"))
                          "@ow", box record.Owner
                          "@tr", box record.TurnId
                          "@tn", box record.ToolName
                          "@ti", box record.Input
                          "@to", box record.Output
                          "@ea", box (Time.toIso record.ExecutedAt)
                          "@rv", Ado.boolValue record.Reverted
                          "@md", box (Json.mapToJson record.Metadata) ]

                return ()
            }
            :> Task

        let getHistoryAsync () =
            task {
                do! ensureAsync ()

                return!
                    Ado.query
                        factory
                        "SELECT record_id, owner, turn_id, tool_name, tool_input, tool_output, executed_at, reverted, metadata FROM nao_execution_journal ORDER BY executed_at DESC"
                        []
                        mapRecord
            }

        let getRevertibleAsync () =
            task {
                do! ensureAsync ()

                return!
                    Ado.query
                        factory
                        "SELECT record_id, owner, turn_id, tool_name, tool_input, tool_output, executed_at, reverted, metadata FROM nao_execution_journal WHERE reverted = 0 ORDER BY executed_at DESC"
                        []
                        mapRecord
            }

        let markRevertedAsync recordId =
            task {
                do! ensureAsync ()

                let! _ =
                    Ado.executeNonQuery
                        factory
                        "UPDATE nao_execution_journal SET reverted = 1 WHERE record_id = @id"
                        [ "@id", box (string recordId) ]

                return ()
            }
            :> Task

        let deleteOwnerAsync owner =
            JournalOperations.protect owner (fun () ->
                task {
                    do! ensureAsync ()

                    return!
                        Ado.executeNonQuery
                            factory
                            "DELETE FROM nao_execution_journal WHERE owner = @ow"
                            [ "@ow", box owner ]
                })

        let deleteExpiredAsync owner before =
            JournalOperations.protect owner (fun () ->
                task {
                    do! ensureAsync ()

                    return!
                        Ado.executeNonQuery
                            factory
                            "DELETE FROM nao_execution_journal WHERE owner = @ow AND executed_at < @before"
                            [ "@ow", box owner; "@before", box (Time.toIso before) ]
                })

        { RecordAsync = recordAsync
          GetHistoryAsync = getHistoryAsync
          GetRevertibleAsync = getRevertibleAsync
          MarkRevertedAsync = markRevertedAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }

module FileExecutionJournal =
    [<CLIMutable>]
    type JournalDocument =
        { SchemaVersion: int
          Records: Dto.ExecutionRecordDto list }

    let create baseDir : ExecutionJournal =
        let sync = obj ()
        let file = Path.Combine(baseDir, "execution-journal.json")

        let load () =
            if not (File.Exists file) then
                []
            else
                let document =
                    FileJson.read<JournalDocument> file Unchecked.defaultof<JournalDocument>

                if isNull (box document) || document.SchemaVersion <> 1 then
                    raise (InvalidDataException "Unsupported execution journal schema.")

                document.Records

        let save records =
            FileJson.write file { SchemaVersion = 1; Records = records }

        let recordAsync (record: ExecutionRecord) =
            JournalOperations.requireOwner record.Owner
            task { lock sync (fun () -> save (Dto.toExecutionDto record :: load ())) } :> Task

        let getHistoryAsync () =
            task { return lock sync (fun () -> load () |> List.map Dto.ofExecutionDto) }

        let getRevertibleAsync () =
            task {
                return
                    lock sync (fun () ->
                        load ()
                        |> List.map Dto.ofExecutionDto
                        |> List.filter (fun record -> not record.Reverted))
            }

        let markRevertedAsync (recordId: Guid) =
            task {
                lock sync (fun () ->
                    let mutable marked = false

                    load ()
                    |> List.map (fun dto ->
                        if not marked && dto.Id = recordId then
                            marked <- true
                            { dto with Reverted = true }
                        else
                            dto)
                    |> save)
            }
            :> Task

        let delete (predicate: ExecutionRecord -> bool) =
            task {
                return
                    lock sync (fun () ->
                        let records = load () in
                        let retained = records |> List.filter (Dto.ofExecutionDto >> predicate >> not) in
                        save retained
                        records.Length - retained.Length)
            }

        let deleteOwnerAsync owner =
            JournalOperations.protect owner (fun () -> delete (fun record -> record.Owner = owner))

        let deleteExpiredAsync owner before =
            JournalOperations.protect owner (fun () ->
                delete (fun record -> record.Owner = owner && record.ExecutedAt < before))

        { RecordAsync = recordAsync
          GetHistoryAsync = getHistoryAsync
          GetRevertibleAsync = getRevertibleAsync
          MarkRevertedAsync = markRevertedAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync }

module ExecutionJournals =
    let ado factory : ExecutionJournal = AdoExecutionJournal.create factory
    let file baseDir : ExecutionJournal = FileExecutionJournal.create baseDir
