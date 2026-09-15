namespace Nao.Knowledge.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("FailClosed")>]
type ChunkingMigrationTests() =
    let now = DateTimeOffset(2026, 9, 15, 2, 0, 0, TimeSpan.Zero)
    let lifecycle = KnowledgeLifecycle.active None false

    let authorization =
        SecurityPrincipal.create (TenantId.parse "migration-tenant") (UserId.parse "migration-user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "migration-workspace") None
            |> Option.get

    let sourceId = KnowledgeSourceId.parse "migration-source"
    let documentId = KnowledgeDocumentId.parse "migration-document"
    let uri = Uri "https://example.test/migration"
    let parser = ComponentVersion.create "fixture-parser" "1"
    let oldChunker = ComponentVersion.create "old-chunker" "1"
    let oldConfiguration = ContentHash.computeText "old-configuration"

    let trust =
        TrustAssessment.create KnowledgeTrust.OrganizationVerified "migration fixture" now

    let source =
        KnowledgeSource.create
            sourceId
            uri
            (AuthorizationScope.identity authorization)
            DataClassification.Internal
            trust
            Map.empty
            now
            lifecycle

    let document =
        KnowledgeDocument.create documentId sourceId uri Map.empty now lifecycle

    let bundle versionNumber text =
        let contentHash = ContentHash.computeText text

        let version =
            KnowledgeDocumentVersion.create
                documentId
                sourceId
                versionNumber
                contentHash
                (SourceAcquisition.create (ComponentVersion.create "fixture-source" "1") contentHash now Map.empty)
                "text/plain"
                (Some "en")
                (int64 text.Length)
                None
                now
                DataClassification.Internal
                trust
                Map.empty
                lifecycle

        let location =
            SourceLocation.create 0L (int64 text.Length) 0 text.Length None (Some 1) (Some 1) None

        let unit =
            ContentUnit.create version.Id 0 ContentUnitKind.Paragraph text location parser [] Map.empty lifecycle

        let chunk =
            KnowledgeChunk.create
                version.Id
                0
                KnowledgeChunkRole.Leaf
                None
                [ unit.Id ]
                text
                [ location ]
                oldChunker
                oldConfiguration
                []
                Map.empty
                lifecycle

        let index = KnowledgeIndexEntry.create "migration-index" chunk None now []

        version,
        unit,
        chunk,
        { Source = source
          Document = document
          Version = version
          ContentUnits = [ unit ]
          Chunks = [ chunk ]
          IndexEntries = [ index ] }

    let saveBundle catalog bundle =
        catalog.SaveVersionAsync authorization bundle CancellationToken.None
        |> _.GetAwaiter().GetResult()

    let run
        (runner: ChunkingMigrationRunner)
        (target: KnowledgeChunker)
        (documentVersionIds: KnowledgeDocumentVersionId list)
        =
        let request: ChunkingMigrationRequest =
            { Authorization = authorization
              DocumentVersionIds = documentVersionIds
              Target = target
              RequestedAt = now }

        runner.RunAsync request CancellationToken.None |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Interrupted strategy migration resumes after the last atomic document commit``() =
        let catalog = KnowledgeCatalog.inMemory ()
        let firstVersion, _, firstOldChunk, firstBundle = bundle 1 "first migration passage"

        let secondVersion, _, secondOldChunk, secondBundle =
            bundle 2 "second migration passage"

        saveBundle catalog firstBundle
        saveBundle catalog secondBundle

        let saveCache (version: KnowledgeDocumentVersion) (chunk: KnowledgeChunk) =
            KnowledgeCacheEntry.create
                (RetrievalResultId.generate ())
                "cached query"
                sourceId
                version.Id
                [ chunk.Id ]
                now
                (now.AddHours 1.0)
                lifecycle
            |> fun entry ->
                catalog.SaveCacheEntryAsync authorization entry CancellationToken.None
                |> _.GetAwaiter().GetResult()

        saveCache firstVersion firstOldChunk
        saveCache secondVersion secondOldChunk

        let orderedIds =
            [ firstVersion.Id; secondVersion.Id ]
            |> List.sortBy KnowledgeDocumentVersionId.value

        let secondId = orderedIds[1]
        let inner = StructureAwareChunker.create { MaximumParentCharacters = 1000 }
        let attempts = Dictionary<KnowledgeDocumentVersionId, int>()
        let mutable interrupt = true

        let target: KnowledgeChunker =
            { Descriptor = inner.Descriptor
              ChunkAsync =
                fun request cancellationToken ->
                    let count =
                        match attempts.TryGetValue request.DocumentVersionId with
                        | true, value -> value + 1
                        | false, _ -> 1

                    attempts[request.DocumentVersionId] <- count

                    if interrupt && request.DocumentVersionId = secondId then
                        Task.FromException<ChunkingResult>(InvalidOperationException "simulated interruption")
                    else
                        inner.ChunkAsync request cancellationToken }

        let mutable ticks = 0L

        let runner =
            ChunkingMigration.create catalog (fun () ->
                ticks <- ticks + 1L
                now.AddTicks ticks)

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            run runner target [ secondVersion.Id; firstVersion.Id ] |> ignore)
        |> ignore

        let interrupted =
            catalog.SnapshotAsync authorization CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let checkpoint = interrupted.ChunkingMigrations |> List.exactlyOne
        Assert.AreEqual(1, checkpoint.NextDocumentIndex)
        Assert.AreEqual(1, checkpoint.Revision)
        Assert.IsTrue(checkpoint.CompletedAt.IsNone)

        let committedId = orderedIds[0]

        interrupted.Chunks
        |> List.filter (fun chunk -> chunk.DocumentVersionId = committedId)
        |> List.iter (fun chunk ->
            Assert.AreEqual(inner.Descriptor.Component, chunk.Chunker)
            Assert.AreEqual(inner.Descriptor.ConfigurationHash, chunk.ChunkerConfigurationHash))

        Assert.AreEqual(1, interrupted.IndexEntries.Length)
        Assert.AreEqual(1, interrupted.CacheEntries.Length)

        interrupt <- false
        let resumed = run runner target [ firstVersion.Id; secondVersion.Id ]

        Assert.AreEqual(1, resumed.MigratedDocuments.Length)
        Assert.AreEqual(secondId, resumed.MigratedDocuments.Head.DocumentVersionId)
        Assert.AreEqual(2, resumed.Checkpoint.NextDocumentIndex)
        Assert.AreEqual(2, resumed.Checkpoint.Revision)
        Assert.IsTrue(resumed.Checkpoint.CompletedAt.IsSome)
        Assert.AreEqual(1, attempts[orderedIds[0]])
        Assert.AreEqual(2, attempts[orderedIds[1]])

        let completed =
            catalog.SnapshotAsync authorization CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(2, completed.Chunks.Length)
        Assert.AreEqual(0, completed.IndexEntries.Length)
        Assert.AreEqual(0, completed.CacheEntries.Length)

        completed.Chunks
        |> List.iter (fun chunk ->
            Assert.AreEqual(inner.Descriptor.Component, chunk.Chunker)
            Assert.AreEqual(inner.Descriptor.ConfigurationHash, chunk.ChunkerConfigurationHash))

        let repeated = run runner target orderedIds
        Assert.AreEqual(0, repeated.MigratedDocuments.Length)
        Assert.AreEqual(resumed.Checkpoint, repeated.Checkpoint)

    [<TestMethod>]
    member _.``Migration rejects chunks outside the pinned target strategy without advancing``() =
        let catalog = KnowledgeCatalog.inMemory ()
        let version, _, _, original = bundle 1 "strategy mismatch"
        saveBundle catalog original

        let declared = StructureAwareChunker.create { MaximumParentCharacters = 1000 }

        let dishonest: KnowledgeChunker =
            { Descriptor = declared.Descriptor
              ChunkAsync =
                fun request _ ->
                    let old = original.Chunks |> List.exactlyOne

                    Task.FromResult(
                        { Chunks = [ old ]
                          Metrics = ChunkingMeasurement.measure request.Units [ old ] None 0 0L }
                    ) }

        let runner = ChunkingMigration.create catalog (fun () -> now.AddMinutes 1.0)

        Assert.ThrowsExactly<ArgumentException>(fun () -> run runner dishonest [ version.Id ] |> ignore)
        |> ignore

        let snapshot =
            catalog.SnapshotAsync authorization CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(0, snapshot.ChunkingMigrations.Length)
        CollectionAssert.AreEqual(original.Chunks |> List.toArray, snapshot.Chunks |> List.toArray)
        Assert.AreEqual(1, snapshot.IndexEntries.Length)
