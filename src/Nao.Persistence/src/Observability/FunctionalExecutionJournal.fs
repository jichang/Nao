namespace Nao.Persistence

open System
open System.IO
open System.Threading.Tasks
open Nao.Agents

module private JournalRedaction =
    let private redact = SecretRedaction.redact

    let failure (failure: ToolFailure) =
        { failure with
            Message = redact failure.Message }

    let record (record: ExecutionRecord) =
        { record with
            ToolName = redact record.ToolName
            Input = redact record.Input
            Output = redact record.Output
            Failure = record.Failure |> Option.map failure
            Metadata = SecretRedaction.redactMap record.Metadata }

    let transition (transition: ToolSideEffectTransition) =
        { transition with
            ToolName = redact transition.ToolName
            Input = redact transition.Input
            Detail = transition.Detail |> SecretRedaction.redactOption }

    let outcome (outcome: HarnessTerminalOutcome) =
        { outcome with
            Response = outcome.Response |> SecretRedaction.redactOption
            Failure = outcome.Failure |> SecretRedaction.redactOption }

module private ExecutionJournalState =
    let create () : ExecutionJournal =
        let records =
            System.Collections.Concurrent.ConcurrentDictionary<string * Guid, ExecutionRecord>()

        let checkpoints =
            System.Collections.Concurrent.ConcurrentDictionary<string * Guid, HarnessCheckpoint>()

        let sideEffects =
            System.Collections.Concurrent.ConcurrentDictionary<string * Guid, ToolSideEffectTransition>()

        let outcomes =
            System.Collections.Concurrent.ConcurrentDictionary<string * Guid, HarnessTerminalOutcome>()

        let ownerKey = AuthorizationScope.identity >> AuthorizationIdentity.storageKey

        let owned
            scope
            (values: System.Collections.Concurrent.ConcurrentDictionary<string * Guid, 'Value>)
            : seq<'Value> =
            let owner = ownerKey scope

            values
            |> Seq.choose (fun pair -> if fst (pair.Key) = owner then Some pair.Value else None)

        let delete scope before =
            task {
                let owner = ownerKey scope
                let mutable deleted = 0

                let removeMatching
                    (items: System.Collections.Concurrent.ConcurrentDictionary<string * Guid, 'Value>)
                    timestamp
                    =
                    for pair in items do
                        if
                            fst (pair.Key) = owner
                            && (before |> Option.forall (fun cutoff -> timestamp pair.Value < cutoff))
                        then
                            match items.TryRemove pair.Key with
                            | true, _ -> deleted <- deleted + 1
                            | false, _ -> ()

                removeMatching records (fun (record: ExecutionRecord) -> record.ExecutedAt)
                removeMatching checkpoints (fun (checkpoint: HarnessCheckpoint) -> checkpoint.RecordedAt)
                removeMatching sideEffects (fun (transition: ToolSideEffectTransition) -> transition.RecordedAt)
                removeMatching outcomes (fun (outcome: HarnessTerminalOutcome) -> outcome.RecordedAt)
                return Ok deleted
            }

        let sideEffectJournal: ToolSideEffectJournal =
            { Save =
                fun scope transition ->
                    let transition = JournalRedaction.transition transition
                    sideEffects.[(ownerKey scope, transition.Id)] <- transition
                    Task.CompletedTask
              GetByIdempotencyKey =
                fun scope idempotencyKey ->
                    owned scope sideEffects
                    |> Seq.filter (fun transition -> transition.IdempotencyKey = idempotencyKey)
                    |> Seq.sortBy _.RecordedAt
                    |> Seq.toList
                    |> Task.FromResult
              GetByExecution =
                fun scope executionId ->
                    owned scope sideEffects
                    |> Seq.filter (fun transition -> transition.Correlation.ExecutionId = executionId)
                    |> Seq.sortBy _.RecordedAt
                    |> Seq.toList
                    |> Task.FromResult }

        let checkpointJournal: HarnessCheckpointJournal =
            { Save =
                fun scope checkpoint ->
                    checkpoints.[(ownerKey scope, checkpoint.Id)] <- checkpoint
                    Task.CompletedTask
              GetByExecution =
                fun scope executionId ->
                    owned scope checkpoints
                    |> Seq.filter (fun checkpoint -> checkpoint.Correlation.ExecutionId = executionId)
                    |> Seq.sortBy _.RecordedAt
                    |> Seq.toList
                    |> Task.FromResult
              GetByIdempotencyKey =
                fun scope idempotencyKey ->
                    owned scope checkpoints
                    |> Seq.filter (fun checkpoint -> checkpoint.IdempotencyKey = idempotencyKey)
                    |> Seq.sortBy _.RecordedAt
                    |> Seq.toList
                    |> Task.FromResult }

        let outcomeJournal: HarnessOutcomeJournal =
            { Save =
                fun scope outcome ->
                    let outcome = JournalRedaction.outcome outcome
                    let owner = ownerKey scope

                    let equivalent (existing: HarnessTerminalOutcome) =
                        existing.IdempotencyKey = outcome.IdempotencyKey
                        && existing.TurnId = outcome.TurnId
                        && existing.AgentId = outcome.AgentId
                        && existing.Phase = outcome.Phase
                        && existing.Response = outcome.Response
                        && existing.Artifacts = outcome.Artifacts
                        && existing.Failure = outcome.Failure

                    let existing =
                        outcomes
                        |> Seq.tryFind (fun pair ->
                            fst pair.Key = owner && pair.Value.IdempotencyKey = outcome.IdempotencyKey)

                    match existing with
                    | Some pair when not (equivalent pair.Value) ->
                        raise (
                            InvalidOperationException
                                "A different terminal outcome already exists for this execution idempotency key."
                        )
                    | Some _ -> Task.CompletedTask
                    | None ->
                        outcomes.[(owner, outcome.Id)] <- outcome
                        Task.CompletedTask
              TryGetByIdempotencyKey =
                fun scope idempotencyKey ->
                    owned scope outcomes
                    |> Seq.filter (fun outcome -> outcome.IdempotencyKey = idempotencyKey)
                    |> Seq.sortByDescending _.RecordedAt
                    |> Seq.tryHead
                    |> Task.FromResult }

        { RecordAsync =
            fun scope record ->
                let record = JournalRedaction.record record
                records.[(ownerKey scope, record.Id)] <- record
                Task.CompletedTask
          GetHistoryAsync =
            fun scope ->
                owned scope records
                |> Seq.sortByDescending _.ExecutedAt
                |> Seq.toList
                |> Task.FromResult
          GetByExecutionAsync =
            fun scope executionId ->
                owned scope records
                |> Seq.filter (fun record -> record.Correlation.ExecutionId = executionId)
                |> Seq.sortByDescending _.ExecutedAt
                |> Seq.toList
                |> Task.FromResult
          GetRevertibleAsync =
            fun scope ->
                let quarantinedKeys =
                    owned scope sideEffects
                    |> Seq.filter (fun transition -> transition.Phase = ToolSideEffectPhase.Quarantined)
                    |> Seq.map _.IdempotencyKey
                    |> Set.ofSeq

                owned scope records
                |> Seq.filter (fun record ->
                    not record.Reverted
                    && record.Failure.IsNone
                    && not (quarantinedKeys |> Set.contains record.IdempotencyKey))
                |> Seq.sortByDescending _.ExecutedAt
                |> Seq.toList
                |> Task.FromResult
          MarkRevertedAsync =
            fun scope recordId ->
                let key = ownerKey scope, recordId

                match records.TryGetValue key with
                | true, record ->
                    records.[key] <- { record with Reverted = true }
                    Task.CompletedTask
                | false, _ ->
                    raise (
                        UnauthorizedAccessException
                            "The execution record does not belong to the supplied authorization scope."
                    )
          DeleteScopeAsync = fun scope -> delete scope None
          DeleteExpiredAsync = fun scope before -> delete scope (Some before)
          SideEffects = sideEffectJournal
          Checkpoints = checkpointJournal
          Outcomes = outcomeJournal }

module InMemoryExecutionJournal =
    let create () : ExecutionJournal = ExecutionJournalState.create ()

[<RequireQualifiedAccess>]
type ExecutionJournalEvent =
    | RecordAccepted of ExecutionRecord
    | RecordReverted of Guid
    | CheckpointAccepted of HarnessCheckpoint
    | SideEffectAccepted of ToolSideEffectTransition
    | OutcomeAccepted of HarnessTerminalOutcome
    | DeleteScope
    | DeleteExpired of DateTimeOffset

type ExecutionJournalDocument =
    { Version: int
      Sequence: int64
      Event: ExecutionJournalEvent }

module PersistentExecutionJournal =
    let private decode context eventNumber line =
        try
            let document = FSharpJson.deserialize<ExecutionJournalDocument> line

            if
                isNull (box document)
                || document.Version <> 1
                || document.Sequence <> int64 eventNumber
            then
                raise (System.Text.Json.JsonException("Expected execution-journal schema version 1."))

            document.Event
        with ex ->
            raise (
                InvalidDataException(
                    sprintf
                        "Execution-journal stream '%s' is invalid at event %d. Follow docs/migrations before writing."
                        context
                        eventNumber,
                    ex
                )
            )

    let create (openStore: string -> EventStore) : ExecutionJournal =
        let sync = obj ()

        let load scope =
            let key = AuthorizationScope.identity scope |> AuthorizationIdentity.storageKey
            let context = "execution-journal/" + key
            let store = openStore key
            let inner = InMemoryExecutionJournal.create ()
            let lines = store.LoadAll()

            lines
            |> List.iteri (fun index line ->
                let eventNumber = index + 1

                try
                    match decode context eventNumber line with
                    | ExecutionJournalEvent.RecordAccepted record ->
                        inner.RecordAsync scope record |> _.GetAwaiter().GetResult()
                    | ExecutionJournalEvent.RecordReverted recordId ->
                        inner.MarkRevertedAsync scope recordId |> _.GetAwaiter().GetResult()
                    | ExecutionJournalEvent.CheckpointAccepted checkpoint ->
                        inner.Checkpoints.Save scope checkpoint |> _.GetAwaiter().GetResult()
                    | ExecutionJournalEvent.SideEffectAccepted transition ->
                        inner.SideEffects.Save scope transition |> _.GetAwaiter().GetResult()
                    | ExecutionJournalEvent.OutcomeAccepted outcome ->
                        inner.Outcomes.Save scope outcome |> _.GetAwaiter().GetResult()
                    | ExecutionJournalEvent.DeleteScope ->
                        inner.DeleteScopeAsync scope |> _.GetAwaiter().GetResult() |> ignore
                    | ExecutionJournalEvent.DeleteExpired before ->
                        inner.DeleteExpiredAsync scope before |> _.GetAwaiter().GetResult() |> ignore
                with
                | :? InvalidDataException -> reraise ()
                | ex ->
                    raise (
                        InvalidDataException(
                            sprintf
                                "Execution-journal stream '%s' is invalid at event %d. Follow docs/migrations before writing."
                                context
                                eventNumber,
                            ex
                        )
                    ))

            store, inner, int64 lines.Length

        let read scope operation =
            lock sync (fun () ->
                let _, inner, _ = load scope
                operation inner)

        let mutate scope operation event =
            lock sync (fun () ->
                let store, inner, sequence = load scope
                let result = operation inner

                store.Append(
                    FSharpJson.serialize
                        { Version = 1
                          Sequence = sequence + 1L
                          Event = event }
                )

                result)

        let protect operation =
            task {
                try
                    return Ok(operation ())
                with ex ->
                    return Error(PlatformFailure.fromException PlatformFailureBoundary.Storage None ex)
            }

        let sideEffectJournal: ToolSideEffectJournal =
            { Save =
                fun scope transition ->
                    let transition = JournalRedaction.transition transition

                    task {
                        mutate
                            scope
                            (fun inner -> inner.SideEffects.Save scope transition |> _.GetAwaiter().GetResult())
                            (ExecutionJournalEvent.SideEffectAccepted transition)
                    }
                    :> Task
              GetByIdempotencyKey =
                fun scope idempotencyKey ->
                    read scope (fun inner -> inner.SideEffects.GetByIdempotencyKey scope idempotencyKey)
              GetByExecution =
                fun scope executionId -> read scope (fun inner -> inner.SideEffects.GetByExecution scope executionId) }

        let checkpointJournal: HarnessCheckpointJournal =
            { Save =
                fun scope checkpoint ->
                    task {
                        mutate
                            scope
                            (fun inner -> inner.Checkpoints.Save scope checkpoint |> _.GetAwaiter().GetResult())
                            (ExecutionJournalEvent.CheckpointAccepted checkpoint)
                    }
                    :> Task
              GetByExecution =
                fun scope executionId -> read scope (fun inner -> inner.Checkpoints.GetByExecution scope executionId)
              GetByIdempotencyKey =
                fun scope idempotencyKey ->
                    read scope (fun inner -> inner.Checkpoints.GetByIdempotencyKey scope idempotencyKey) }

        let outcomeJournal: HarnessOutcomeJournal =
            { Save =
                fun scope outcome ->
                    let outcome = JournalRedaction.outcome outcome

                    task {
                        mutate
                            scope
                            (fun inner -> inner.Outcomes.Save scope outcome |> _.GetAwaiter().GetResult())
                            (ExecutionJournalEvent.OutcomeAccepted outcome)
                    }
                    :> Task
              TryGetByIdempotencyKey =
                fun scope idempotencyKey ->
                    read scope (fun inner -> inner.Outcomes.TryGetByIdempotencyKey scope idempotencyKey) }

        { RecordAsync =
            fun scope record ->
                let record = JournalRedaction.record record

                task {
                    mutate
                        scope
                        (fun inner -> inner.RecordAsync scope record |> _.GetAwaiter().GetResult())
                        (ExecutionJournalEvent.RecordAccepted record)
                }
                :> Task
          GetHistoryAsync = fun scope -> read scope (fun inner -> inner.GetHistoryAsync scope)
          GetByExecutionAsync =
            fun scope executionId -> read scope (fun inner -> inner.GetByExecutionAsync scope executionId)
          GetRevertibleAsync = fun scope -> read scope (fun inner -> inner.GetRevertibleAsync scope)
          MarkRevertedAsync =
            fun scope recordId ->
                task {
                    mutate
                        scope
                        (fun inner -> inner.MarkRevertedAsync scope recordId |> _.GetAwaiter().GetResult())
                        (ExecutionJournalEvent.RecordReverted recordId)
                }
                :> Task
          DeleteScopeAsync =
            fun scope ->
                protect (fun () ->
                    mutate
                        scope
                        (fun inner ->
                            inner.DeleteScopeAsync scope
                            |> _.GetAwaiter().GetResult()
                            |> Result.defaultWith (fun failure -> raise (InvalidOperationException failure.Message)))
                        ExecutionJournalEvent.DeleteScope)
          DeleteExpiredAsync =
            fun scope before ->
                protect (fun () ->
                    mutate
                        scope
                        (fun inner ->
                            inner.DeleteExpiredAsync scope before
                            |> _.GetAwaiter().GetResult()
                            |> Result.defaultWith (fun failure -> raise (InvalidOperationException failure.Message)))
                        (ExecutionJournalEvent.DeleteExpired before))
          SideEffects = sideEffectJournal
          Checkpoints = checkpointJournal
          Outcomes = outcomeJournal }

module ExecutionJournals =
    let ado factory : ExecutionJournal =
        PersistentExecutionJournal.create (fun key -> EventStore.db factory ("execution-journal/" + key))

    let file baseDir : ExecutionJournal =
        PersistentExecutionJournal.create (fun key ->
            EventStore.file (Path.Combine(baseDir, "execution-journal", key + ".jsonl")))
