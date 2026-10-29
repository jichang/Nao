namespace Nao.Platform

open System
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type RecoveryDataKind =
    | Session
    | SessionDirectory
    | Conversation
    | Turn
    | Feedback
    | PermissionGrant
    | Policy
    | ProviderConfiguration
    | WorkspaceRoute
    | RuntimeAdministration
    | KnowledgeSource
    | SourceCheckpoint
    | KnowledgeRecord
    | Memory
    | Artifact
    | Metric
    | Trace
    | Audit
    | ExecutionJournal
    | EvaluationDataset
    | EvaluationReport
    | MigrationCheckpoint
    | EmbeddingCache
    | VectorIndex
    | LexicalIndex
    | GraphIndex

[<RequireQualifiedAccess>]
type RecoveryDataRole =
    | Authoritative
    | Derived

[<RequireQualifiedAccess>]
module RecoveryDataKind =
    let role =
        function
        | RecoveryDataKind.VectorIndex
        | RecoveryDataKind.LexicalIndex
        | RecoveryDataKind.GraphIndex
        | RecoveryDataKind.EmbeddingCache -> RecoveryDataRole.Derived
        | _ -> RecoveryDataRole.Authoritative

type RecoveryReference =
    private
        { Kind: RecoveryDataKind
          Id: string }

[<RequireQualifiedAccess>]
module RecoveryReference =
    let create kind id =
        if String.IsNullOrWhiteSpace id || id <> id.Trim() then
            invalidArg (nameof id) "Recovery reference identity must be canonical and non-blank."

        { Kind = kind; Id = id }

    let kind value = value.Kind
    let id value = value.Id

type RecoveryRecord =
    private
        { Kind: RecoveryDataKind
          Owner: string
          Id: string
          ContentSha256: string
          References: RecoveryReference list }

[<RequireQualifiedAccess>]
module RecoveryRecord =
    let private requireCanonical argumentName value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg argumentName "Recovery identities must be canonical and non-blank."

    let private requireSha256 (value: string) =
        if
            isNull value
            || value.Length <> 64
            || value
               |> Seq.exists (fun character -> not (Char.IsDigit character || character >= 'a' && character <= 'f'))
        then
            invalidArg (nameof value) "Recovery content hashes must be lowercase SHA-256 hexadecimal values."

    let create kind owner id contentSha256 references =
        requireCanonical (nameof owner) owner
        requireCanonical (nameof id) id
        requireSha256 contentSha256

        if isNull (box references) then
            nullArg (nameof references)

        let duplicateReferences =
            references
            |> Seq.countBy (fun reference -> RecoveryReference.kind reference, RecoveryReference.id reference)
            |> Seq.exists (fun (_, count) -> count > 1)

        if duplicateReferences then
            invalidArg (nameof references) "Recovery records cannot contain duplicate references."

        if RecoveryDataKind.role kind = RecoveryDataRole.Derived && List.isEmpty references then
            invalidArg (nameof references) "Derived recovery records require authoritative source references."

        { Kind = kind
          Owner = owner
          Id = id
          ContentSha256 = contentSha256
          References = List.ofSeq references }

    let kind value = value.Kind
    let role value = RecoveryDataKind.role value.Kind
    let owner value = value.Owner
    let id value = value.Id
    let contentSha256 value = value.ContentSha256
    let references value = value.References

type BackupManifest =
    private
        { SchemaVersion: int
          BackupId: string
          Region: string
          CapturedAt: DateTimeOffset
          RecoveryPoint: DateTimeOffset
          Records: RecoveryRecord list }

