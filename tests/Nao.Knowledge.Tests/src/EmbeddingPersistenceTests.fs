namespace Nao.Knowledge.Tests

open System
open System.Threading
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type EmbeddingPersistenceTests() =
    let now = DateTimeOffset(2026, 9, 15, 4, 0, 0, TimeSpan.Zero)

    let authorization tenant =
        SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse "embedding-user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "embedding-workspace") None
            |> Option.get

    let ownerAuthorization = authorization "embedding-owner"

    let documentVersionId =
        KnowledgeDocumentVersionId.derive
            (KnowledgeDocumentId.parse "persisted-embedding-document")
            1
            (ContentHash.computeText "persisted-embedding-document")

    let profile =
        EmbeddingCapabilityDescriptor.create
            (ComponentVersion.create "fixture-embedding-model" "2026-09")
            2
            EmbeddingNormalization.UnitLength
            VectorDistanceMetric.Cosine
            (ComponentVersion.create "fixture-preprocessing" "1")
            (EmbeddingCapabilityLimits.create 8 1000 256 1024)

    let generated sequence text values =
        let contentHash = ContentHash.computeText text

        { ChunkId = KnowledgeChunkId.derive documentVersionId "embedding-persistence" sequence contentHash
          ContentHash = contentHash
          ProfileId = profile.ProfileId
          Values = values
          GeneratedAt = now }

    let save store values =
        EmbeddingPersistence.saveAsync store ownerAuthorization profile values CancellationToken.None
        |> _.GetAwaiter().GetResult()

    let load store authorization id =
        store.TryGetAsync authorization id CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Persistence validates dimensions before an atomic batch write``() =
        let store = KnowledgeEmbeddingStores.inMemory ()
        let valid = generated 0 "valid" [| 1.0; 2.0 |]
        let invalid = generated 1 "invalid" [| 1.0 |]
        let validId = PersistedEmbeddingId.derive valid.ChunkId profile.ProfileId

        Assert.ThrowsExactly<ArgumentException>(fun () -> save store [ valid; invalid ])
        |> ignore

        Assert.IsTrue((load store ownerAuthorization validId).IsNone)

        let nonFinite = generated 2 "non-finite" [| Double.NaN; 1.0 |]

        Assert.ThrowsExactly<ArgumentException>(fun () -> save store [ nonFinite ])
        |> ignore

    [<TestMethod>]
    member _.``Persisted vectors are immutable and isolated by authorization identity``() =
        let store = KnowledgeEmbeddingStores.inMemory ()
        let values = [| 0.25; 0.75 |]
        let item = generated 0 "immutable" values
        let embeddingId = PersistedEmbeddingId.derive item.ChunkId profile.ProfileId

        save store [ item ]
        values[0] <- 99.0

        let persisted =
            load store ownerAuthorization embeddingId
            |> Option.defaultWith (fun () ->
                Assert.Fail "Expected persisted embedding."
                Unchecked.defaultof<PersistedEmbedding>)

        Assert.AreEqual(PersistedEmbedding.currentVersion, PersistedEmbedding.version persisted)
        Assert.AreEqual(profile, PersistedEmbedding.profile persisted)
        CollectionAssert.AreEqual([| 0.25; 0.75 |], PersistedEmbedding.values persisted)

        let exposed = PersistedEmbedding.values persisted
        exposed[0] <- 42.0

        let reloaded =
            load store ownerAuthorization embeddingId
            |> Option.defaultWith (fun () ->
                Assert.Fail "Expected persisted embedding."
                Unchecked.defaultof<PersistedEmbedding>)

        CollectionAssert.AreEqual([| 0.25; 0.75 |], PersistedEmbedding.values reloaded)
        Assert.IsTrue((load store (authorization "other-owner") embeddingId).IsNone)
        Assert.AreEqual(embeddingId, embeddingId |> PersistedEmbeddingId.value |> PersistedEmbeddingId.parse)

    [<TestMethod>]
    member _.``Persisted embedding identities cannot be rebound to different vectors``() =
        let store = KnowledgeEmbeddingStores.inMemory ()
        let first = generated 0 "stable" [| 1.0; 0.0 |]
        let changed = generated 0 "stable" [| 0.0; 1.0 |]

        let replayedLater =
            { first with
                GeneratedAt = now.AddMinutes 1.0 }

        save store [ first ]
        save store [ replayedLater ]

        Assert.ThrowsExactly<InvalidOperationException>(fun () -> save store [ changed ])
        |> ignore

        let embeddingId = PersistedEmbeddingId.derive first.ChunkId profile.ProfileId

        let persisted =
            load store ownerAuthorization embeddingId
            |> Option.defaultWith (fun () ->
                Assert.Fail "Expected persisted embedding."
                Unchecked.defaultof<PersistedEmbedding>)

        Assert.AreEqual(now, PersistedEmbedding.generatedAt persisted)
