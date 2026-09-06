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
        let checkpoints = System.Collections.Generic.List<HarnessCheckpoint>()

        let recordAsync (record: ExecutionRecord) =
            JournalOperations.requireOwner record.Owner
            lock entries (fun () -> entries.Insert(0, record))
            Task.CompletedTask

        let getHistoryAsync () =
            lock entries (fun () -> entries |> Seq.toList) |> Task.FromResult

        let getByExecutionAsync executionId =
            lock entries (fun () ->
                entries
                |> Seq.filter (fun entry -> entry.Correlation.ExecutionId = executionId)
                |> Seq.toList)
            |> Task.FromResult

        let saveCheckpoint (checkpoint: HarnessCheckpoint) =
            JournalOperations.requireOwner checkpoint.Owner
            lock checkpoints (fun () -> checkpoints.Add checkpoint)
            Task.CompletedTask

        let getCheckpoints (executionId: ExecutionId) =
            lock checkpoints (fun () ->
                checkpoints
                |> Seq.filter (fun checkpoint -> checkpoint.Correlation.ExecutionId = executionId)
                |> Seq.sortBy _.RecordedAt
                |> Seq.toList)
            |> Task.FromResult

        let getRevertibleAsync () =
            lock entries (fun () -> entries |> Seq.filter (fun entry -> not entry.Reverted) |> Seq.toList)
            |> Task.FromResult

        let markRevertedAsync (recordId: Guid) =
            lock entries (fun () ->
                match entries |> Seq.tryFindIndex (fun entry -> entry.Id = recordId) with
                | Some index -> entries.[index] <- { entries.[index] with Reverted = true }
                | None -> ())

            Task.CompletedTask

        let delete (recordPredicate: ExecutionRecord -> bool) (checkpointPredicate: HarnessCheckpoint -> bool) =
            let recordsDeleted =
                lock entries (fun () -> entries.RemoveAll(fun record -> recordPredicate record))

            let checkpointsDeleted =
                lock checkpoints (fun () -> checkpoints.RemoveAll(fun checkpoint -> checkpointPredicate checkpoint))

            Task.FromResult(recordsDeleted + checkpointsDeleted)

        let deleteOwnerAsync owner =
            JournalOperations.protect owner (fun () ->
                delete (fun record -> record.Owner = owner) (fun checkpoint -> checkpoint.Owner = owner))

        let deleteExpiredAsync owner before =
            JournalOperations.protect owner (fun () ->
                delete (fun record -> record.Owner = owner && record.ExecutedAt < before) (fun checkpoint ->
                    checkpoint.Owner = owner && checkpoint.RecordedAt < before))

        let checkpointJournal =
            { Save = saveCheckpoint
              GetByExecution = getCheckpoints }

        { RecordAsync = recordAsync
          GetHistoryAsync = getHistoryAsync
          GetByExecutionAsync = getByExecutionAsync
          GetRevertibleAsync = getRevertibleAsync
          MarkRevertedAsync = markRevertedAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync
          Checkpoints = checkpointJournal }

