namespace Nao.Knowledge.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type MetadataFilteredRetrievalTests() =
    let now = DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero)
    let namespaceName = KnowledgeIndexNamespace.parse "primary"
    let sourceId = KnowledgeSourceId.parse "source"
    let otherSourceId = KnowledgeSourceId.parse "other-source"

    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

    let version suffix =
        let contentHash = ContentHash.computeText ("version-" + suffix)
        KnowledgeDocumentVersionId.derive (KnowledgeDocumentId.parse ("document-" + suffix)) 1 contentHash

    let expectedVersion = version "expected"

    let filter =
        KnowledgeIndexFilter.create
            [ sourceId ]
            [ expectedVersion ]
            (Map.ofList [ "chunk.section", "intro" ])
            (Some(now.AddHours -1.0))
            (Some(now.AddHours 1.0))
            (Some(now.AddHours -2.0))
            (Some(now.AddHours 2.0))

    let document source documentVersion metadata sourceModifiedAt ingestedAt suffix =
        let text = "content-" + suffix
        let contentHash = ContentHash.computeText text
        let chunkId = KnowledgeChunkId.derive documentVersion "test-chunker" 0 contentHash

        KnowledgeIndexDocument.restore
            namespaceName
            (AuthorizationScope.identity authorization)
            KnowledgeIndexAccess.ownerOnly
            source
            documentVersion
            chunkId
            KnowledgeChunkRole.Leaf
            None
            contentHash
            text
            metadata
            sourceModifiedAt
            ingestedAt
            ingestedAt

    let understanding =
        KnowledgeRetrievalRequest.create
            "query"
            authorization
            namespaceName
            filter
            KnowledgeTaskIntent.KnowledgeSeeking
            KnowledgeRetrievalStrategy.Lexical
            KnowledgeQueryTransformationStrategy.None
            (KnowledgeRetrievalLimits.create 100 200 1 5 5)
            KnowledgeRetrievalBudget.deterministic
        |> KnowledgeQueryNormalizer.normalize
        |> fun normalized -> KnowledgeQueryUnderstanding.planAsync None normalized CancellationToken.None
        |> _.GetAwaiter().GetResult()

    let index hit =
        { ApplyAsync =
            fun _ mutation _ ->
                Task.FromResult(
                    KnowledgeIndexMutationResult.create (KnowledgeIndexMutation.namespaceName mutation) 0 0 0 now
                )
          QueryAsync =
            fun actualAuthorization query _ ->
                Assert.AreSame(authorization, actualAuthorization)
                Assert.AreSame(filter, LexicalIndexQuery.filter query)
                Task.FromResult [ LexicalIndexHit.create hit 1.0 ] }

    let execute hit =
        KnowledgeLexicalRetrievalExecutor.executeAsync (index hit) understanding CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Retrieval applies and evidences the complete typed filter``() =
        let matching =
            document
                sourceId
                expectedVersion
                (Map.ofList [ "chunk.section", "intro"; "source.system", "docs" ])
                (Some now)
                now
                "matching"

        match execute matching with
        | KnowledgeLexicalRetrievalResult.Skipped _ -> Assert.Fail "Filtered retrieval was unexpectedly skipped."
        | KnowledgeLexicalRetrievalResult.Retrieved retrieval ->
            let evidence = KnowledgeLexicalRetrieval.filterEvidence retrieval

            Assert.AreEqual(
                ComponentVersion.create "nao-retrieval-filter-boundary" "1",
                KnowledgeRetrievalFilterEvidence.boundary evidence
            )

            Assert.AreSame(filter, KnowledgeRetrievalFilterEvidence.filter evidence)
            Assert.AreEqual(1, KnowledgeRetrievalFilterEvidence.validatedCandidates evidence)

    [<TestMethod>]
    member _.``Retrieval rejects every typed filter dimension when an adapter widens results``() =
        let cases =
            [ document otherSourceId expectedVersion (Map.ofList [ "chunk.section", "intro" ]) (Some now) now "source"
              document sourceId (version "other") (Map.ofList [ "chunk.section", "intro" ]) (Some now) now "version"
              document sourceId expectedVersion (Map.ofList [ "chunk.section", "appendix" ]) (Some now) now "metadata"
              document sourceId expectedVersion (Map.ofList [ "chunk.section", "intro" ]) None now "missing-modified"
              document
                  sourceId
                  expectedVersion
                  (Map.ofList [ "chunk.section", "intro" ])
                  (Some(now.AddHours -2.0))
                  now
                  "modified-before"
              document
                  sourceId
                  expectedVersion
                  (Map.ofList [ "chunk.section", "intro" ])
                  (Some now)
                  (now.AddHours 3.0)
                  "ingested-after" ]

        for widened in cases do
            Assert.ThrowsExactly<InvalidOperationException>(fun () -> execute widened |> ignore)
            |> ignore
