namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<Struct>]
type ChunkingMigrationId = private ChunkingMigrationId of string

[<RequireQualifiedAccess>]
module ChunkingMigrationId =
    let derive (target: ChunkerDescriptor) planHash =
        let identity =
            String.concat
                "\u001f"
                [ string (ChunkerContractVersion.value target.ContractVersion)
                  target.Component.ComponentId
                  target.Component.Version
                  ContentHash.value target.ConfigurationHash
                  ContentHash.value planHash ]

        ChunkingMigrationId("cmig_" + (identity |> ContentHash.computeText |> ContentHash.value))

    let value (ChunkingMigrationId value) = value

    let tryParse (value: string) =
        if
            not (isNull value)
            && value.StartsWith("cmig_", StringComparison.Ordinal)
            && value.Length = 69
            && value[5..]
               |> Seq.forall (fun character -> Char.IsDigit character || character >= 'a' && character <= 'f')
        then
            Some(ChunkingMigrationId value)
        else
            None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () -> invalidArg (nameof value) "Invalid canonical chunking-migration ID.")

type ChunkingMigrationCheckpoint =
    { Version: int
      Id: ChunkingMigrationId
      Owner: AuthorizationIdentity
      Target: ChunkerDescriptor
      PlanHash: ContentHash
      DocumentVersionIds: KnowledgeDocumentVersionId list
      NextDocumentIndex: int
      Revision: int
      StartedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset
      CompletedAt: DateTimeOffset option }

[<RequireQualifiedAccess>]
module ChunkingMigrationCheckpoint =
    let currentVersion = 1

    let computePlanHash documentVersionIds =
        documentVersionIds
        |> List.map KnowledgeDocumentVersionId.value
        |> String.concat "\u001f"
        |> ContentHash.computeText

    let create owner target documentVersionIds startedAt =
        if isNull (box documentVersionIds) || List.isEmpty documentVersionIds then
            invalidArg (nameof documentVersionIds) "A chunking migration requires at least one document version."

        let ordered = documentVersionIds |> List.sortBy KnowledgeDocumentVersionId.value

        if ordered <> documentVersionIds || (ordered |> Set.ofList).Count <> ordered.Length then
            invalidArg
                (nameof documentVersionIds)
                "Chunking-migration document versions must be unique and canonically ordered."

        let planHash = computePlanHash documentVersionIds

        { Version = currentVersion
          Id = ChunkingMigrationId.derive target planHash
          Owner = owner
          Target = target
          PlanHash = planHash
          DocumentVersionIds = documentVersionIds
          NextDocumentIndex = 0
          Revision = 0
          StartedAt = startedAt
          UpdatedAt = startedAt
          CompletedAt = None }

    let advance updatedAt checkpoint =
        if checkpoint.Version <> currentVersion then
            invalidArg (nameof checkpoint) "Unsupported chunking-migration checkpoint version."

        if
            checkpoint.CompletedAt.IsSome
            || checkpoint.NextDocumentIndex >= checkpoint.DocumentVersionIds.Length
        then
            invalidOp "A completed chunking migration cannot advance."

        let nextIndex = checkpoint.NextDocumentIndex + 1

        { checkpoint with
            NextDocumentIndex = nextIndex
            Revision = checkpoint.Revision + 1
            UpdatedAt = updatedAt
            CompletedAt =
                if nextIndex = checkpoint.DocumentVersionIds.Length then
                    Some updatedAt
                else
                    None }

type ChunkingMigrationInput =
    { DocumentVersionId: KnowledgeDocumentVersionId
      Units: ContentUnit list
      Lifecycle: KnowledgeLifecycle }

type ChunkingMigrationRequest =
    { Authorization: AuthorizationScope
      DocumentVersionIds: KnowledgeDocumentVersionId list
      Target: KnowledgeChunker
      RequestedAt: DateTimeOffset }

type ChunkingMigrationDocumentResult =
    { DocumentVersionId: KnowledgeDocumentVersionId
      Chunking: ChunkingResult }

type ChunkingMigrationOutcome =
    { Checkpoint: ChunkingMigrationCheckpoint
      MigratedDocuments: ChunkingMigrationDocumentResult list }

type ChunkingMigrationRunner =
    { RunAsync: ChunkingMigrationRequest -> CancellationToken -> Task<ChunkingMigrationOutcome> }