module AdoExecutionJournal =
    let create (factory: DbConnectionFactory) : ExecutionJournal =
        let ensureAsync () =
            task {
                do!
                    AdoSchema.ensureVersionedTable
                        factory
                        "execution-journal"
                        "nao_execution_journal"
                        "CREATE TABLE IF NOT EXISTS nao_execution_journal (record_id TEXT NOT NULL PRIMARY KEY, execution_id TEXT NOT NULL, correlation_id TEXT NOT NULL, causation_id TEXT NULL, attempt INTEGER NOT NULL, owner TEXT NOT NULL, turn_id TEXT NOT NULL, tool_name TEXT NOT NULL, tool_input TEXT NOT NULL, tool_output TEXT NOT NULL, executed_at TEXT NOT NULL, reverted INTEGER NOT NULL, metadata TEXT NOT NULL)"

                do!
                    AdoSchema.ensureVersionedTable
                        factory
                        "harness-checkpoints"
                        "nao_harness_checkpoints"
                        "CREATE TABLE IF NOT EXISTS nao_harness_checkpoints (checkpoint_id TEXT NOT NULL PRIMARY KEY, execution_id TEXT NOT NULL, correlation_id TEXT NOT NULL, causation_id TEXT NULL, attempt INTEGER NOT NULL, owner TEXT NOT NULL, turn_id TEXT NOT NULL, agent_id TEXT NOT NULL, phase TEXT NOT NULL, recorded_at TEXT NOT NULL)"

                let! _ =
                    Ado.executeNonQuery
                        factory
                        "CREATE INDEX IF NOT EXISTS nao_execution_journal_owner_time ON nao_execution_journal (owner, executed_at)"
                        []

                let! _ =
                    Ado.executeNonQuery
                        factory
                        "CREATE INDEX IF NOT EXISTS nao_harness_checkpoints_owner_time ON nao_harness_checkpoints (owner, recorded_at)"
                        []

                return ()
            }
            :> Task

        let mapRecord (reader: DbDataReader) : ExecutionRecord =
            let id = Ado.getString reader "record_id"

            try
                let attempt = Convert.ToInt32(reader.["attempt"])

                if attempt < 1 then
                    invalidArg (nameof attempt) "Correlation attempt must be positive."

                let correlation =
                    { ExecutionId = ExecutionId.parse (Ado.getString reader "execution_id")
                      CorrelationId = CorrelationId.parse (Ado.getString reader "correlation_id")
                      CausationId = Ado.getStringOpt reader "causation_id" |> Option.map ExecutionId.parse
                      Attempt = attempt }

                { Id = Guid.Parse id
                  Correlation = correlation
                  Owner = Ado.getString reader "owner"
                  TurnId = Ado.getString reader "turn_id"
                  ToolName = Ado.getString reader "tool_name"
                  Input = Ado.getString reader "tool_input"
                  Output = Ado.getString reader "tool_output"
                  ExecutedAt = Time.fromIso (Ado.getString reader "executed_at")
                  Reverted = Ado.getBool reader "reverted"
                  Metadata = Json.mapFromJson (Ado.getString reader "metadata") }
            with ex ->
                raise (
                    InvalidDataException(
                        sprintf "Execution-journal row '%s' is invalid. Follow docs/migrations before writing." id,
                        ex
                    )
                )

        let validateAsync () =
            task {
                do! ensureAsync ()

                let! _ =
                    Ado.query
                        factory
                        "SELECT record_id, execution_id, correlation_id, causation_id, attempt, owner, turn_id, tool_name, tool_input, tool_output, executed_at, reverted, metadata FROM nao_execution_journal"
                        []
                        mapRecord

                return ()
            }

        let mapCheckpoint (reader: DbDataReader) : HarnessCheckpoint =
            let id = Ado.getString reader "checkpoint_id"

            try
                HarnessCheckpointSerialization.ofDto
                    { Id = Guid.Parse id
                      ExecutionId = Ado.getString reader "execution_id"
                      CorrelationId = Ado.getString reader "correlation_id"
                      CausationId = Ado.getStringOpt reader "causation_id" |> Option.defaultValue null
                      Attempt = Convert.ToInt32(reader.["attempt"])
                      Owner = Ado.getString reader "owner"
                      TurnId = Ado.getString reader "turn_id"
                      AgentId = Ado.getString reader "agent_id"
                      Phase = Ado.getString reader "phase"
                      RecordedAt = Time.fromIso (Ado.getString reader "recorded_at") }
            with ex ->
                raise (
                    InvalidDataException(
                        sprintf "Harness-checkpoint row '%s' is invalid. Follow docs/migrations before writing." id,
                        ex
                    )
                )

        let saveCheckpoint (checkpoint: HarnessCheckpoint) =
            task {
                JournalOperations.requireOwner checkpoint.Owner
                do! ensureAsync ()
                let dto = HarnessCheckpointSerialization.toDto checkpoint

                let causationId =
                    Option.ofObj dto.CausationId |> Option.map box |> Option.defaultValue null

                let! _ =
                    Ado.executeNonQuery
                        factory
                        "INSERT INTO nao_harness_checkpoints (checkpoint_id, execution_id, correlation_id, causation_id, attempt, owner, turn_id, agent_id, phase, recorded_at) VALUES (@id, @ex, @co, @ca, @at, @ow, @tr, @ag, @ph, @ra)"
                        [ "@id", box (dto.Id.ToString("D"))
                          "@ex", box dto.ExecutionId
                          "@co", box dto.CorrelationId
                          "@ca", causationId
                          "@at", box dto.Attempt
                          "@ow", box dto.Owner
                          "@tr", box dto.TurnId
                          "@ag", box dto.AgentId
                          "@ph", box dto.Phase
                          "@ra", box (Time.toIso dto.RecordedAt) ]

                return ()
            }
            :> Task

        let getCheckpoints executionId =
            task {
                do! ensureAsync ()

                return!
                    Ado.query
                        factory
                        "SELECT checkpoint_id, execution_id, correlation_id, causation_id, attempt, owner, turn_id, agent_id, phase, recorded_at FROM nao_harness_checkpoints WHERE execution_id = @ex ORDER BY recorded_at ASC"
                        [ "@ex", box (ExecutionId.serialize executionId) ]
                        mapCheckpoint
            }

        let recordAsync (record: ExecutionRecord) =
            task {
                JournalOperations.requireOwner record.Owner
                do! validateAsync ()

                let causationId =
                    record.Correlation.CausationId
                    |> Option.map (ExecutionId.serialize >> box)
                    |> Option.defaultValue null

                let! _ =
                    Ado.executeNonQuery
                        factory
                        "INSERT INTO nao_execution_journal (record_id, execution_id, correlation_id, causation_id, attempt, owner, turn_id, tool_name, tool_input, tool_output, executed_at, reverted, metadata) VALUES (@id, @ex, @co, @ca, @at, @ow, @tr, @tn, @ti, @to, @ea, @rv, @md)"
                        [ "@id", box (record.Id.ToString("D"))
                          "@ex", box (ExecutionId.serialize record.Correlation.ExecutionId)
                          "@co", box (CorrelationId.serialize record.Correlation.CorrelationId)
                          "@ca", causationId
                          "@at", box record.Correlation.Attempt
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
                        "SELECT record_id, execution_id, correlation_id, causation_id, attempt, owner, turn_id, tool_name, tool_input, tool_output, executed_at, reverted, metadata FROM nao_execution_journal ORDER BY executed_at DESC"
                        []
                        mapRecord
            }

        let getByExecutionAsync executionId =
            task {
                do! ensureAsync ()

                return!
                    Ado.query
                        factory
                        "SELECT record_id, execution_id, correlation_id, causation_id, attempt, owner, turn_id, tool_name, tool_input, tool_output, executed_at, reverted, metadata FROM nao_execution_journal WHERE execution_id = @ex ORDER BY executed_at DESC"
                        [ "@ex", box (ExecutionId.serialize executionId) ]
                        mapRecord
            }

        let getRevertibleAsync () =
            task {
                do! ensureAsync ()

                return!
                    Ado.query
                        factory
                        "SELECT record_id, execution_id, correlation_id, causation_id, attempt, owner, turn_id, tool_name, tool_input, tool_output, executed_at, reverted, metadata FROM nao_execution_journal WHERE reverted = 0 ORDER BY executed_at DESC"
                        []
                        mapRecord
            }

        let markRevertedAsync recordId =
            task {
                do! validateAsync ()

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
                    do! validateAsync ()

                    let! recordsDeleted =
                        Ado.executeNonQuery
                            factory
                            "DELETE FROM nao_execution_journal WHERE owner = @ow"
                            [ "@ow", box owner ]

                    let! checkpointsDeleted =
                        Ado.executeNonQuery
                            factory
                            "DELETE FROM nao_harness_checkpoints WHERE owner = @ow"
                            [ "@ow", box owner ]

                    return recordsDeleted + checkpointsDeleted
                })

        let deleteExpiredAsync owner before =
            JournalOperations.protect owner (fun () ->
                task {
                    do! validateAsync ()

                    let! recordsDeleted =
                        Ado.executeNonQuery
                            factory
                            "DELETE FROM nao_execution_journal WHERE owner = @ow AND executed_at < @before"
                            [ "@ow", box owner; "@before", box (Time.toIso before) ]

                    let! checkpointsDeleted =
                        Ado.executeNonQuery
                            factory
                            "DELETE FROM nao_harness_checkpoints WHERE owner = @ow AND recorded_at < @before"
                            [ "@ow", box owner; "@before", box (Time.toIso before) ]

                    return recordsDeleted + checkpointsDeleted
                })

        let checkpointJournal =
            { Save = saveCheckpoint
              GetByExecution = getCheckpoints }

        { RecordAsync = recordAsync
          GetHistoryAsync = getHistoryAsync
          GetByExecutionAsync = getByExecutionAsync
          GetRevertibleAsync = getRevertibleAsync
          MarkRevertedAsync = markRevertedAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync
          Checkpoints = checkpointJournal }

