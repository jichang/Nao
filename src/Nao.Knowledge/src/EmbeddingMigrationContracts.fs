namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<Struct>]
type EmbeddingMigrationId = private EmbeddingMigrationId of string

[<RequireQualifiedAccess>]
module EmbeddingMigrationId =
    let derive sourceProfileId targetProfileId planHash =
        String.concat
            "\u001f"
            [ EmbeddingProfileId.value sourceProfileId
              EmbeddingProfileId.value targetProfileId
              ContentHash.value planHash ]
        |> ContentHash.computeText
        |> ContentHash.value
        |> fun hash -> EmbeddingMigrationId("emig_" + hash)

    let value (EmbeddingMigrationId value) = value

    let tryParse (value: string) =
        if
            not (isNull value)
            && value.StartsWith("emig_", StringComparison.Ordinal)
            && value.Length = 69
            && value[5..]
               |> Seq.forall (fun character -> Char.IsDigit character || character >= 'a' && character <= 'f')
        then
            Some(EmbeddingMigrationId value)
        else
            None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () -> invalidArg (nameof value) "Invalid canonical embedding-migration ID.")

type EmbeddingMigrationCheckpoint =
    private
        { Version: int
          Id: EmbeddingMigrationId
          Owner: AuthorizationIdentity
          Source: EmbeddingCapabilityDescriptor
          Target: EmbeddingCapabilityDescriptor
          PlanHash: ContentHash
          ContentHashes: ContentHash list
          NextContentIndex: int
          Revision: int
          StartedAt: DateTimeOffset
          UpdatedAt: DateTimeOffset
          CompletedAt: DateTimeOffset option }

[<RequireQualifiedAccess>]
module EmbeddingMigrationCheckpoint =
    let currentVersion = 1

    let computePlanHash contentHashes =
        contentHashes
        |> List.map ContentHash.value
        |> String.concat "\u001f"
        |> ContentHash.computeText

    let private validatePlan contentHashes =
        if isNull (box contentHashes) || List.isEmpty contentHashes then
            invalidArg (nameof contentHashes) "An embedding migration requires at least one content hash."

        let ordered = contentHashes |> List.sortBy ContentHash.value

        if ordered <> contentHashes || (ordered |> Set.ofList).Count <> ordered.Length then
            invalidArg
                (nameof contentHashes)
                "Embedding-migration content hashes must be unique and canonically ordered."

    let create owner source target contentHashes startedAt =
        let source = EmbeddingCapabilityDescriptor.validate source
        let target = EmbeddingCapabilityDescriptor.validate target

        if source.ProfileId = target.ProfileId then
            invalidArg (nameof target) "Embedding migration source and target profiles must differ."

        validatePlan contentHashes
        let planHash = computePlanHash contentHashes

        { Version = currentVersion
          Id = EmbeddingMigrationId.derive source.ProfileId target.ProfileId planHash
          Owner = owner
          Source = source
          Target = target
          PlanHash = planHash
          ContentHashes = contentHashes
          NextContentIndex = 0
          Revision = 0
          StartedAt = startedAt
          UpdatedAt = startedAt
          CompletedAt = None }

    let restore
        version
        id
        owner
        source
        target
        planHash
        contentHashes
        nextContentIndex
        revision
        startedAt
        updatedAt
        completedAt
        =
        if version <> currentVersion then
            invalidArg (nameof version) "Unsupported embedding-migration checkpoint version."

        let initial = create owner source target contentHashes startedAt

        if initial.Id <> id || initial.PlanHash <> planHash then
            invalidArg (nameof id) "Embedding-migration identity does not match its profiles and content plan."

        if nextContentIndex < 0 || nextContentIndex > contentHashes.Length then
            invalidArg (nameof nextContentIndex) "Embedding-migration cursor is outside its content plan."

        if revision < 0 || (nextContentIndex = 0) <> (revision = 0) then
            invalidArg (nameof revision) "Embedding-migration revision is inconsistent with its cursor."

        if updatedAt < startedAt then
            invalidArg (nameof updatedAt) "Embedding-migration update time precedes its start time."

        match completedAt with
        | Some completed when nextContentIndex <> contentHashes.Length || completed <> updatedAt ->
            invalidArg (nameof completedAt) "Embedding-migration completion is inconsistent with its cursor."
        | None when nextContentIndex = contentHashes.Length ->
            invalidArg (nameof completedAt) "A fully advanced embedding migration must be complete."
        | _ -> ()

        { initial with
            NextContentIndex = nextContentIndex
            Revision = revision
            UpdatedAt = updatedAt
            CompletedAt = completedAt }

    let advance count updatedAt checkpoint =
        if count <= 0 then
            invalidArg (nameof count) "Embedding migration must advance by at least one content item."

        if checkpoint.Version <> currentVersion then
            invalidArg (nameof checkpoint) "Unsupported embedding-migration checkpoint version."

        if
            checkpoint.CompletedAt.IsSome
            || checkpoint.NextContentIndex >= checkpoint.ContentHashes.Length
        then
            invalidOp "A completed embedding migration cannot advance."

        let nextIndex = checkpoint.NextContentIndex + count

        if nextIndex > checkpoint.ContentHashes.Length then
            invalidArg (nameof count) "Embedding migration cannot advance beyond its content plan."

        { checkpoint with
            NextContentIndex = nextIndex
            Revision = checkpoint.Revision + 1
            UpdatedAt = updatedAt
            CompletedAt =
                if nextIndex = checkpoint.ContentHashes.Length then
                    Some updatedAt
                else
                    None }

    let version value = value.Version
    let id value = value.Id
    let owner value = value.Owner
    let source value = value.Source
    let target value = value.Target
    let planHash value = value.PlanHash
    let contentHashes value = value.ContentHashes
    let nextContentIndex value = value.NextContentIndex
    let revision value = value.Revision
    let startedAt value = value.StartedAt
    let updatedAt value = value.UpdatedAt
    let completedAt value = value.CompletedAt

