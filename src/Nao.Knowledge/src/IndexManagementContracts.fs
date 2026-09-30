namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
type KnowledgeIndexCreationResult =
    | Created
    | AlreadyExists

[<RequireQualifiedAccess>]
type KnowledgeIndexHealthStatus =
    | Healthy
    | Degraded
    | Unavailable

type KnowledgeIndexHealth =
    private
        { Status: KnowledgeIndexHealthStatus
          SchemaVersion: int option
          SchemaRevision: ContentHash option
          SearchableRecords: int64 option
          CheckedAt: DateTimeOffset
          Issues: string list }

[<RequireQualifiedAccess>]
module KnowledgeIndexHealth =
    let create status schemaVersion schemaRevision searchableRecords checkedAt issues =
        schemaVersion
        |> Option.iter (fun version ->
            if version <= 0 then
                invalidArg (nameof schemaVersion) "Index schema version must be positive.")

        searchableRecords
        |> Option.iter (fun count ->
            if count < 0L then
                invalidArg (nameof searchableRecords) "Searchable index record count cannot be negative.")

        let issues =
            issues
            |> List.map (fun issue ->
                if String.IsNullOrWhiteSpace issue || issue <> issue.Trim() then
                    invalidArg (nameof issues) "Index health issues must be non-blank and trimmed."

                issue)

        match status, issues with
        | KnowledgeIndexHealthStatus.Healthy, [] -> ()
        | KnowledgeIndexHealthStatus.Healthy, _ ->
            invalidArg (nameof issues) "A healthy index cannot report health issues."
        | _, [] -> invalidArg (nameof issues) "A degraded or unavailable index must report at least one issue."
        | _ -> ()

        { Status = status
          SchemaVersion = schemaVersion
          SchemaRevision = schemaRevision
          SearchableRecords = searchableRecords
          CheckedAt = checkedAt
          Issues = issues }

    let status value = value.Status
    let schemaVersion value = value.SchemaVersion
    let schemaRevision value = value.SchemaRevision
    let searchableRecords value = value.SearchableRecords
    let checkedAt value = value.CheckedAt
    let issues value = value.Issues

type KnowledgeIndexMigrationPlan =
    private
        { CurrentRevision: ContentHash option
          TargetRevision: ContentHash
          RequiresRebuild: bool
          PlannedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module KnowledgeIndexMigrationPlan =
    let create currentRevision targetRevision requiresRebuild plannedAt =
        if currentRevision = Some targetRevision then
            invalidArg (nameof targetRevision) "Index migration target must differ from the current revision."

        { CurrentRevision = currentRevision
          TargetRevision = targetRevision
          RequiresRebuild = requiresRebuild
          PlannedAt = plannedAt }

    let currentRevision value = value.CurrentRevision
    let targetRevision value = value.TargetRevision
    let requiresRebuild value = value.RequiresRebuild
    let plannedAt value = value.PlannedAt

type KnowledgeIndexBackup<'Record> =
    private
        { Version: int
          Namespace: KnowledgeIndexNamespace
          Owner: AuthorizationIdentity
          CreatedAt: DateTimeOffset
          Records: 'Record list }

[<RequireQualifiedAccess>]
module KnowledgeIndexBackup =
    let currentVersion = 1

    let private build version namespaceName owner createdAt records =
        if version <> currentVersion then
            invalidArg (nameof version) "Unsupported knowledge-index backup version."

        if isNull (box records) then
            nullArg (nameof records)

        { Version = version
          Namespace = namespaceName
          Owner = owner
          CreatedAt = createdAt
          Records = records }

    let create namespaceName owner createdAt records =
        build currentVersion namespaceName owner createdAt records

    let restore version namespaceName owner createdAt records =
        build version namespaceName owner createdAt records

    let version value = value.Version
    let namespaceName value = value.Namespace
    let owner value = value.Owner
    let createdAt value = value.CreatedAt
    let records value = value.Records

    let requireOwner (authorization: AuthorizationScope) backup =
        if AuthorizationScope.identity authorization <> backup.Owner then
            raise (UnauthorizedAccessException "Knowledge-index backup belongs to another authorization identity.")

        backup

type KnowledgeIndexRebuildResult =
    private
        { Namespace: KnowledgeIndexNamespace
          PreviousRecords: int64
          CurrentRecords: int64
          CompletedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module KnowledgeIndexRebuildResult =
    let create namespaceName previousRecords currentRecords completedAt =
        if previousRecords < 0L then
            invalidArg (nameof previousRecords) "Previous index record count cannot be negative."

        if currentRecords < 0L then
            invalidArg (nameof currentRecords) "Current index record count cannot be negative."

        { Namespace = namespaceName
          PreviousRecords = previousRecords
          CurrentRecords = currentRecords
          CompletedAt = completedAt }

    let namespaceName value = value.Namespace
    let previousRecords value = value.PreviousRecords
    let currentRecords value = value.CurrentRecords
    let completedAt value = value.CompletedAt

[<RequireQualifiedAccess>]
module KnowledgeIndexRebuild =
    let private runAsync applyAsync authorization namespaceName records cancellationToken =
        task {
            let mutation =
                KnowledgeIndexMutation.create namespaceName records [] (Some KnowledgeIndexFilter.none) []

            let! applied = applyAsync authorization mutation cancellationToken

            if KnowledgeIndexMutationResult.namespaceName applied <> namespaceName then
                invalidOp "Index rebuild mutation returned evidence for another namespace."

            if KnowledgeIndexMutationResult.upsertedRecords applied <> List.length records then
                invalidOp "Index rebuild mutation returned an unexpected authoritative record count."

            return
                KnowledgeIndexRebuildResult.create
                    namespaceName
                    (KnowledgeIndexMutationResult.deletedRecords applied |> int64)
                    (KnowledgeIndexMutationResult.upsertedRecords applied |> int64)
                    (KnowledgeIndexMutationResult.completedAt applied)
        }

    let vectorAsync (index: KnowledgeVectorIndex) authorization namespaceName records cancellationToken =
        runAsync index.ApplyAsync authorization namespaceName records cancellationToken

    let lexicalAsync (index: KnowledgeLexicalIndex) authorization namespaceName records cancellationToken =
        runAsync index.ApplyAsync authorization namespaceName records cancellationToken

type KnowledgeIndexManagement<'Record> =
    { EnsureCreatedAsync: CancellationToken -> Task<KnowledgeIndexCreationResult>
      CheckHealthAsync: CancellationToken -> Task<KnowledgeIndexHealth>
      PlanMigrationAsync: CancellationToken -> Task<KnowledgeIndexMigrationPlan option>
      MigrateAsync: KnowledgeIndexMigrationPlan -> CancellationToken -> Task
      RebuildAsync:
          AuthorizationScope
              -> KnowledgeIndexNamespace
              -> 'Record list
              -> CancellationToken
              -> Task<KnowledgeIndexRebuildResult>
      BackupAsync:
          AuthorizationScope -> KnowledgeIndexNamespace -> CancellationToken -> Task<KnowledgeIndexBackup<'Record>>
      RestoreAsync:
          AuthorizationScope -> KnowledgeIndexBackup<'Record> -> CancellationToken -> Task<KnowledgeIndexRebuildResult> }

type ManagedKnowledgeVectorIndex =
    { Index: KnowledgeVectorIndex
      Management: KnowledgeIndexManagement<VectorIndexRecord> }

type ManagedKnowledgeLexicalIndex =
    { Index: KnowledgeLexicalIndex
      Management: KnowledgeIndexManagement<KnowledgeIndexDocument> }
