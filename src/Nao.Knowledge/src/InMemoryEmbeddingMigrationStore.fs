namespace Nao.Knowledge

open System
open System.Collections.Generic
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
module KnowledgeEmbeddingMigrationStores =
    let inMemory () =
        let gate = obj ()

        let partitions =
            Dictionary<string, Dictionary<EmbeddingMigrationId, EmbeddingMigrationCheckpoint>>()

        let partition authorization =
            authorization |> AuthorizationScope.identity |> AuthorizationIdentity.storageKey

        { LoadAsync =
            fun authorization migrationId cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()
                let partitionKey = partition authorization

                lock gate (fun () ->
                    match partitions.TryGetValue partitionKey with
                    | true, records ->
                        match records.TryGetValue migrationId with
                        | true, checkpoint -> Some checkpoint
                        | false, _ -> None
                    | false, _ -> None)
                |> Task.FromResult
          SaveAsync =
            fun authorization expected checkpoint cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()
                let identity = AuthorizationScope.identity authorization

                if EmbeddingMigrationCheckpoint.owner checkpoint <> identity then
                    raise (UnauthorizedAccessException "Embedding-migration checkpoint belongs to another identity.")

                let checkpoint =
                    EmbeddingMigrationCheckpoint.restore
                        (EmbeddingMigrationCheckpoint.version checkpoint)
                        (EmbeddingMigrationCheckpoint.id checkpoint)
                        (EmbeddingMigrationCheckpoint.owner checkpoint)
                        (EmbeddingMigrationCheckpoint.source checkpoint)
                        (EmbeddingMigrationCheckpoint.target checkpoint)
                        (EmbeddingMigrationCheckpoint.planHash checkpoint)
                        (EmbeddingMigrationCheckpoint.contentHashes checkpoint)
                        (EmbeddingMigrationCheckpoint.nextContentIndex checkpoint)
                        (EmbeddingMigrationCheckpoint.revision checkpoint)
                        (EmbeddingMigrationCheckpoint.startedAt checkpoint)
                        (EmbeddingMigrationCheckpoint.updatedAt checkpoint)
                        (EmbeddingMigrationCheckpoint.completedAt checkpoint)

                let partitionKey = partition authorization

                lock gate (fun () ->
                    let records =
                        match partitions.TryGetValue partitionKey with
                        | true, existing -> existing
                        | false, _ ->
                            let created = Dictionary()
                            partitions.Add(partitionKey, created)
                            created

                    let migrationId = EmbeddingMigrationCheckpoint.id checkpoint

                    match (records.TryGetValue migrationId), expected with
                    | (false, _), None ->
                        if EmbeddingMigrationCheckpoint.revision checkpoint <> 1 then
                            invalidOp "The first persisted embedding-migration checkpoint must have revision one."
                    | (true, current), Some prior when current = prior ->
                        if
                            EmbeddingMigrationCheckpoint.revision checkpoint
                            <> EmbeddingMigrationCheckpoint.revision current + 1
                        then
                            invalidOp "Embedding-migration checkpoint revisions must advance exactly once."
                    | _ -> invalidOp "Embedding-migration checkpoint compare-and-swap failed."

                    records[migrationId] <- checkpoint)

                Task.CompletedTask }
