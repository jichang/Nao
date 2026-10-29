namespace Nao.Platform

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

type MigrationPreflight =
    private
        { SourceRevision: string
          TargetRevision: string
          VerifiedBackupId: string
          VerifiedAt: DateTimeOffset
          ActiveWriters: int
          AvailableBytes: int64
          RequiredBytes: int64 }

[<RequireQualifiedAccess>]
module MigrationPreflight =
    let create sourceRevision targetRevision verifiedBackupId verifiedAt activeWriters availableBytes requiredBytes =
        for argumentName, value in
            [ nameof sourceRevision, sourceRevision
              nameof targetRevision, targetRevision
              nameof verifiedBackupId, verifiedBackupId ] do
            if String.IsNullOrWhiteSpace value || value <> value.Trim() then
                invalidArg argumentName "Migration preflight identities must be canonical and non-blank."

        if sourceRevision = targetRevision then
            invalidArg (nameof targetRevision) "Migration target revision must differ from its source."

        if activeWriters <> 0 then
            invalidArg (nameof activeWriters) "Migration preflight requires all incompatible writers to be fenced."

        if availableBytes < 0L || requiredBytes <= 0L || availableBytes < requiredBytes then
            invalidArg (nameof availableBytes) "Migration preflight requires sufficient positive storage capacity."

        { SourceRevision = sourceRevision
          TargetRevision = targetRevision
          VerifiedBackupId = verifiedBackupId
          VerifiedAt = verifiedAt
          ActiveWriters = activeWriters
          AvailableBytes = availableBytes
          RequiredBytes = requiredBytes }

    let sourceRevision value = value.SourceRevision
    let targetRevision value = value.TargetRevision
    let verifiedBackupId value = value.VerifiedBackupId
    let verifiedAt value = value.VerifiedAt

type MigrationStep =
    private
        { Id: string
          ApplyAsync: CancellationToken -> Task
          RollbackAsync: CancellationToken -> Task }

[<RequireQualifiedAccess>]
module MigrationStep =
    let create id applyAsync rollbackAsync =
        if String.IsNullOrWhiteSpace id || id <> id.Trim() then
            invalidArg (nameof id) "Migration step identities must be canonical and non-blank."

        if isNull (box applyAsync) then
            nullArg (nameof applyAsync)

        if isNull (box rollbackAsync) then
            nullArg (nameof rollbackAsync)

        { Id = id
          ApplyAsync = applyAsync
          RollbackAsync = rollbackAsync }

    let id value = value.Id
    let applyAsync cancellationToken value = value.ApplyAsync cancellationToken
    let rollbackAsync cancellationToken value = value.RollbackAsync cancellationToken

type MigrationPlan =
    private
        { Id: string
          Digest: string
          Preflight: MigrationPreflight
          Steps: MigrationStep list }

[<RequireQualifiedAccess>]
module MigrationPlan =
    let create id preflight steps =
        if String.IsNullOrWhiteSpace id || id <> id.Trim() then
            invalidArg (nameof id) "Migration identities must be canonical and non-blank."

        if isNull (box steps) || List.isEmpty steps then
            invalidArg (nameof steps) "Migration plans require at least one reversible step."

        let stepIds = steps |> List.map MigrationStep.id

        if stepIds.Length <> (stepIds |> List.distinct |> List.length) then
            invalidArg (nameof steps) "Migration step identities must be unique."

        let digestInput =
            String.concat
                "\n"
                ([ id
                   MigrationPreflight.sourceRevision preflight
                   MigrationPreflight.targetRevision preflight
                   MigrationPreflight.verifiedBackupId preflight ]
                 @ stepIds)

        let digest =
            digestInput
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData
            |> Convert.ToHexString
            |> _.ToLowerInvariant()

        { Id = id
          Digest = digest
          Preflight = preflight
          Steps = List.ofSeq steps }

    let id value = value.Id
    let digest value = value.Digest
    let preflight value = value.Preflight
    let steps value = value.Steps

[<RequireQualifiedAccess>]
type MigrationStatus =
    | Pending
    | Running
    | Failed
    | Completed
    | RollingBack
    | RolledBack