[<RequireQualifiedAccess>]
module BackupManifest =
    [<Literal>]
    let CurrentSchemaVersion = 1

    let private key record =
        RecoveryRecord.owner record, RecoveryRecord.kind record, RecoveryRecord.id record

    let create backupId region capturedAt recoveryPoint records =
        for argumentName, value in [ nameof backupId, backupId; nameof region, region ] do
            if String.IsNullOrWhiteSpace value || value <> value.Trim() then
                invalidArg argumentName "Backup manifest identities must be canonical and non-blank."

        if recoveryPoint > capturedAt then
            invalidArg (nameof recoveryPoint) "A backup recovery point cannot be later than its capture time."

        if isNull (box records) || List.isEmpty records then
            invalidArg (nameof records) "Backup manifests require at least one recovery record."

        let duplicate =
            records |> Seq.countBy key |> Seq.tryFind (fun (_, count) -> count > 1)

        if duplicate.IsSome then
            invalidArg (nameof records) "Backup manifests cannot contain duplicate owner, kind, and identity tuples."

        let available = records |> Seq.map key |> Set.ofSeq

        for record in records do
            for reference in RecoveryRecord.references record do
                let referenced =
                    RecoveryRecord.owner record, RecoveryReference.kind reference, RecoveryReference.id reference

                if not (Set.contains referenced available) then
                    invalidArg
                        (nameof records)
                        $"Recovery record '{RecoveryRecord.id record}' contains a dangling or cross-owner reference."

                if
                    RecoveryRecord.role record = RecoveryDataRole.Derived
                    && RecoveryDataKind.role (RecoveryReference.kind reference)
                       <> RecoveryDataRole.Authoritative
                then
                    invalidArg (nameof records) "Derived recovery records can depend only on authoritative records."

        { SchemaVersion = CurrentSchemaVersion
          BackupId = backupId
          Region = region
          CapturedAt = capturedAt
          RecoveryPoint = recoveryPoint
          Records = List.ofSeq records }

    let schemaVersion value = value.SchemaVersion
    let backupId value = value.BackupId
    let region value = value.Region
    let capturedAt value = value.CapturedAt
    let recoveryPoint value = value.RecoveryPoint
    let records value = value.Records

type RecoveryDeletion =
    private
        { Owner: string
          Kind: RecoveryDataKind
          Id: string
          RequestedAt: DateTimeOffset
          RetainUntil: DateTimeOffset option
          LegalHoldReason: string option }

[<RequireQualifiedAccess>]
module RecoveryDeletion =
    let create owner kind id requestedAt retainUntil legalHoldReason =
        for argumentName, value in [ nameof owner, owner; nameof id, id ] do
            if String.IsNullOrWhiteSpace value || value <> value.Trim() then
                invalidArg argumentName "Recovery deletion identities must be canonical and non-blank."

        if retainUntil |> Option.exists (fun cutoff -> cutoff < requestedAt) then
            invalidArg (nameof retainUntil) "Backup retention cannot end before the deletion request."

        let legalHoldReason =
            legalHoldReason
            |> Option.map (fun reason ->
                if String.IsNullOrWhiteSpace reason || reason <> reason.Trim() then
                    invalidArg (nameof legalHoldReason) "Legal-hold reasons must be canonical and non-blank."

                reason)

        { Owner = owner
          Kind = kind
          Id = id
          RequestedAt = requestedAt
          RetainUntil = retainUntil
          LegalHoldReason = legalHoldReason }

    let owner value = value.Owner
    let kind value = value.Kind
    let id value = value.Id
    let requestedAt value = value.RequestedAt
    let retainUntil value = value.RetainUntil
    let legalHoldReason value = value.LegalHoldReason

type RecoveryDeletionLedger =
    private
        { SchemaVersion: int
          RecordedAt: DateTimeOffset
          Entries: RecoveryDeletion list }

[<RequireQualifiedAccess>]
module RecoveryDeletionLedger =
    [<Literal>]
    let CurrentSchemaVersion = 1

    let create recordedAt entries =
        if isNull (box entries) then
            nullArg (nameof entries)

        let duplicate =
            entries
            |> Seq.countBy (fun deletion ->
                RecoveryDeletion.owner deletion, RecoveryDeletion.kind deletion, RecoveryDeletion.id deletion)
            |> Seq.exists (fun (_, count) -> count > 1)

        if duplicate then
            invalidArg (nameof entries) "Recovery deletion ledgers cannot contain duplicate targets."

        if
            entries
            |> Seq.exists (fun deletion -> RecoveryDeletion.requestedAt deletion > recordedAt)
        then
            invalidArg (nameof entries) "Recovery deletion ledgers cannot contain future requests."

        { SchemaVersion = CurrentSchemaVersion
          RecordedAt = recordedAt
          Entries = List.ofSeq entries }

    let schemaVersion value = value.SchemaVersion
    let recordedAt value = value.RecordedAt
    let entries value = value.Entries

type DerivedRebuild =
    private
        { Owner: string
          Kind: RecoveryDataKind
          Id: string
          ExpectedContentSha256: string
          SourceReferences: RecoveryReference list }

[<RequireQualifiedAccess>]
module DerivedRebuild =
    let owner value = value.Owner
    let kind value = value.Kind
    let id value = value.Id
    let expectedContentSha256 value = value.ExpectedContentSha256
    let sourceReferences value = value.SourceReferences

