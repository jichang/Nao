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

    let create
        recordAsync
        getHistory
        getByExecution
        getRevertible
        markReverted
        deleteOwner
        deleteExpired
        sideEffects
        checkpoints
        : ExecutionJournal =
        { RecordAsync = recordAsync
          GetHistoryAsync = getHistory
          GetByExecutionAsync = getByExecution
          GetRevertibleAsync = getRevertible
          MarkRevertedAsync = markReverted
          DeleteOwnerAsync = deleteOwner
          DeleteExpiredAsync = deleteExpired
          SideEffects = sideEffects
          Checkpoints = checkpoints }

[<RequireQualifiedAccess>]
module ToolSideEffectSerialization =
    [<CLIMutable>]
    type Dto =
        { Id: Guid
          IdempotencyKey: string
          ExecutionId: string
          CorrelationId: string
          CausationId: string
          Attempt: int
          Owner: string
          TurnId: string
          ToolName: string
          Input: string
          Phase: string
          Detail: string
          RecordedAt: DateTimeOffset }

    [<CLIMutable>]
    type Document =
        { SchemaVersion: int
          Transitions: Dto list }

    let private phaseToString =
        function
        | ToolSideEffectPhase.IntentRecorded -> "intent-recorded"
        | ToolSideEffectPhase.Succeeded -> "succeeded"
        | ToolSideEffectPhase.Failed -> "failed"
        | ToolSideEffectPhase.Interrupted -> "interrupted"
        | ToolSideEffectPhase.CompensationSucceeded -> "compensation-succeeded"
        | ToolSideEffectPhase.CompensationFailed -> "compensation-failed"

    let private phaseFromString =
        function
        | "intent-recorded" -> ToolSideEffectPhase.IntentRecorded
        | "succeeded" -> ToolSideEffectPhase.Succeeded
        | "failed" -> ToolSideEffectPhase.Failed
        | "interrupted" -> ToolSideEffectPhase.Interrupted
        | "compensation-succeeded" -> ToolSideEffectPhase.CompensationSucceeded
        | "compensation-failed" -> ToolSideEffectPhase.CompensationFailed
        | value -> invalidArg (nameof value) "Invalid tool side-effect phase."

    let toDto (transition: ToolSideEffectTransition) : Dto =
        let causationId =
            transition.Correlation.CausationId
            |> Option.map ExecutionId.serialize
            |> Option.defaultValue null

        { Id = transition.Id
          IdempotencyKey = IdempotencyKey.value transition.IdempotencyKey
          ExecutionId = ExecutionId.serialize transition.Correlation.ExecutionId
          CorrelationId = CorrelationId.serialize transition.Correlation.CorrelationId
          CausationId = causationId
          Attempt = transition.Correlation.Attempt
          Owner = transition.Owner
          TurnId = transition.TurnId
          ToolName = transition.ToolName
          Input = transition.Input
          Phase = phaseToString transition.Phase
          Detail = transition.Detail |> Option.defaultValue null
          RecordedAt = transition.RecordedAt }

    let ofDto (dto: Dto) : ToolSideEffectTransition =
        let correlation =
            { ExecutionId = ExecutionId.parse dto.ExecutionId
              CorrelationId = CorrelationId.parse dto.CorrelationId
              CausationId = Option.ofObj dto.CausationId |> Option.map ExecutionId.parse
              Attempt = dto.Attempt }

        { Id = dto.Id
          IdempotencyKey = IdempotencyKey.parse dto.IdempotencyKey
          Correlation = correlation
          Owner = dto.Owner
          TurnId = dto.TurnId
          ToolName = dto.ToolName
          Input = dto.Input
          Phase = phaseFromString dto.Phase
          Detail = Option.ofObj dto.Detail
          RecordedAt = dto.RecordedAt }