module FileExecutionJournal =
    [<CLIMutable>]
    type JournalDocument =
        { SchemaVersion: int
          Records: Dto.ExecutionRecordDto list }

    [<CLIMutable>]
    type CheckpointDocument =
        { SchemaVersion: int
          Checkpoints: HarnessCheckpointSerialization.Dto list }

    let create baseDir : ExecutionJournal =
        let sync = obj ()
        let file = Path.Combine(baseDir, "execution-journal.json")
        let checkpointFile = Path.Combine(baseDir, "harness-checkpoints.json")

        let load () =
            if not (File.Exists file) then
                []
            else
                try
                    let document =
                        FileJson.read<JournalDocument> file Unchecked.defaultof<JournalDocument>

                    if isNull (box document) || document.SchemaVersion <> 1 then
                        raise (InvalidDataException "Expected execution journal schema version 1.")

                    document.Records
                with ex ->
                    raise (
                        InvalidDataException(
                            sprintf "Execution journal '%s' is invalid. Follow docs/migrations before writing." file,
                            ex
                        )
                    )

        let save records =
            FileJson.write file { SchemaVersion = 1; Records = records }

        let loadCheckpoints () =
            if not (File.Exists checkpointFile) then
                []
            else
                try
                    let document =
                        FileJson.read<CheckpointDocument> checkpointFile Unchecked.defaultof<CheckpointDocument>

                    if isNull (box document) || document.SchemaVersion <> 1 then
                        raise (InvalidDataException "Expected harness checkpoint schema version 1.")

                    document.Checkpoints
                with ex ->
                    raise (
                        InvalidDataException(
                            sprintf
                                "Harness checkpoints '%s' are invalid. Follow docs/migrations before writing."
                                checkpointFile,
                            ex
                        )
                    )

        let saveCheckpoints checkpoints =
            FileJson.write
                checkpointFile
                { SchemaVersion = 1
                  Checkpoints = checkpoints }

        let recordAsync (record: ExecutionRecord) =
            JournalOperations.requireOwner record.Owner
            task { lock sync (fun () -> save (Dto.toExecutionDto record :: load ())) } :> Task

        let getHistoryAsync () =
            task { return lock sync (fun () -> load () |> List.map Dto.ofExecutionDto) }

        let getByExecutionAsync executionId =
            task {
                return
                    lock sync (fun () ->
                        load ()
                        |> List.map Dto.ofExecutionDto
                        |> List.filter (fun record -> record.Correlation.ExecutionId = executionId))
            }

        let saveCheckpoint (checkpoint: HarnessCheckpoint) =
            JournalOperations.requireOwner checkpoint.Owner

            task {
                lock sync (fun () ->
                    checkpoint
                    |> HarnessCheckpointSerialization.toDto
                    |> fun dto -> loadCheckpoints () @ [ dto ]
                    |> saveCheckpoints)
            }
            :> Task

        let getCheckpoints executionId =
            task {
                return
                    lock sync (fun () ->
                        loadCheckpoints ()
                        |> List.map HarnessCheckpointSerialization.ofDto
                        |> List.filter (fun checkpoint -> checkpoint.Correlation.ExecutionId = executionId)
                        |> List.sortBy _.RecordedAt)
            }

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

        let delete (recordPredicate: ExecutionRecord -> bool) (checkpointPredicate: HarnessCheckpoint -> bool) =
            task {
                return
                    lock sync (fun () ->
                        let records = load () in

                        let retainedRecords =
                            records |> List.filter (Dto.ofExecutionDto >> recordPredicate >> not) in

                        let checkpoints = loadCheckpoints () in

                        let retainedCheckpoints =
                            checkpoints
                            |> List.filter (HarnessCheckpointSerialization.ofDto >> checkpointPredicate >> not) in

                        save retainedRecords
                        saveCheckpoints retainedCheckpoints

                        records.Length - retainedRecords.Length + checkpoints.Length
                        - retainedCheckpoints.Length)
            }

        let deleteOwnerAsync owner =
            JournalOperations.protect owner (fun () ->
                delete (fun record -> record.Owner = owner) (fun checkpoint -> checkpoint.Owner = owner))

        let deleteExpiredAsync owner before =
            JournalOperations.protect owner (fun () ->
                delete (fun record -> record.Owner = owner && record.ExecutedAt < before) (fun checkpoint ->
                    checkpoint.Owner = owner && checkpoint.RecordedAt < before))

        let checkpointJournal =
            { Save = saveCheckpoint
              GetByExecution = getCheckpoints }

        { RecordAsync = recordAsync
          GetHistoryAsync = getHistoryAsync
          GetByExecutionAsync = getByExecutionAsync
          GetRevertibleAsync = getRevertibleAsync
          MarkRevertedAsync = markRevertedAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync
          Checkpoints = checkpointJournal }

module ExecutionJournals =
    let ado factory : ExecutionJournal = AdoExecutionJournal.create factory
    let file baseDir : ExecutionJournal = FileExecutionJournal.create baseDir