type RestorePlan =
    private
        { BackupId: string
          RecoveryPoint: DateTimeOffset
          TargetRegion: string
          AuthoritativeRecords: RecoveryRecord list
          DerivedRebuilds: DerivedRebuild list
          AppliedDeletions: RecoveryDeletion list }

[<RequireQualifiedAccess>]
module RestorePlan =
    let private recordKey record =
        RecoveryRecord.owner record, RecoveryRecord.kind record, RecoveryRecord.id record

    let create manifest deletionLedger targetRegion allowedRegions =
        if String.IsNullOrWhiteSpace targetRegion || targetRegion <> targetRegion.Trim() then
            invalidArg (nameof targetRegion) "Restore target regions must be canonical and non-blank."

        if not (Set.contains targetRegion allowedRegions) then
            invalidArg (nameof targetRegion) "Restore target region is not allowed by regional-placement policy."

        let deleted =
            RecoveryDeletionLedger.entries deletionLedger
            |> Seq.map (fun deletion ->
                RecoveryDeletion.owner deletion, RecoveryDeletion.kind deletion, RecoveryDeletion.id deletion)
            |> Set.ofSeq

        let available =
            BackupManifest.records manifest
            |> Seq.filter (recordKey >> deleted.Contains >> not)
            |> Seq.map recordKey
            |> Set.ofSeq

        let authoritative =
            BackupManifest.records manifest
            |> List.filter (fun record ->
                RecoveryRecord.role record = RecoveryDataRole.Authoritative
                && not (Set.contains (recordKey record) deleted))

        for record in authoritative do
            for reference in RecoveryRecord.references record do
                let referenced =
                    RecoveryRecord.owner record, RecoveryReference.kind reference, RecoveryReference.id reference

                if not (Set.contains referenced available) then
                    invalidArg
                        (nameof deletionLedger)
                        $"Deletion suppression would leave recovery record '{RecoveryRecord.id record}' dangling."

        let rebuilds =
            BackupManifest.records manifest
            |> List.filter (fun record ->
                RecoveryRecord.role record = RecoveryDataRole.Derived
                && not (Set.contains (recordKey record) deleted))
            |> List.map (fun record ->
                for reference in RecoveryRecord.references record do
                    let referenced =
                        RecoveryRecord.owner record, RecoveryReference.kind reference, RecoveryReference.id reference

                    if not (Set.contains referenced available) then
                        invalidArg
                            (nameof deletionLedger)
                            $"Derived rebuild '{RecoveryRecord.id record}' lost an authoritative source."

                { Owner = RecoveryRecord.owner record
                  Kind = RecoveryRecord.kind record
                  Id = RecoveryRecord.id record
                  ExpectedContentSha256 = RecoveryRecord.contentSha256 record
                  SourceReferences = RecoveryRecord.references record })

        { BackupId = BackupManifest.backupId manifest
          RecoveryPoint = BackupManifest.recoveryPoint manifest
          TargetRegion = targetRegion
          AuthoritativeRecords = authoritative
          DerivedRebuilds = rebuilds
          AppliedDeletions = RecoveryDeletionLedger.entries deletionLedger }

    let backupId value = value.BackupId
    let recoveryPoint value = value.RecoveryPoint
    let targetRegion value = value.TargetRegion
    let authoritativeRecords value = value.AuthoritativeRecords
    let derivedRebuilds value = value.DerivedRebuilds
    let appliedDeletions value = value.AppliedDeletions

type RecoveryRestoreTarget =
    { RestoreAuthoritativeAsync: RecoveryRecord -> CancellationToken -> Task
      RebuildDerivedAsync: DerivedRebuild -> CancellationToken -> Task
      ReadContentSha256Async: string -> RecoveryDataKind -> string -> CancellationToken -> Task<string option> }

type RecoveryExecutionReport =
    private
        { BackupId: string
          RestoredAuthoritativeRecords: int
          RebuiltDerivedRecords: int
          VerifiedRecords: int }

[<RequireQualifiedAccess>]
module RecoveryExecutionReport =
    let backupId value = value.BackupId
    let restoredAuthoritativeRecords value = value.RestoredAuthoritativeRecords
    let rebuiltDerivedRecords value = value.RebuiltDerivedRecords
    let verifiedRecords value = value.VerifiedRecords