type KnowledgeEmbeddingMigrationStore =
    { LoadAsync:
        AuthorizationScope -> EmbeddingMigrationId -> CancellationToken -> Task<EmbeddingMigrationCheckpoint option>
      SaveAsync:
          AuthorizationScope
              -> EmbeddingMigrationCheckpoint option
              -> EmbeddingMigrationCheckpoint
              -> CancellationToken
              -> Task }

type EmbeddingMigrationRequest =
    { Authorization: AuthorizationScope
      Source: EmbeddingCapabilityDescriptor
      Target: CachedEmbeddingExecutor
      Inputs: EmbeddingInput list
      RequestedAt: DateTimeOffset }

type EmbeddingMigrationOutcome =
    { Checkpoint: EmbeddingMigrationCheckpoint
      MigratedContentCount: int
      ExecutionMetrics: CachedEmbeddingExecutionMetrics list }

type EmbeddingMigrationRunner =
    { RunAsync: EmbeddingMigrationRequest -> CancellationToken -> Task<EmbeddingMigrationOutcome> }

[<Struct>]
type EmbeddingContentReference =
    { ChunkId: KnowledgeChunkId
      ContentHash: ContentHash }

[<RequireQualifiedAccess>]
type EmbeddingReadMode =
    | SourceOnly
    | TargetFirst
    | TargetOnly

type EmbeddingUpgradeReadRequest =
    { Authorization: AuthorizationScope
      Inputs: EmbeddingContentReference list
      Mode: EmbeddingReadMode }

type EmbeddingUpgradeReadResult =
    { Profile: EmbeddingCapabilityDescriptor
      Embeddings: GeneratedEmbedding list }

type EmbeddingUpgradeReader =
    { ResolveAsync: EmbeddingUpgradeReadRequest -> CancellationToken -> Task<EmbeddingUpgradeReadResult> }
