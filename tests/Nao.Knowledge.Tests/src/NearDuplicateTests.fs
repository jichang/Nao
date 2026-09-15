namespace Nao.Knowledge.Tests

open System
open System.Threading
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass>]
type NearDuplicateTests() =
    let lifecycle = KnowledgeLifecycle.active None false
    let chunker = ComponentVersion.create "chunker" "1"
    let chunkerConfig = ContentHash.computeText "chunker-config"
    let documentId = KnowledgeDocumentId.parse "duplicate-fixture"

    let documentVersionId =
        KnowledgeDocumentVersionId.derive documentId 1 (ContentHash.computeText "document")

    let authorization =
        SecurityPrincipal.create (TenantId.parse "duplicate-tenant") (UserId.parse "duplicate-user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "duplicate-workspace") None
            |> Option.get

    let chunk sequence text =
        KnowledgeChunk.create
            documentVersionId
            sequence
            KnowledgeChunkRole.Leaf
            None
            [ ContentUnitId.derive documentVersionId sequence (ContentHash.computeText text) ]
            text
            [ SourceLocation.create
                  (int64 (sequence * 100))
                  (int64 text.Length)
                  (sequence * 100)
                  text.Length
                  None
                  None
                  None
                  (Some $"body/{sequence}") ]
            chunker
            chunkerConfig
            []
            Map.empty
            lifecycle

    let config =
        { Tokenizer = UnicodeWordTokenizer.create ()
          ShingleTokens = 2
          SimilarityThreshold = Confidence.create 0.3
          MaximumChunks = 100
          MaximumCandidatePairs = 100
          MaximumDetectedPairs = 100 }

    let detect detector chunks =
        detector.DetectAsync
            { Authorization = authorization
              Chunks = chunks
              DetectedAt = DateTimeOffset(2026, 9, 15, 1, 30, 0, TimeSpan.Zero) }
            CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Normalized exact duplicates have deterministic canonical identity``() =
        let first = chunk 0 "Nao provides secure agent tools."
        let second = chunk 1 "NAO provides secure agent tools!"
        let detector = ShingleNearDuplicateDetector.create config

        let forward = detect detector [ first; second ]
        let reversed = detect detector [ second; first ]
        let duplicate = forward.Duplicates |> List.exactlyOne

        Assert.AreEqual(ContentDuplicateKind.Exact, duplicate.Kind)
        Assert.AreEqual(1.0, Confidence.value duplicate.Similarity, 0.0001)
        CollectionAssert.AreEqual(forward.Duplicates |> List.toArray, reversed.Duplicates |> List.toArray)

        let expectedCanonical = [ first.Id; second.Id ] |> List.minBy KnowledgeChunkId.value

        Assert.AreEqual(expectedCanonical, duplicate.CanonicalChunkId)
        Assert.AreEqual(duplicate.Id, duplicate.Id |> NearDuplicateId.value |> NearDuplicateId.parse)

    [<TestMethod>]
    member _.``Shingle similarity detects near duplicates and ignores unrelated content``() =
        let first = chunk 0 "Nao provides secure agent tools"
        let second = chunk 1 "Nao provides reliable agent tools"
        let unrelated = chunk 2 "Gardens need water and sunlight"

        let result =
            detect (ShingleNearDuplicateDetector.create config) [ first; unrelated; second ]

        let duplicate = result.Duplicates |> List.exactlyOne

        Assert.AreEqual(ContentDuplicateKind.Near, duplicate.Kind)
        Assert.AreEqual(1.0 / 3.0, Confidence.value duplicate.Similarity, 0.0001)
        Assert.AreEqual(3, result.AnalyzedChunks)
        Assert.AreEqual(1, result.CandidatePairs)

    [<TestMethod>]
    member _.``Detector configuration participates in duplicate identity``() =
        let first = chunk 0 "Nao provides secure agent tools"
        let second = chunk 1 "Nao provides secure reliable agent tools"

        let bigrams = ShingleNearDuplicateDetector.create config

        let unigrams = ShingleNearDuplicateDetector.create { config with ShingleTokens = 1 }

        let firstDetection =
            (detect bigrams [ first; second ]).Duplicates |> List.exactlyOne

        let secondDetection =
            (detect unigrams [ first; second ]).Duplicates |> List.exactlyOne

        Assert.AreNotEqual(bigrams.Descriptor.ConfigurationHash, unigrams.Descriptor.ConfigurationHash)
        Assert.AreNotEqual(firstDetection.Id, secondDetection.Id)
        Assert.AreNotEqual(firstDetection.Similarity, secondDetection.Similarity)

    [<TestMethod>]
    member _.``Duplicate analysis fails closed at candidate and input limits``() =
        let chunks =
            [ chunk 0 "same repeated content"
              chunk 1 "same repeated content"
              chunk 2 "same repeated content" ]

        let candidateLimited =
            ShingleNearDuplicateDetector.create
                { config with
                    MaximumCandidatePairs = 2 }

        Assert.ThrowsExactly<InvalidOperationException>(fun () -> detect candidateLimited chunks |> ignore)
        |> ignore

        let inputLimited =
            ShingleNearDuplicateDetector.create { config with MaximumChunks = 2 }

        Assert.ThrowsExactly<ArgumentException>(fun () -> detect inputLimited chunks |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Punctuation-only chunks are not classified as duplicates``() =
        let result =
            detect (ShingleNearDuplicateDetector.create config) [ chunk 0 "..."; chunk 1 "!!!" ]

        Assert.AreEqual(0, result.CandidatePairs)
        Assert.AreEqual(0, result.Duplicates.Length)
