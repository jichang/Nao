namespace Nao.Knowledge.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass>]
type KnowledgeContractTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

    let owner = AuthorizationScope.identity authorization

    let now = DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero)
    let lifecycle = KnowledgeLifecycle.active None false
    let sourceId = KnowledgeSourceId.parse "docs"
    let documentId = KnowledgeDocumentId.parse "guide"
    let sourceUri = Uri("https://example.test/docs/guide")

    let trust =
        TrustAssessment.create KnowledgeTrust.OrganizationVerified "repository review" now

    let source =
        KnowledgeSource.create sourceId sourceUri owner DataClassification.Internal trust Map.empty now lifecycle

    let document =
        KnowledgeDocument.create documentId sourceId sourceUri Map.empty now lifecycle

    let connector = ComponentVersion.create "filesystem" "1"

    let version number (content: string) =
        let bytes = Text.Encoding.UTF8.GetBytes content

        KnowledgeDocumentVersion.create
            documentId
            sourceId
            number
            (ContentHash.computeBytes bytes)
            (SourceAcquisition.create connector (ContentHash.computeBytes bytes) now Map.empty)
            "text/markdown"
            (Some "en")
            (int64 bytes.Length)
            (Some now)
            now
            DataClassification.Internal
            trust
            Map.empty
            lifecycle

    let location = SourceLocation.create 0L 12L 0 12 None (Some 1) (Some 1) None
    let parser = ComponentVersion.create "markdown-parser" "1"
    let chunker = ComponentVersion.create "token-window" "1"

    [<TestMethod>]
    member _.``Content hashes and derived IDs are canonical and deterministic``() =
        let first = version 1 "stable content"
        let second = version 1 "stable content"

        Assert.AreEqual(first.ContentHash, second.ContentHash)
        Assert.AreEqual(first.Id, second.Id)
        Assert.AreEqual(first.Id, first.Id |> KnowledgeDocumentVersionId.value |> KnowledgeDocumentVersionId.parse)
        Assert.AreEqual(64, first.ContentHash |> ContentHash.value |> String.length)
        Assert.AreEqual(1, KnowledgeSchemaVersion.value first.SchemaVersion)

    [<TestMethod>]
    member _.``Reprocessing creates a distinct immutable document version``() =
        let first = version 1 "first content"
        let second = version 2 "second content"

        Assert.AreNotEqual(first.Id, second.Id)
        Assert.AreNotEqual(first.ContentHash, second.ContentHash)
        Assert.AreEqual(1, first.Version)
        Assert.AreEqual(2, second.Version)

    [<TestMethod>]
    member _.``Retrieved passage traces to exact source version and location``() =
        let documentVersion = version 1 "grounded passage"

        let unit =
            ContentUnit.create
                documentVersion.Id
                0
                ContentUnitKind.Paragraph
                "grounded passage"
                location
                parser
                []
                Map.empty
                lifecycle

        let chunk =
            KnowledgeChunk.create documentVersion.Id 0 [ unit.Id ] unit.Text [ location ] chunker [] Map.empty lifecycle

        let resultId = RetrievalResultId.generate ()

        let citation =
            Citation.create resultId source document documentVersion chunk location chunk.Text now

        let passage =
            RetrievedPassage.create chunk citation 0.91 (Some(Confidence.create 0.8)) "vector" Map.empty

        let result = RetrievalResult.create resultId "query" now [ passage ] []

        let retrieved = result.Passages |> List.exactlyOne
        Assert.AreEqual(source.Id, retrieved.Citation.SourceId)
        Assert.AreEqual(document.Id, retrieved.Citation.DocumentId)
        Assert.AreEqual(documentVersion.Id, retrieved.Citation.DocumentVersionId)
        Assert.AreEqual(chunk.Id, retrieved.Citation.ChunkId)
        Assert.AreEqual(location, retrieved.Citation.Location)

    [<TestMethod>]
    member _.``Citation rejects a chunk from another document version``() =
        let firstVersion = version 1 "first"
        let secondVersion = version 2 "second"

        let unit =
            ContentUnit.create
                firstVersion.Id
                0
                ContentUnitKind.Paragraph
                "first"
                location
                parser
                []
                Map.empty
                lifecycle

        let chunk =
            KnowledgeChunk.create firstVersion.Id 0 [ unit.Id ] unit.Text [ location ] chunker [] Map.empty lifecycle

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            Citation.create (RetrievalResultId.generate ()) source document secondVersion chunk location chunk.Text now
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Citation rejects text or location outside the cited chunk``() =
        let documentVersion = version 1 "first"

        let unit =
            ContentUnit.create
                documentVersion.Id
                0
                ContentUnitKind.Paragraph
                "first"
                location
                parser
                []
                Map.empty
                lifecycle

        let chunk =
            KnowledgeChunk.create documentVersion.Id 0 [ unit.Id ] unit.Text [ location ] chunker [] Map.empty lifecycle

        let resultId = RetrievalResultId.generate ()

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            Citation.create resultId source document documentVersion chunk location "not present" now
            |> ignore)
        |> ignore

        let otherLocation = SourceLocation.create 1L 1L 1 1 None (Some 1) (Some 1) None

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            Citation.create resultId source document documentVersion chunk otherLocation "first" now
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Legal hold prevents tombstone and purge transitions``() =
        let held = KnowledgeLifecycle.active None true

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeLifecycle.tombstone now "request" held |> ignore)
        |> ignore

        Assert.ThrowsExactly<InvalidOperationException>(fun () -> KnowledgeLifecycle.purge now held |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Lifecycle requires tombstone before purge``() =
        Assert.ThrowsExactly<InvalidOperationException>(fun () -> KnowledgeLifecycle.purge now lifecycle |> ignore)
        |> ignore

        let tombstoned = KnowledgeLifecycle.tombstone now "source deleted" lifecycle
        let purged = KnowledgeLifecycle.purge (now.AddDays 1.0) tombstoned

        match purged.DeletionStatus with
        | KnowledgeDeletionStatus.Purged deletedAt -> Assert.AreEqual(now.AddDays 1.0, deletedAt)
        | status -> Assert.Fail($"Expected purged lifecycle, got {status}.")

    [<TestMethod>]
    member _.``Invalid scalar and location values fail closed``() =
        Assert.IsTrue(ContentHash.tryParse(String('A', 64)).IsNone)
        Assert.IsTrue(Confidence.tryCreate Double.NaN |> Option.isNone)
        Assert.IsTrue(KnowledgeChunkId.tryParse "chunk_not-a-hash" |> Option.isNone)

        Assert.ThrowsExactly<ArgumentException>(fun () -> KnowledgeSchemaVersion.parse 2 |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            SourceLocation.create -1L 1L 0 1 None None None None |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () -> KnowledgeSourceId.parse " padded " |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Document version rejects acquisition hash substitution``() =
        let expected = ContentHash.computeText "expected"

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeDocumentVersion.create
                documentId
                sourceId
                1
                expected
                (SourceAcquisition.create connector (ContentHash.computeText "substituted") now Map.empty)
                "text/plain"
                None
                8L
                None
                now
                DataClassification.Internal
                trust
                Map.empty
                lifecycle
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Source deletion propagates atomically through indexes and caches``() =
        let documentVersion = version 1 "indexed passage"

        let unit =
            ContentUnit.create
                documentVersion.Id
                0
                ContentUnitKind.Paragraph
                "indexed passage"
                location
                parser
                []
                Map.empty
                lifecycle

        let chunk =
            KnowledgeChunk.create documentVersion.Id 0 [ unit.Id ] unit.Text [ location ] chunker [] Map.empty lifecycle

        let indexEntry = KnowledgeIndexEntry.create "primary" chunk None now []

        let cacheEntry =
            KnowledgeCacheEntry.create
                (RetrievalResultId.generate ())
                "query"
                source.Id
                documentVersion.Id
                [ chunk.Id ]
                now
                (now.AddHours 1.0)
                lifecycle

        let catalog = KnowledgeCatalog.inMemory ()

        catalog.SaveVersionAsync
            authorization
            { Source = source
              Document = document
              Version = documentVersion
              ContentUnits = [ unit ]
              Chunks = [ chunk ]
              IndexEntries = [ indexEntry ] }
            Threading.CancellationToken.None
        |> _.GetAwaiter().GetResult()

        catalog.SaveCacheEntryAsync authorization cacheEntry Threading.CancellationToken.None
        |> _.GetAwaiter().GetResult()

        let evidence =
            catalog.TombstoneSourceAsync
                authorization
                source.Id
                (now.AddMinutes 1.0)
                "source removed"
                Threading.CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(1, evidence.Documents)
        Assert.AreEqual(1, evidence.Versions)
        Assert.AreEqual(1, evidence.ContentUnits)
        Assert.AreEqual(1, evidence.Chunks)
        Assert.AreEqual(1, evidence.IndexEntries)
        Assert.AreEqual(1, evidence.CacheEntries)

        let snapshot =
            catalog.SnapshotAsync authorization Threading.CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let statuses =
            [ snapshot.Sources |> List.exactlyOne |> _.Lifecycle.DeletionStatus
              snapshot.Documents |> List.exactlyOne |> _.Lifecycle.DeletionStatus
              snapshot.Versions |> List.exactlyOne |> _.Lifecycle.DeletionStatus
              snapshot.ContentUnits |> List.exactlyOne |> _.Lifecycle.DeletionStatus
              snapshot.Chunks |> List.exactlyOne |> _.Lifecycle.DeletionStatus
              snapshot.IndexEntries |> List.exactlyOne |> _.Lifecycle.DeletionStatus
              snapshot.CacheEntries |> List.exactlyOne |> _.Lifecycle.DeletionStatus ]

        statuses
        |> List.iter (function
            | KnowledgeDeletionStatus.Tombstoned _ -> ()
            | status -> Assert.Fail($"Expected tombstone, got {status}."))

    [<TestMethod>]
    member _.``Knowledge catalog partitions complete authorization identity``() =
        let catalog = KnowledgeCatalog.inMemory ()
        let documentVersion = version 1 "private"

        catalog.SaveVersionAsync
            authorization
            { Source = source
              Document = document
              Version = documentVersion
              ContentUnits = []
              Chunks = []
              IndexEntries = [] }
            Threading.CancellationToken.None
        |> _.GetAwaiter().GetResult()

        let otherAuthorization =
            SecurityPrincipal.create (TenantId.parse "other-tenant") (UserId.parse "user") []
            |> fun principal ->
                AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
                |> Option.get

        let snapshot =
            catalog.SnapshotAsync otherAuthorization Threading.CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(0, snapshot.Sources.Length)

        Assert.ThrowsExactly<Collections.Generic.KeyNotFoundException>(fun () ->
            catalog.TombstoneSourceAsync
                otherAuthorization
                source.Id
                now
                "cross-owner"
                Threading.CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Legal hold prevents every deletion mutation atomically``() =
        let catalog = KnowledgeCatalog.inMemory ()
        let documentVersion = version 1 "held passage"
        let heldLifecycle = KnowledgeLifecycle.active None true

        let unit =
            ContentUnit.create
                documentVersion.Id
                0
                ContentUnitKind.Paragraph
                "held passage"
                location
                parser
                []
                Map.empty
                heldLifecycle

        let chunk =
            KnowledgeChunk.create documentVersion.Id 0 [ unit.Id ] unit.Text [ location ] chunker [] Map.empty lifecycle

        catalog.SaveVersionAsync
            authorization
            { Source = source
              Document = document
              Version = documentVersion
              ContentUnits = [ unit ]
              Chunks = [ chunk ]
              IndexEntries = [] }
            Threading.CancellationToken.None
        |> _.GetAwaiter().GetResult()

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            catalog.TombstoneSourceAsync authorization source.Id now "blocked" Threading.CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let snapshot =
            catalog.SnapshotAsync authorization Threading.CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(
            KnowledgeDeletionStatus.Active,
            snapshot.Sources |> List.exactlyOne |> _.Lifecycle.DeletionStatus
        )

        Assert.AreEqual(
            KnowledgeDeletionStatus.Active,
            snapshot.Chunks |> List.exactlyOne |> _.Lifecycle.DeletionStatus
        )

    [<TestMethod>]
    member _.``Cancelled catalog mutation has no side effects``() =
        let catalog = KnowledgeCatalog.inMemory ()
        use cancellation = new Threading.CancellationTokenSource()
        cancellation.Cancel()

        Assert.ThrowsExactly<OperationCanceledException>(fun () ->
            catalog.SaveVersionAsync
                authorization
                { Source = source
                  Document = document
                  Version = version 1 "cancelled"
                  ContentUnits = []
                  Chunks = []
                  IndexEntries = [] }
                cancellation.Token
            |> _.GetAwaiter().GetResult())
        |> ignore

        let snapshot =
            catalog.SnapshotAsync authorization Threading.CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(0, snapshot.Sources.Length)