type MigrationCheckpoint =
    private
        { SchemaVersion: int
          MigrationId: string
          PlanDigest: string
          Status: MigrationStatus
          CompletedSteps: string list
          RolledBackSteps: string list
          Failure: string option
          UpdatedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module MigrationCheckpoint =
    [<Literal>]
    let CurrentSchemaVersion = 1

    let create migrationId planDigest updatedAt =
        { SchemaVersion = CurrentSchemaVersion
          MigrationId = migrationId
          PlanDigest = planDigest
          Status = MigrationStatus.Pending
          CompletedSteps = []
          RolledBackSteps = []
          Failure = None
          UpdatedAt = updatedAt }

    let schemaVersion value = value.SchemaVersion
    let migrationId value = value.MigrationId
    let planDigest value = value.PlanDigest
    let status value = value.Status
    let completedSteps value = value.CompletedSteps
    let rolledBackSteps value = value.RolledBackSteps
    let failure value = value.Failure
    let updatedAt value = value.UpdatedAt

    let internal update status completed rolledBack failure updatedAt value =
        { value with
            Status = status
            CompletedSteps = completed
            RolledBackSteps = rolledBack
            Failure = failure
            UpdatedAt = updatedAt }

type MigrationCheckpointStore =
    { LoadAsync: string -> CancellationToken -> Task<MigrationCheckpoint option>
      SaveAsync: MigrationCheckpoint -> CancellationToken -> Task }

[<RequireQualifiedAccess>]
module MigrationCheckpointStores =
    let inMemory () =
        let gate = obj ()
        let mutable checkpoints = Map.empty

        { LoadAsync = fun migrationId _ -> lock gate (fun () -> Map.tryFind migrationId checkpoints) |> Task.FromResult
          SaveAsync =
            fun checkpoint _ ->
                lock gate (fun () ->
                    checkpoints <- Map.add (MigrationCheckpoint.migrationId checkpoint) checkpoint checkpoints)

                Task.CompletedTask }

[<RequireQualifiedAccess>]
module MigrationRunner =
    let private save status completed rolledBack failure now checkpoint store cancellationToken =
        task {
            let updated =
                MigrationCheckpoint.update status completed rolledBack failure now checkpoint

            do! store.SaveAsync updated cancellationToken
            return updated
        }

    let runAsync now store plan cancellationToken =
        task {
            let migrationId = MigrationPlan.id plan
            let planDigest = MigrationPlan.digest plan
            let! stored = store.LoadAsync migrationId cancellationToken

            let checkpoint =
                match stored with
                | None -> MigrationCheckpoint.create migrationId planDigest now
                | Some value when MigrationCheckpoint.planDigest value = planDigest -> value
                | Some _ -> invalidOp "Migration checkpoint belongs to a different immutable plan."

            match MigrationCheckpoint.status checkpoint with
            | MigrationStatus.Completed -> return checkpoint
            | MigrationStatus.RollingBack
            | MigrationStatus.RolledBack ->
                return raise (InvalidOperationException "Rolled-back migrations cannot resume forward execution.")
            | MigrationStatus.Pending
            | MigrationStatus.Running
            | MigrationStatus.Failed ->
                let! started =
                    save
                        MigrationStatus.Running
                        (MigrationCheckpoint.completedSteps checkpoint)
                        (MigrationCheckpoint.rolledBackSteps checkpoint)
                        None
                        now
                        checkpoint
                        store
                        cancellationToken

                let mutable current = started

                try
                    for step in MigrationPlan.steps plan do
                        if
                            MigrationCheckpoint.completedSteps current
                            |> List.contains (MigrationStep.id step)
                            |> not
                        then
                            do! MigrationStep.applyAsync cancellationToken step

                            let! updated =
                                save
                                    MigrationStatus.Running
                                    (MigrationCheckpoint.completedSteps current @ [ MigrationStep.id step ])
                                    (MigrationCheckpoint.rolledBackSteps current)
                                    None
                                    now
                                    current
                                    store
                                    cancellationToken

                            current <- updated

                    return!
                        save
                            MigrationStatus.Completed
                            (MigrationCheckpoint.completedSteps current)
                            (MigrationCheckpoint.rolledBackSteps current)
                            None
                            now
                            current
                            store
                            cancellationToken
                with error ->
                    let! _ =
                        save
                            MigrationStatus.Failed
                            (MigrationCheckpoint.completedSteps current)
                            (MigrationCheckpoint.rolledBackSteps current)
                            (Some error.Message)
                            now
                            current
                            store
                            cancellationToken

                    return raise error
        }

    let rollbackAsync now store plan cancellationToken =
        task {
            let migrationId = MigrationPlan.id plan
            let! stored = store.LoadAsync migrationId cancellationToken

            let checkpoint =
                stored
                |> Option.defaultWith (fun () -> invalidOp "Migration rollback requires a durable checkpoint.")

            if MigrationCheckpoint.planDigest checkpoint <> MigrationPlan.digest plan then
                invalidOp "Migration checkpoint belongs to a different immutable plan."

            let! started =
                save
                    MigrationStatus.RollingBack
                    (MigrationCheckpoint.completedSteps checkpoint)
                    (MigrationCheckpoint.rolledBackSteps checkpoint)
                    None
                    now
                    checkpoint
                    store
                    cancellationToken

            let mutable current = started

            for step in MigrationPlan.steps plan |> List.rev do
                if
                    MigrationCheckpoint.completedSteps current
                    |> List.contains (MigrationStep.id step)
                    && (MigrationCheckpoint.rolledBackSteps current
                        |> List.contains (MigrationStep.id step)
                        |> not)
                then
                    do! MigrationStep.rollbackAsync cancellationToken step

                    let! updated =
                        save
                            MigrationStatus.RollingBack
                            (MigrationCheckpoint.completedSteps current)
                            (MigrationCheckpoint.rolledBackSteps current @ [ MigrationStep.id step ])
                            None
                            now
                            current
                            store
                            cancellationToken

                    current <- updated

            return!
                save
                    MigrationStatus.RolledBack
                    (MigrationCheckpoint.completedSteps current)
                    (MigrationCheckpoint.rolledBackSteps current)
                    None
                    now
                    current
                    store
                    cancellationToken
        }