module InMemoryExecutionJournal =
    let create () : ExecutionJournal =
        let entries = System.Collections.Generic.List<ExecutionRecord>()
        let checkpoints = System.Collections.Generic.List<HarnessCheckpoint>()
        let sideEffects = System.Collections.Generic.List<ToolSideEffectTransition>()

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

        let saveSideEffect (transition: ToolSideEffectTransition) =
            JournalOperations.requireOwner transition.Owner
            lock sideEffects (fun () -> sideEffects.Add transition)
            Task.CompletedTask

        let getSideEffects (idempotencyKey: IdempotencyKey) =
            lock sideEffects (fun () ->
                sideEffects
                |> Seq.filter (fun transition -> transition.IdempotencyKey = idempotencyKey)
                |> Seq.sortBy _.RecordedAt
                |> Seq.toList)
            |> Task.FromResult

        let getSideEffectsByExecution executionId =
            lock sideEffects (fun () ->
                sideEffects
                |> Seq.filter (fun transition -> transition.Correlation.ExecutionId = executionId)
                |> Seq.sortBy _.RecordedAt
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

        let getCheckpointsByIdempotencyKey idempotencyKey =
            lock checkpoints (fun () ->
                checkpoints
                |> Seq.filter (fun checkpoint -> checkpoint.IdempotencyKey = idempotencyKey)
                |> Seq.sortBy _.RecordedAt
                |> Seq.toList)
            |> Task.FromResult

        let getRevertibleAsync () =
            lock entries (fun () ->
                entries
                |> Seq.filter (fun entry -> not entry.Reverted && entry.Failure.IsNone)
                |> Seq.toList)
            |> Task.FromResult

        let markRevertedAsync (recordId: Guid) =
            lock entries (fun () ->
                match entries |> Seq.tryFindIndex (fun entry -> entry.Id = recordId) with
                | Some index -> entries.[index] <- { entries.[index] with Reverted = true }
                | None -> ())

            Task.CompletedTask

        let delete
            (recordPredicate: ExecutionRecord -> bool)
            (checkpointPredicate: HarnessCheckpoint -> bool)
            (sideEffectPredicate: ToolSideEffectTransition -> bool)
            =
            let recordsDeleted =
                lock entries (fun () -> entries.RemoveAll(fun record -> recordPredicate record))

            let checkpointsDeleted =
                lock checkpoints (fun () -> checkpoints.RemoveAll(fun checkpoint -> checkpointPredicate checkpoint))

            let sideEffectsDeleted =
                lock sideEffects (fun () -> sideEffects.RemoveAll(fun transition -> sideEffectPredicate transition))

            Task.FromResult(recordsDeleted + checkpointsDeleted + sideEffectsDeleted)

        let deleteOwnerAsync owner =
            JournalOperations.protect owner (fun () ->
                delete
                    (fun record -> record.Owner = owner)
                    (fun checkpoint -> checkpoint.Owner = owner)
                    (fun transition -> transition.Owner = owner))

        let deleteExpiredAsync owner before =
            JournalOperations.protect owner (fun () ->
                delete
                    (fun record -> record.Owner = owner && record.ExecutedAt < before)
                    (fun checkpoint -> checkpoint.Owner = owner && checkpoint.RecordedAt < before)
                    (fun transition -> transition.Owner = owner && transition.RecordedAt < before))

        let checkpointJournal: HarnessCheckpointJournal =
            { Save = saveCheckpoint
              GetByExecution = getCheckpoints
              GetByIdempotencyKey = getCheckpointsByIdempotencyKey }

        let sideEffectJournal: ToolSideEffectJournal =
            { Save = saveSideEffect
              GetByIdempotencyKey = getSideEffects
              GetByExecution = getSideEffectsByExecution }

        { RecordAsync = recordAsync
          GetHistoryAsync = getHistoryAsync
          GetByExecutionAsync = getByExecutionAsync
          GetRevertibleAsync = getRevertibleAsync
          MarkRevertedAsync = markRevertedAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync
          SideEffects = sideEffectJournal
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
                        "CREATE TABLE IF NOT EXISTS nao_execution_journal (record_id TEXT NOT NULL PRIMARY KEY, idempotency_key TEXT NOT NULL, execution_id TEXT NOT NULL, correlation_id TEXT NOT NULL, causation_id TEXT NULL, attempt INTEGER NOT NULL, owner TEXT NOT NULL, turn_id TEXT NOT NULL, tool_name TEXT NOT NULL, replay_safety TEXT NOT NULL, tool_input TEXT NOT NULL, tool_output TEXT NOT NULL, failure_kind TEXT NULL, failure_message TEXT NULL, failure_retryable INTEGER NULL, executed_at TEXT NOT NULL, reverted INTEGER NOT NULL, metadata TEXT NOT NULL)"

                do!
                    AdoSchema.ensureVersionedTable
                        factory
                        "harness-checkpoints"
                        "nao_harness_checkpoints"
                        "CREATE TABLE IF NOT EXISTS nao_harness_checkpoints (checkpoint_id TEXT NOT NULL PRIMARY KEY, idempotency_key TEXT NOT NULL, execution_id TEXT NOT NULL, correlation_id TEXT NOT NULL, causation_id TEXT NULL, attempt INTEGER NOT NULL, owner TEXT NOT NULL, turn_id TEXT NOT NULL, agent_id TEXT NOT NULL, phase TEXT NOT NULL, recorded_at TEXT NOT NULL)"

                do!
                    AdoSchema.ensureVersionedTable
                        factory
                        "tool-side-effects"
                        "nao_tool_side_effects"
                        "CREATE TABLE IF NOT EXISTS nao_tool_side_effects (transition_id TEXT NOT NULL PRIMARY KEY, idempotency_key TEXT NOT NULL, execution_id TEXT NOT NULL, correlation_id TEXT NOT NULL, causation_id TEXT NULL, attempt INTEGER NOT NULL, owner TEXT NOT NULL, turn_id TEXT NOT NULL, tool_name TEXT NOT NULL, tool_input TEXT NOT NULL, phase TEXT NOT NULL, detail TEXT NULL, recorded_at TEXT NOT NULL)"

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

                let! _ =
                    Ado.executeNonQuery
                        factory
                        "CREATE INDEX IF NOT EXISTS nao_harness_checkpoints_key_time ON nao_harness_checkpoints (idempotency_key, recorded_at)"
                        []

                let! _ =
                    Ado.executeNonQuery
                        factory
                        "CREATE INDEX IF NOT EXISTS nao_tool_side_effects_key_time ON nao_tool_side_effects (idempotency_key, recorded_at)"
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

                let idempotencyKey = IdempotencyKey.parse (Ado.getString reader "idempotency_key")

                let failure =
                    match
                        Ado.getStringOpt reader "failure_kind",
                        Ado.getStringOpt reader "failure_message",
                        reader.IsDBNull(reader.GetOrdinal "failure_retryable")
                    with
                    | None, None, true -> None
                    | Some kind, Some message, false ->
                        Some
                            { Kind = ToolFailureKind.parse kind
                              Message = message
                              Retryable = Ado.getBool reader "failure_retryable" }
                    | _ -> invalidArg "failure_kind" "Incomplete tool failure row."

                { Id = Guid.Parse id
                  IdempotencyKey = idempotencyKey
                  Correlation = correlation
                  Owner = Ado.getString reader "owner"
                  TurnId = Ado.getString reader "turn_id"
                  ToolName = Ado.getString reader "tool_name"
                  ReplaySafety = ToolReplaySafety.parse (Ado.getString reader "replay_safety")
                  Input = Ado.getString reader "tool_input"
                  Output = Ado.getString reader "tool_output"
                  Failure = failure
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
                        "SELECT record_id, idempotency_key, execution_id, correlation_id, causation_id, attempt, owner, turn_id, tool_name, replay_safety, tool_input, tool_output, failure_kind, failure_message, failure_retryable, executed_at, reverted, metadata FROM nao_execution_journal"
                        []
                        mapRecord

                return ()
            }

        let mapCheckpoint (reader: DbDataReader) : HarnessCheckpoint =
            let id = Ado.getString reader "checkpoint_id"

            try
                HarnessCheckpointSerialization.ofDto
                    { Id = Guid.Parse id
                      IdempotencyKey = Ado.getString reader "idempotency_key"
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

        let validateCheckpointsAsync () =
            task {
                let! _ =
                    Ado.query
                        factory
                        "SELECT checkpoint_id, idempotency_key, execution_id, correlation_id, causation_id, attempt, owner, turn_id, agent_id, phase, recorded_at FROM nao_harness_checkpoints"
                        []
                        mapCheckpoint

                return ()
            }

        let saveCheckpoint (checkpoint: HarnessCheckpoint) =
            task {
                JournalOperations.requireOwner checkpoint.Owner
                do! ensureAsync ()
                do! validateCheckpointsAsync ()
                let dto = HarnessCheckpointSerialization.toDto checkpoint

                let causationId =
                    Option.ofObj dto.CausationId |> Option.map box |> Option.defaultValue null

                let! _ =
                    Ado.executeNonQuery
                        factory
                        "INSERT INTO nao_harness_checkpoints (checkpoint_id, idempotency_key, execution_id, correlation_id, causation_id, attempt, owner, turn_id, agent_id, phase, recorded_at) VALUES (@id, @ik, @ex, @co, @ca, @at, @ow, @tr, @ag, @ph, @ra)"
                        [ "@id", box (dto.Id.ToString("D"))
                          "@ik", box dto.IdempotencyKey
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
                        "SELECT checkpoint_id, idempotency_key, execution_id, correlation_id, causation_id, attempt, owner, turn_id, agent_id, phase, recorded_at FROM nao_harness_checkpoints WHERE execution_id = @ex ORDER BY recorded_at ASC"
                        [ "@ex", box (ExecutionId.serialize executionId) ]
                        mapCheckpoint
            }

        let getCheckpointsByIdempotencyKey idempotencyKey =
            task {
                do! ensureAsync ()

                return!
                    Ado.query
                        factory
                        "SELECT checkpoint_id, idempotency_key, execution_id, correlation_id, causation_id, attempt, owner, turn_id, agent_id, phase, recorded_at FROM nao_harness_checkpoints WHERE idempotency_key = @ik ORDER BY recorded_at ASC"
                        [ "@ik", box (IdempotencyKey.value idempotencyKey) ]
                        mapCheckpoint
            }

        let mapSideEffect (reader: DbDataReader) =
            ToolSideEffectSerialization.ofDto
                { Id = Guid.Parse(Ado.getString reader "transition_id")
                  IdempotencyKey = Ado.getString reader "idempotency_key"
                  ExecutionId = Ado.getString reader "execution_id"
                  CorrelationId = Ado.getString reader "correlation_id"
                  CausationId = Ado.getStringOpt reader "causation_id" |> Option.defaultValue null
                  Attempt = Convert.ToInt32(reader.["attempt"])
                  Owner = Ado.getString reader "owner"
                  TurnId = Ado.getString reader "turn_id"
                  ToolName = Ado.getString reader "tool_name"
                  Input = Ado.getString reader "tool_input"
                  Phase = Ado.getString reader "phase"
                  Detail = Ado.getStringOpt reader "detail" |> Option.defaultValue null
                  RecordedAt = Time.fromIso (Ado.getString reader "recorded_at") }

        let validateSideEffectsAsync () =
            task {
                let! _ =
                    Ado.query
                        factory
                        "SELECT transition_id, idempotency_key, execution_id, correlation_id, causation_id, attempt, owner, turn_id, tool_name, tool_input, phase, detail, recorded_at FROM nao_tool_side_effects"
                        []
                        mapSideEffect

                return ()
            }

        let saveSideEffect (transition: ToolSideEffectTransition) =
            task {
                JournalOperations.requireOwner transition.Owner
                do! ensureAsync ()
                do! validateSideEffectsAsync ()
                let dto = ToolSideEffectSerialization.toDto transition

                let causationId =
                    Option.ofObj dto.CausationId |> Option.map box |> Option.defaultValue null

                let detail = Option.ofObj dto.Detail |> Option.map box |> Option.defaultValue null

                let! _ =
                    Ado.executeNonQuery
                        factory
                        "INSERT INTO nao_tool_side_effects (transition_id, idempotency_key, execution_id, correlation_id, causation_id, attempt, owner, turn_id, tool_name, tool_input, phase, detail, recorded_at) VALUES (@id, @ik, @ex, @co, @ca, @at, @ow, @tr, @tn, @ti, @ph, @de, @ra)"
                        [ "@id", box (dto.Id.ToString("D"))
                          "@ik", box dto.IdempotencyKey
                          "@ex", box dto.ExecutionId
                          "@co", box dto.CorrelationId
                          "@ca", causationId
                          "@at", box dto.Attempt
                          "@ow", box dto.Owner
                          "@tr", box dto.TurnId
                          "@tn", box dto.ToolName
                          "@ti", box dto.Input
                          "@ph", box dto.Phase
                          "@de", detail
                          "@ra", box (Time.toIso dto.RecordedAt) ]

                return ()
            }
            :> Task

        let getSideEffects idempotencyKey =
            task {
                do! ensureAsync ()

                return!
                    Ado.query
                        factory
                        "SELECT transition_id, idempotency_key, execution_id, correlation_id, causation_id, attempt, owner, turn_id, tool_name, tool_input, phase, detail, recorded_at FROM nao_tool_side_effects WHERE idempotency_key = @ik ORDER BY recorded_at ASC"
                        [ "@ik", box (IdempotencyKey.value idempotencyKey) ]
                        mapSideEffect
            }

        let getSideEffectsByExecution executionId =
            task {
                do! ensureAsync ()

                return!
                    Ado.query
                        factory
                        "SELECT transition_id, idempotency_key, execution_id, correlation_id, causation_id, attempt, owner, turn_id, tool_name, tool_input, phase, detail, recorded_at FROM nao_tool_side_effects WHERE execution_id = @ex ORDER BY recorded_at ASC"
                        [ "@ex", box (ExecutionId.serialize executionId) ]
                        mapSideEffect
            }

        let recordAsync (record: ExecutionRecord) =
            task {
                JournalOperations.requireOwner record.Owner
                do! validateAsync ()

                let causationId =
                    record.Correlation.CausationId
                    |> Option.map (ExecutionId.serialize >> box)
                    |> Option.defaultValue null

                let idempotencyParameter = "@ik", box (IdempotencyKey.value record.IdempotencyKey)

                let failureKind, failureMessage, failureRetryable =
                    match record.Failure with
                    | Some failure ->
                        box (ToolFailureKind.serialize failure.Kind),
                        box failure.Message,
                        Ado.boolValue failure.Retryable
                    | None -> null, null, null

                let parameters =
                    idempotencyParameter
                    :: [ "@id", box (record.Id.ToString("D"))
                         "@ex", box (ExecutionId.serialize record.Correlation.ExecutionId)
                         "@co", box (CorrelationId.serialize record.Correlation.CorrelationId)
                         "@ca", causationId
                         "@at", box record.Correlation.Attempt
                         "@ow", box record.Owner
                         "@tr", box record.TurnId
                         "@tn", box record.ToolName
                         "@rs", box (ToolReplaySafety.serialize record.ReplaySafety)
                         "@ti", box record.Input
                         "@to", box record.Output
                         "@fk", failureKind
                         "@fm", failureMessage
                         "@fr", failureRetryable
                         "@ea", box (Time.toIso record.ExecutedAt)
                         "@rv", Ado.boolValue record.Reverted
                         "@md", box (Json.mapToJson record.Metadata) ]

                let! _ =
                    Ado.executeNonQuery
                        factory
                        "INSERT INTO nao_execution_journal (record_id, idempotency_key, execution_id, correlation_id, causation_id, attempt, owner, turn_id, tool_name, replay_safety, tool_input, tool_output, failure_kind, failure_message, failure_retryable, executed_at, reverted, metadata) VALUES (@id, @ik, @ex, @co, @ca, @at, @ow, @tr, @tn, @rs, @ti, @to, @fk, @fm, @fr, @ea, @rv, @md)"
                        parameters

                return ()
            }
            :> Task

        let getHistoryAsync () =
            task {
                do! ensureAsync ()

                return!
                    Ado.query
                        factory
                        "SELECT record_id, idempotency_key, execution_id, correlation_id, causation_id, attempt, owner, turn_id, tool_name, replay_safety, tool_input, tool_output, failure_kind, failure_message, failure_retryable, executed_at, reverted, metadata FROM nao_execution_journal ORDER BY executed_at DESC"
                        []
                        mapRecord
            }

        let getByExecutionAsync executionId =
            task {
                do! ensureAsync ()

                return!
                    Ado.query
                        factory
                        "SELECT record_id, idempotency_key, execution_id, correlation_id, causation_id, attempt, owner, turn_id, tool_name, replay_safety, tool_input, tool_output, failure_kind, failure_message, failure_retryable, executed_at, reverted, metadata FROM nao_execution_journal WHERE execution_id = @ex ORDER BY executed_at DESC"
                        [ "@ex", box (ExecutionId.serialize executionId) ]
                        mapRecord
            }

        let getRevertibleAsync () =
            task {
                do! ensureAsync ()

                return!
                    Ado.query
                        factory
                        "SELECT record_id, idempotency_key, execution_id, correlation_id, causation_id, attempt, owner, turn_id, tool_name, replay_safety, tool_input, tool_output, failure_kind, failure_message, failure_retryable, executed_at, reverted, metadata FROM nao_execution_journal WHERE reverted = 0 AND failure_kind IS NULL ORDER BY executed_at DESC"
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

                    let! sideEffectsDeleted =
                        Ado.executeNonQuery
                            factory
                            "DELETE FROM nao_tool_side_effects WHERE owner = @ow"
                            [ "@ow", box owner ]

                    return recordsDeleted + checkpointsDeleted + sideEffectsDeleted
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

                    let! sideEffectsDeleted =
                        Ado.executeNonQuery
                            factory
                            "DELETE FROM nao_tool_side_effects WHERE owner = @ow AND recorded_at < @before"
                            [ "@ow", box owner; "@before", box (Time.toIso before) ]

                    return recordsDeleted + checkpointsDeleted + sideEffectsDeleted
                })

        let checkpointJournal: HarnessCheckpointJournal =
            { Save = saveCheckpoint
              GetByExecution = getCheckpoints
              GetByIdempotencyKey = getCheckpointsByIdempotencyKey }

        let sideEffectJournal: ToolSideEffectJournal =
            { Save = saveSideEffect
              GetByIdempotencyKey = getSideEffects
              GetByExecution = getSideEffectsByExecution }

        { RecordAsync = recordAsync
          GetHistoryAsync = getHistoryAsync
          GetByExecutionAsync = getByExecutionAsync
          GetRevertibleAsync = getRevertibleAsync
          MarkRevertedAsync = markRevertedAsync
          DeleteOwnerAsync = deleteOwnerAsync
          DeleteExpiredAsync = deleteExpiredAsync
          SideEffects = sideEffectJournal
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
        let sideEffectFile = Path.Combine(baseDir, "tool-side-effects.json")

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
                    |> List.iter (HarnessCheckpointSerialization.ofDto >> ignore)

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

        let loadSideEffects () =
            if not (File.Exists sideEffectFile) then
                []
            else
                try
                    let document =
                        FileJson.read<ToolSideEffectSerialization.Document>
                            sideEffectFile
                            Unchecked.defaultof<ToolSideEffectSerialization.Document>

                    if isNull (box document) || document.SchemaVersion <> 1 then
                        raise (InvalidDataException "Expected tool side-effect schema version 1.")

                    document.Transitions |> List.iter (ToolSideEffectSerialization.ofDto >> ignore)

                    document.Transitions
                with ex ->
                    raise (
                        InvalidDataException(
                            sprintf
                                "Tool side effects '%s' are invalid. Follow docs/migrations before writing."
                                sideEffectFile,
                            ex
                        )
                    )

        let saveSideEffects transitions =
            let document: ToolSideEffectSerialization.Document =
                { SchemaVersion = 1
                  Transitions = transitions }

            FileJson.write sideEffectFile document

        let saveSideEffect (transition: ToolSideEffectTransition) =
            JournalOperations.requireOwner transition.Owner

            task {
                lock sync (fun () ->
                    saveSideEffects (loadSideEffects () @ [ ToolSideEffectSerialization.toDto transition ]))
            }
            :> Task

        let getSideEffects idempotencyKey =
            task {
                return
                    lock sync (fun () ->
                        loadSideEffects ()
                        |> List.map ToolSideEffectSerialization.ofDto
                        |> List.filter (fun transition -> transition.IdempotencyKey = idempotencyKey)
                        |> List.sortBy _.RecordedAt)
            }

        let getSideEffectsByExecution executionId =
            task {
                return
                    lock sync (fun () ->
                        loadSideEffects ()
                        |> List.map ToolSideEffectSerialization.ofDto
                        |> List.filter (fun transition -> transition.Correlation.ExecutionId = executionId)
                        |> List.sortBy _.RecordedAt)
            }

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

        let getCheckpointsByIdempotencyKey idempotencyKey =
            task {
                return
                    lock sync (fun () ->
                        loadCheckpoints ()
                        |> List.map HarnessCheckpointSerialization.ofDto
                        |> List.filter (fun checkpoint -> checkpoint.IdempotencyKey = idempotencyKey)
                        |> List.sortBy _.RecordedAt)
            }

        let getRevertibleAsync () =
            task {
                return
                    lock sync (fun () ->
                        load ()
                        |> List.map Dto.ofExecutionDto
                        |> List.filter (fun record -> not record.Reverted && record.Failure.IsNone))
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

        let delete
            (recordPredicate: ExecutionRecord -> bool)
            (checkpointPredicate: HarnessCheckpoint -> bool)
            (sideEffectPredicate: ToolSideEffectTransition -> bool)
            =
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

                        let sideEffects = loadSideEffects () in

                        let retainedSideEffects =
                            sideEffects
                            |> List.filter (ToolSideEffectSerialization.ofDto >> sideEffectPredicate >> not) in

                        save retainedRecords
                        saveCheckpoints retainedCheckpoints
                        saveSideEffects retainedSideEffects

                        records.Length - retainedRecords.Length + checkpoints.Length
                        - retainedCheckpoints.Length
                        + sideEffects.Length
                        - retainedSideEffects.Length)
            }

        let deleteOwnerAsync owner =
            JournalOperations.protect owner (fun () ->
                delete
                    (fun record -> record.Owner = owner)
                    (fun checkpoint -> checkpoint.Owner = owner)
                    (fun transition -> transition.Owner = owner))

        let deleteExpiredAsync owner before =
            JournalOperations.protect owner (fun () ->
                delete
                    (fun record -> record.Owner = owner && record.ExecutedAt < before)
                    (fun checkpoint -> checkpoint.Owner = owner && checkpoint.RecordedAt < before)
                    (fun transition -> transition.Owner = owner && transition.RecordedAt < before))

        let checkpointJournal: HarnessCheckpointJournal =
            { Save = saveCheckpoint
              GetByExecution = getCheckpoints
              GetByIdempotencyKey = getCheckpointsByIdempotencyKey }

        let sideEffectJournal: ToolSideEffectJournal =
            { Save = saveSideEffect
              GetByIdempotencyKey = getSideEffects
              GetByExecution = getSideEffectsByExecution }

        JournalOperations.create
            recordAsync
            getHistoryAsync
            getByExecutionAsync
            getRevertibleAsync
            markRevertedAsync
            deleteOwnerAsync
            deleteExpiredAsync
            sideEffectJournal
            checkpointJournal

module ExecutionJournals =
    let ado factory : ExecutionJournal = AdoExecutionJournal.create factory
    let file baseDir : ExecutionJournal = FileExecutionJournal.create baseDir