[<RequireQualifiedAccess>]
module RecoveryCoordinator =
    let restoreAsync target plan cancellationToken =
        task {
            if isNull (box target.RestoreAuthoritativeAsync) then
                nullArg (nameof target.RestoreAuthoritativeAsync)

            if isNull (box target.RebuildDerivedAsync) then
                nullArg (nameof target.RebuildDerivedAsync)

            if isNull (box target.ReadContentSha256Async) then
                nullArg (nameof target.ReadContentSha256Async)

            for record in RestorePlan.authoritativeRecords plan do
                do! target.RestoreAuthoritativeAsync record cancellationToken

            for rebuild in RestorePlan.derivedRebuilds plan do
                do! target.RebuildDerivedAsync rebuild cancellationToken

            let mutable verified = 0

            for record in RestorePlan.authoritativeRecords plan do
                let! actual =
                    target.ReadContentSha256Async
                        (RecoveryRecord.owner record)
                        (RecoveryRecord.kind record)
                        (RecoveryRecord.id record)
                        cancellationToken

                if actual <> Some(RecoveryRecord.contentSha256 record) then
                    invalidOp $"Restored recovery record '{RecoveryRecord.id record}' failed content verification."

                verified <- verified + 1

            for rebuild in RestorePlan.derivedRebuilds plan do
                let! actual =
                    target.ReadContentSha256Async
                        (DerivedRebuild.owner rebuild)
                        (DerivedRebuild.kind rebuild)
                        (DerivedRebuild.id rebuild)
                        cancellationToken

                if actual <> Some(DerivedRebuild.expectedContentSha256 rebuild) then
                    invalidOp $"Rebuilt recovery record '{DerivedRebuild.id rebuild}' failed content verification."

                verified <- verified + 1

            return
                { BackupId = RestorePlan.backupId plan
                  RestoredAuthoritativeRecords = RestorePlan.authoritativeRecords plan |> List.length
                  RebuiltDerivedRecords = RestorePlan.derivedRebuilds plan |> List.length
                  VerifiedRecords = verified }
        }

[<RequireQualifiedAccess>]
type RecoveryObjective =
    | BestEffort
    | Bounded of MaximumDataLoss: TimeSpan * MaximumRecoveryTime: TimeSpan
    | ControlPlaneOwned

[<RequireQualifiedAccess>]
module RecoveryObjectives =
    let forProfile =
        function
        | DeploymentProfile.LocalDevelopment -> RecoveryObjective.BestEffort
        | DeploymentProfile.SingleNodeProduction ->
            RecoveryObjective.Bounded(TimeSpan.FromMinutes 15.0, TimeSpan.FromHours 4.0)
        | DeploymentProfile.ClusteredProduction ->
            RecoveryObjective.Bounded(TimeSpan.FromMinutes 5.0, TimeSpan.FromHours 1.0)
        | DeploymentProfile.IsolatedWorker -> RecoveryObjective.ControlPlaneOwned

type RestoreDrillReport =
    private
        { BackupId: string
          StartedAt: DateTimeOffset
          CompletedAt: DateTimeOffset
          RestoredAuthoritativeRecords: int
          ScheduledRebuilds: int
          ObjectiveMet: bool }

[<RequireQualifiedAccess>]
module RestoreDrill =
    let run objective startedAt completedAt now plan =
        if completedAt < startedAt then
            invalidArg (nameof completedAt) "Restore drills cannot complete before they start."

        let objectiveMet =
            match objective with
            | RecoveryObjective.BestEffort
            | RecoveryObjective.ControlPlaneOwned -> true
            | RecoveryObjective.Bounded(maximumDataLoss, maximumRecoveryTime) ->
                now - RestorePlan.recoveryPoint plan <= maximumDataLoss
                && completedAt - startedAt <= maximumRecoveryTime

        { BackupId = RestorePlan.backupId plan
          StartedAt = startedAt
          CompletedAt = completedAt
          RestoredAuthoritativeRecords = RestorePlan.authoritativeRecords plan |> List.length
          ScheduledRebuilds = RestorePlan.derivedRebuilds plan |> List.length
          ObjectiveMet = objectiveMet }

    let backupId value = value.BackupId
    let startedAt value = value.StartedAt
    let completedAt value = value.CompletedAt
    let restoredAuthoritativeRecords value = value.RestoredAuthoritativeRecords
    let scheduledRebuilds value = value.ScheduledRebuilds
    let objectiveMet value = value.ObjectiveMet
