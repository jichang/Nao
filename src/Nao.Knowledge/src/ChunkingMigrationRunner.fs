namespace Nao.Knowledge

open System
open System.Threading
open Nao.Agents

[<RequireQualifiedAccess>]
module ChunkingMigration =
    let private plan (documentVersionIds: KnowledgeDocumentVersionId list) =
        if isNull (box documentVersionIds) || List.isEmpty documentVersionIds then
            invalidArg (nameof documentVersionIds) "A chunking migration requires at least one document version."

        let ordered = documentVersionIds |> List.sortBy KnowledgeDocumentVersionId.value

        if (ordered |> Set.ofList).Count <> ordered.Length then
            invalidArg (nameof documentVersionIds) "A chunking migration cannot contain duplicate document versions."

        ordered

    let create (catalog: KnowledgeCatalog) (clock: unit -> DateTimeOffset) =
        if isNull (box catalog) then
            nullArg (nameof catalog)

        if isNull (box clock) then
            nullArg (nameof clock)

        { RunAsync =
            fun request cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    if isNull (box request.Target) then
                        nullArg (nameof request.Target)

                    let documentVersionIds = plan request.DocumentVersionIds

                    let initial =
                        ChunkingMigrationCheckpoint.create
                            (AuthorizationScope.identity request.Authorization)
                            request.Target.Descriptor
                            documentVersionIds
                            request.RequestedAt

                    let! stored = catalog.LoadChunkingMigrationAsync request.Authorization initial.Id cancellationToken

                    let mutable checkpoint =
                        match stored with
                        | Some existing ->
                            if
                                existing.Owner <> initial.Owner
                                || existing.Target <> initial.Target
                                || existing.PlanHash <> initial.PlanHash
                                || existing.DocumentVersionIds <> initial.DocumentVersionIds
                            then
                                invalidOp "Stored chunking-migration checkpoint does not match the requested plan."

                            existing
                        | None -> initial

                    let migrated = ResizeArray<ChunkingMigrationDocumentResult>()

                    while checkpoint.CompletedAt.IsNone do
                        cancellationToken.ThrowIfCancellationRequested()

                        let documentVersionId = documentVersionIds[checkpoint.NextDocumentIndex]

                        let! input =
                            catalog.LoadChunkingMigrationInputAsync
                                request.Authorization
                                documentVersionId
                                cancellationToken

                        let! result =
                            request.Target.ChunkAsync
                                { Authorization = request.Authorization
                                  DocumentVersionId = input.DocumentVersionId
                                  Units = input.Units
                                  Lifecycle = input.Lifecycle
                                  ChunkedAt = clock () }
                                cancellationToken

                        let next = ChunkingMigrationCheckpoint.advance (clock ()) checkpoint

                        do!
                            catalog.CommitChunkingMigrationStepAsync
                                request.Authorization
                                (if checkpoint.Revision = 0 then None else Some checkpoint)
                                next
                                input.DocumentVersionId
                                result.Chunks
                                cancellationToken

                        checkpoint <- next

                        migrated.Add(
                            { DocumentVersionId = input.DocumentVersionId
                              Chunking = result }
                        )

                    return
                        { Checkpoint = checkpoint
                          MigratedDocuments = List.ofSeq migrated }
                } }
