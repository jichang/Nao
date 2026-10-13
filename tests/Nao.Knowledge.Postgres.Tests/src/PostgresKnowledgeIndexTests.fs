namespace Nao.Knowledge.Postgres.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge
open Nao.Knowledge.Postgres
open Npgsql

[<TestClass>]
type PostgresKnowledgeIndexTests() =
    let connectionString =
        Environment.GetEnvironmentVariable "NAO_TEST_POSTGRES"
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let now = DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero)
    let tenant = TenantId.parse "tenant-a"
    let workspace = WorkspaceId.parse "workspace-a"
    let ownerUser = UserId.parse "owner"
    let readerUser = UserId.parse "reader"
    let readerGroup = GroupId.parse "readers"
    let namespaceName = KnowledgeIndexNamespace.parse "primary"
    let lifecycle = KnowledgeLifecycle.active None false
    let testComponent = ComponentVersion.create "test" "1"

    let profile =
        EmbeddingCapabilityDescriptor.create
            (ComponentVersion.create "embedding-model" "1")
            3
            EmbeddingNormalization.UnitLength
            VectorDistanceMetric.Cosine
            (ComponentVersion.create "preprocessing" "1")
            (EmbeddingCapabilityLimits.create 8 1000 100 800)

    let principal user groups roles =
        SecurityPrincipal.createAuthenticated tenant user None groups roles Map.empty AuthenticationStrength.MultiFactor

    let authorization user groups roles =
        AuthorizationScope.tryCreate (principal user groups roles) None workspace None
        |> Option.get

    let createRecordForProfile embeddingProfile owner sourceName text section access values =
        let ownerIdentity = AuthorizationScope.identity owner

        let source =
            KnowledgeSource.create
                (KnowledgeSourceId.parse sourceName)
                (Uri($"https://example.test/{sourceName}"))
                ownerIdentity
                DataClassification.Internal
                (TrustAssessment.create KnowledgeTrust.OrganizationVerified "test" now)
                (Map.ofList [ "system", "docs" ])
                now
                lifecycle

        let documentId = KnowledgeDocumentId.parse ("document-" + sourceName)
        let contentHash = ContentHash.computeText text

        let version =
            KnowledgeDocumentVersion.create
                documentId
                source.Id
                1
                contentHash
                (SourceAcquisition.create testComponent contentHash now Map.empty)
                "text/plain"
                (Some "en")
                (int64 text.Length)
                (Some now)
                now
                DataClassification.Internal
                source.Trust
                (Map.ofList [ "release", "current" ])
                lifecycle

        let location =
            SourceLocation.create 0L (int64 text.Length) 0 text.Length None (Some 1) (Some 1) None

        let unit =
            ContentUnit.create version.Id 0 ContentUnitKind.Paragraph text location testComponent [] Map.empty lifecycle

        let chunk =
            let parentChunkId =
                KnowledgeChunkId.derive version.Id "parent-fixture" 0 (ContentHash.computeText "parent")

            KnowledgeChunk.create
                version.Id
                0
                KnowledgeChunkRole.Leaf
                (Some parentChunkId)
                [ unit.Id ]
                text
                [ location ]
                testComponent
                (ContentHash.computeText "chunker")
                []
                (Map.ofList [ "section", section ])
                lifecycle

        let document =
            KnowledgeIndexDocument.create namespaceName access source version chunk now

        let embedding =
            PersistedEmbedding.create
                ownerIdentity
                embeddingProfile
                { ChunkId = chunk.Id
                  ContentHash = chunk.ContentHash
                  ProfileId = embeddingProfile.ProfileId
                  Values = values
                  GeneratedAt = now }

        VectorIndexRecord.create document embedding, source.Id, chunk.Id

    let createRecord owner sourceName text section access values =
        createRecordForProfile profile owner sourceName text section access values

    let query (authorization: AuthorizationScope) (index: KnowledgeVectorIndex) values filter : VectorIndexHit list =
        index.QueryAsync
            authorization
            (VectorIndexQuery.create namespaceName profile values filter 10)
            CancellationToken.None
        |> _.GetAwaiter().GetResult()

    let lexicalQuery
        (authorization: AuthorizationScope)
        (index: KnowledgeLexicalIndex)
        queryText
        filter
        : LexicalIndexHit list =
        index.QueryAsync
            authorization
            (LexicalIndexQuery.create namespaceName queryText filter 10)
            CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    [<TestCategory("ExternalDatabase")>]
    [<TestCategory("Container")>]
    member _.``Pgvector applies ACL filters ranking and deletion inside PostgreSQL``() =
        match connectionString with
        | None -> Assert.Inconclusive "Set NAO_TEST_POSTGRES to run pgvector integration tests."
        | Some connectionString ->
            let schema = "nao_test_" + Guid.NewGuid().ToString("N").Substring(0, 16)
            let config = PgvectorIndexConfig.create schema 8 32
            use dataSource = PostgresKnowledgeDataSource.create connectionString

            try
                let index =
                    PgvectorKnowledgeIndex.createAsync dataSource profile config CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                let owner = authorization ownerUser [] Set.empty

                let sharedAccess =
                    KnowledgeIndexAccess.create [ readerUser ] [ readerGroup ] [ "knowledge-reader" ]

                let first, firstSourceId, firstChunkId =
                    createRecord owner "source-one" "alpha content" "intro" sharedAccess [| 1.0; 0.0; 0.0 |]

                let second, _, secondChunkId =
                    createRecord
                        owner
                        "source-two"
                        "beta content"
                        "appendix"
                        KnowledgeIndexAccess.ownerOnly
                        [| 0.0; 1.0; 0.0 |]

                let invalid, _, _ =
                    createRecord
                        owner
                        "source-invalid"
                        "invalid\u0000content"
                        "invalid"
                        KnowledgeIndexAccess.ownerOnly
                        [| 0.0; 0.0; 1.0 |]

                Assert.ThrowsExactly<PostgresException>(fun () ->
                    index.ApplyAsync
                        owner
                        (KnowledgeIndexMutation.create namespaceName [ first; invalid ] [] None [])
                        CancellationToken.None
                    |> _.GetAwaiter().GetResult()
                    |> ignore)
                |> ignore

                Assert.AreEqual(0, query owner index [| 1.0; 0.0; 0.0 |] KnowledgeIndexFilter.none |> List.length)

                let initialMutation =
                    KnowledgeIndexMutation.create namespaceName [ first; second ] [] None []

                let initialResult =
                    index.ApplyAsync owner initialMutation CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                Assert.AreEqual(2, KnowledgeIndexMutationResult.upsertedRecords initialResult)
                Assert.AreEqual(0, KnowledgeIndexMutationResult.deletedRecords initialResult)

                let ownerHits = query owner index [| 0.99; 0.01; 0.0 |] KnowledgeIndexFilter.none
                Assert.AreEqual(2, ownerHits.Length)

                Assert.AreEqual(
                    firstChunkId,
                    ownerHits
                    |> List.head
                    |> VectorIndexHit.document
                    |> KnowledgeIndexDocument.chunkId
                )

                let firstDocument = first |> VectorIndexRecord.document
                let returnedDocument = ownerHits |> List.head |> VectorIndexHit.document
                Assert.AreEqual(KnowledgeChunkRole.Leaf, KnowledgeIndexDocument.role returnedDocument)

                Assert.AreEqual(
                    KnowledgeIndexDocument.documentId firstDocument,
                    KnowledgeIndexDocument.documentId returnedDocument
                )

                Assert.AreEqual(
                    KnowledgeIndexDocument.documentVersion firstDocument,
                    KnowledgeIndexDocument.documentVersion returnedDocument
                )

                Assert.AreEqual(
                    KnowledgeIndexDocument.sourceTrust firstDocument,
                    KnowledgeIndexDocument.sourceTrust returnedDocument
                )

                Assert.AreEqual(
                    KnowledgeIndexDocument.versionTrust firstDocument,
                    KnowledgeIndexDocument.versionTrust returnedDocument
                )

                Assert.AreEqual(
                    KnowledgeIndexDocument.parentChunkId firstDocument,
                    KnowledgeIndexDocument.parentChunkId returnedDocument
                )

                CollectionAssert.AreEqual(
                    KnowledgeIndexDocument.locations firstDocument |> List.toArray,
                    KnowledgeIndexDocument.locations returnedDocument |> List.toArray
                )

                let rebuildResult =
                    KnowledgeIndexRebuild.vectorAsync index owner namespaceName [ first; second ] CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                Assert.AreEqual(2L, KnowledgeIndexRebuildResult.previousRecords rebuildResult)
                Assert.AreEqual(2L, KnowledgeIndexRebuildResult.currentRecords rebuildResult)

                let rebuiltHits = query owner index [| 0.99; 0.01; 0.0 |] KnowledgeIndexFilter.none
                Assert.AreEqual(ownerHits, rebuiltHits)

                let reader = authorization readerUser [] Set.empty
                let readerHits = query reader index [| 1.0; 0.0; 0.0 |] KnowledgeIndexFilter.none
                Assert.AreEqual(1, readerHits.Length)

                let outsider = authorization (UserId.parse "outsider") [] Set.empty

                Assert.AreEqual(
                    0,
                    query outsider index [| 1.0; 0.0; 0.0 |] KnowledgeIndexFilter.none
                    |> List.length
                )

                let metadataFilter =
                    KnowledgeIndexFilter.create [] [] (Map.ofList [ "chunk.section", "appendix" ]) None None None None

                let filtered = query owner index [| 1.0; 0.0; 0.0 |] metadataFilter
                Assert.AreEqual(1, filtered.Length)

                Assert.AreEqual(
                    secondChunkId,
                    filtered
                    |> List.head
                    |> VectorIndexHit.document
                    |> KnowledgeIndexDocument.chunkId
                )

                Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
                    index.ApplyAsync
                        reader
                        (KnowledgeIndexMutation.create namespaceName [ first ] [] None [])
                        CancellationToken.None
                    |> _.GetAwaiter().GetResult()
                    |> ignore)
                |> ignore

                let sourceFilter =
                    KnowledgeIndexFilter.create [ firstSourceId ] [] Map.empty None None None None

                let replacement =
                    KnowledgeIndexMutation.create namespaceName [ second ] [] (Some sourceFilter) []

                let replacementResult =
                    index.ApplyAsync owner replacement CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                Assert.AreEqual(1, KnowledgeIndexMutationResult.upsertedRecords replacementResult)
                Assert.AreEqual(1, KnowledgeIndexMutationResult.deletedRecords replacementResult)

                let remaining = query owner index [| 1.0; 0.0; 0.0 |] KnowledgeIndexFilter.none
                Assert.AreEqual(1, remaining.Length)

                Assert.AreEqual(
                    secondChunkId,
                    remaining
                    |> List.head
                    |> VectorIndexHit.document
                    |> KnowledgeIndexDocument.chunkId
                )

                let secondSourceId =
                    second |> VectorIndexRecord.document |> KnowledgeIndexDocument.sourceId

                let retirement =
                    KnowledgeIndexMutation.create namespaceName [] [] None [ secondSourceId ]

                let retirementResult =
                    index.ApplyAsync owner retirement CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                Assert.AreEqual(1, KnowledgeIndexMutationResult.deletedRecords retirementResult)
                Assert.AreEqual(1, KnowledgeIndexMutationResult.retiredSources retirementResult)
                Assert.AreEqual(0, query owner index [| 0.0; 1.0; 0.0 |] KnowledgeIndexFilter.none |> List.length)

                let retryResult =
                    index.ApplyAsync owner retirement CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                Assert.AreEqual(0, KnowledgeIndexMutationResult.deletedRecords retryResult)
                Assert.AreEqual(1, KnowledgeIndexMutationResult.retiredSources retryResult)

                Assert.ThrowsExactly<InvalidOperationException>(fun () ->
                    index.ApplyAsync
                        owner
                        (KnowledgeIndexMutation.create namespaceName [ second ] [] None [])
                        CancellationToken.None
                    |> _.GetAwaiter().GetResult()
                    |> ignore)
                |> ignore
            finally
                use connection = new NpgsqlConnection(connectionString)
                connection.Open()
                use command = connection.CreateCommand()
                command.CommandText <- $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"
                command.ExecuteNonQuery() |> ignore

    [<TestMethod>]
    [<TestCategory("ExternalDatabase")>]
    [<TestCategory("Container")>]
    member _.``PostgreSQL adapters drive authorized hybrid retrieval and verified citations``() =
        match connectionString with
        | None -> Assert.Inconclusive "Set NAO_TEST_POSTGRES to run PostgreSQL retrieval qualification."
        | Some connectionString ->
            let schema = "nao_qualification_" + Guid.NewGuid().ToString("N").Substring(0, 16)
            let vectorConfig = PgvectorIndexConfig.create schema 8 32
            let lexicalConfig = PostgresLexicalIndexConfig.create schema "pg_catalog.simple"
            use dataSource = PostgresKnowledgeDataSource.create connectionString

            try
                let vectorIndex =
                    PgvectorKnowledgeIndex.createAsync dataSource profile vectorConfig CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                let lexicalIndex =
                    PostgresLexicalKnowledgeIndex.createAsync dataSource lexicalConfig CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                let owner = authorization ownerUser [] Set.empty

                let relevant, relevantSourceId, relevantChunkId =
                    createRecord
                        owner
                        "qualified-release-policy"
                        "The release policy requires two reviewers."
                        "policy"
                        KnowledgeIndexAccess.ownerOnly
                        [| 1.0; 0.0; 0.0 |]

                let unrelated, _, _ =
                    createRecord
                        owner
                        "qualified-cafeteria-menu"
                        "The cafeteria serves soup on Friday."
                        "menu"
                        KnowledgeIndexAccess.ownerOnly
                        [| 0.0; 1.0; 0.0 |]

                let vectorRecords = [ relevant; unrelated ]

                vectorIndex.ApplyAsync
                    owner
                    (KnowledgeIndexMutation.create namespaceName vectorRecords [] None [])
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()
                |> ignore

                let documents = vectorRecords |> List.map VectorIndexRecord.document

                lexicalIndex.ApplyAsync
                    owner
                    (KnowledgeIndexMutation.create namespaceName documents [] None [])
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()
                |> ignore

                let embedder: KnowledgeQueryEmbedder =
                    { Profile = profile
                      EmbedQueriesAsync =
                        fun _ request _ ->
                            let embeddings =
                                request
                                |> KnowledgeQueryEmbeddingRequest.inputs
                                |> List.map (fun input ->
                                    KnowledgeQueryEmbedding.create
                                        (KnowledgeQueryEmbeddingInput.contentHash input)
                                        [| 1.0; 0.0; 0.0 |])

                            KnowledgeQueryEmbeddingResult.create
                                embeddings
                                (KnowledgeQueryEmbeddingUsage.create 1 embeddings.Length 0m)
                            |> Task.FromResult }

                let retrievalConfig =
                    KnowledgeHybridRetrievalConfig.create
                        KnowledgeReciprocalRankFusionConfig.defaultConfig
                        (KnowledgeRetrievalDiversityConfig.unrestricted 4)
                        None
                        None

                let retrievalRequest authorization =
                    KnowledgeRetrievalRequest.create
                        "release policy reviewers"
                        authorization
                        namespaceName
                        KnowledgeIndexFilter.none
                        KnowledgeTaskIntent.KnowledgeSeeking
                        KnowledgeRetrievalStrategy.Hybrid
                        KnowledgeQueryTransformationStrategy.None
                        (KnowledgeRetrievalLimits.create 100 200 2 2 4)
                        (KnowledgeRetrievalBudget.create (TimeSpan.FromSeconds 5.0) 0 1 10 0m)

                let retrieve authorization =
                    KnowledgeHybridRetrievalPipeline.executeAsync
                        None
                        lexicalIndex
                        embedder
                        vectorIndex
                        retrievalConfig
                        (retrievalRequest authorization)
                        CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                let retrieval =
                    match retrieve owner with
                    | KnowledgeHybridRetrievalResult.Retrieved value -> value
                    | KnowledgeHybridRetrievalResult.Skipped reason ->
                        invalidOp $"Production retrieval qualification was unexpectedly skipped: {reason}"

                let candidates =
                    retrieval
                    |> KnowledgeHybridRetrieval.candidates
                    |> KnowledgeRetrievalCandidates.candidates

                let firstDocument = candidates |> List.head |> KnowledgeScoredCandidate.document

                Assert.AreEqual(relevantChunkId, KnowledgeIndexDocument.chunkId firstDocument)
                Assert.AreEqual(relevantSourceId, KnowledgeIndexDocument.sourceId firstDocument)

                let reranking =
                    KnowledgeRerankingExecutor.executeAsync
                        (KnowledgeScoreBasedReranker.create KnowledgeScoreBasedRerankerConfig.defaultConfig)
                        (KnowledgeRerankRequest.create
                            (KnowledgeHybridRetrieval.candidates retrieval)
                            1
                            (KnowledgeRerankBudget.create (TimeSpan.FromSeconds 1.0) 0 0 0m))
                        CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                let context =
                    match
                        KnowledgeContextAssembler.assemble
                            owner
                            (KnowledgeContextAssemblyConfig.create
                                (UnicodeWordTokenizer.create ())
                                (KnowledgeContextSourcePreferencePolicy.create
                                    KnowledgeContextSourcePreferencePriority.AuthorityThenCurrent)
                                KnowledgeContextSafetyPolicy.strict
                                200
                                1
                                1
                                1)
                            reranking
                    with
                    | KnowledgeContextAssemblyResult.Assembled value -> value
                    | KnowledgeContextAssemblyResult.Insufficient(reason, _) ->
                        invalidOp $"Production retrieval context was unexpectedly insufficient: {reason}"

                let contextCitation =
                    context
                    |> KnowledgeAssembledContext.items
                    |> List.exactlyOne
                    |> KnowledgeContextItem.citation

                let referenceId = KnowledgeGroundedCitationReferenceId.parse "qualified-reference"

                let reference =
                    KnowledgeGroundedCitationReference.create
                        referenceId
                        (KnowledgeContextCitation.marker contextCitation)
                        (KnowledgeContextCitation.chunkId contextCitation)

                let answer = "The release policy requires two reviewers."

                let claim =
                    KnowledgeGroundedClaim.create
                        answer
                        (KnowledgeGroundedClaimId.parse "qualified-claim")
                        0
                        answer.Length
                        KnowledgeGroundedClaimPresentation.GeneratedInterpretation
                        [ referenceId ]
                        (Confidence.create 1.0)

                let proposal =
                    KnowledgeGroundedAnswerProposal.create answer [ claim ] [ reference ] (Confidence.create 1.0)

                match
                    KnowledgeGroundedCitationVerifier.verify
                        context
                        (KnowledgeGroundedGenerationResult.ProposedAnswer proposal)
                with
                | KnowledgeGroundedVerificationResult.VerifiedAnswer verified ->
                    let citation =
                        verified
                        |> KnowledgeVerifiedGroundedAnswer.claims
                        |> List.exactlyOne
                        |> KnowledgeVerifiedClaim.citations
                        |> List.exactlyOne
                        |> KnowledgeVerifiedCitation.contextCitation

                    Assert.AreEqual(relevantSourceId, KnowledgeContextCitation.sourceId citation)
                    Assert.AreEqual(relevantChunkId, KnowledgeContextCitation.chunkId citation)
                | result -> Assert.Fail $"Expected verified PostgreSQL-backed citation, received {result}."

                let outsider = authorization (UserId.parse "outsider") [] Set.empty

                match retrieve outsider with
                | KnowledgeHybridRetrievalResult.Retrieved value ->
                    Assert.AreEqual(
                        0,
                        value
                        |> KnowledgeHybridRetrieval.candidates
                        |> KnowledgeRetrievalCandidates.candidates
                        |> List.length
                    )
                | KnowledgeHybridRetrievalResult.Skipped reason ->
                    Assert.Fail $"Unauthorized retrieval was unexpectedly skipped rather than empty: {reason}"
            finally
                use connection = new NpgsqlConnection(connectionString)
                connection.Open()
                use command = connection.CreateCommand()
                command.CommandText <- $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"
                command.ExecuteNonQuery() |> ignore

    [<TestMethod>]
    [<TestCategory("ExternalDatabase")>]
    [<TestCategory("Container")>]
    member _.``PostgreSQL full text applies ACL filters ranking and deletion inside the query boundary``() =
        match connectionString with
        | None -> Assert.Inconclusive "Set NAO_TEST_POSTGRES to run PostgreSQL integration tests."
        | Some connectionString ->
            let schema = "nao_test_" + Guid.NewGuid().ToString("N").Substring(0, 16)
            let config = PostgresLexicalIndexConfig.create schema "pg_catalog.english"
            use dataSource = PostgresKnowledgeDataSource.create connectionString

            try
                let index =
                    PostgresLexicalKnowledgeIndex.createAsync dataSource config CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                let owner = authorization ownerUser [] Set.empty

                let sharedAccess =
                    KnowledgeIndexAccess.create [ readerUser ] [ readerGroup ] [ "knowledge-reader" ]

                let firstRecord, firstSourceId, firstChunkId =
                    createRecord
                        owner
                        "lexical-source-one"
                        "alpha alpha launch guide"
                        "intro"
                        sharedAccess
                        [| 1.0; 0.0; 0.0 |]

                let secondRecord, _, secondChunkId =
                    createRecord
                        owner
                        "lexical-source-two"
                        "beta archive"
                        "appendix"
                        KnowledgeIndexAccess.ownerOnly
                        [| 0.0; 1.0; 0.0 |]

                let first = VectorIndexRecord.document firstRecord
                let second = VectorIndexRecord.document secondRecord

                let invalidRecord, _, _ =
                    createRecord
                        owner
                        "lexical-source-invalid"
                        "invalid\u0000content"
                        "invalid"
                        KnowledgeIndexAccess.ownerOnly
                        [| 0.0; 0.0; 1.0 |]

                let invalid = VectorIndexRecord.document invalidRecord

                Assert.ThrowsExactly<PostgresException>(fun () ->
                    index.ApplyAsync
                        owner
                        (KnowledgeIndexMutation.create namespaceName [ first; invalid ] [] None [])
                        CancellationToken.None
                    |> _.GetAwaiter().GetResult()
                    |> ignore)
                |> ignore

                Assert.AreEqual(0, lexicalQuery owner index "alpha" KnowledgeIndexFilter.none |> List.length)

                let initialMutation =
                    KnowledgeIndexMutation.create namespaceName [ first; second ] [] None []

                let initialResult =
                    index.ApplyAsync owner initialMutation CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                Assert.AreEqual(2, KnowledgeIndexMutationResult.upsertedRecords initialResult)
                Assert.AreEqual(0, KnowledgeIndexMutationResult.deletedRecords initialResult)

                let ranked = lexicalQuery owner index "alpha OR beta" KnowledgeIndexFilter.none
                Assert.AreEqual(2, ranked.Length)

                Assert.AreEqual(
                    firstChunkId,
                    ranked
                    |> List.head
                    |> LexicalIndexHit.document
                    |> KnowledgeIndexDocument.chunkId
                )

                let returnedDocument = ranked |> List.head |> LexicalIndexHit.document
                Assert.AreEqual(KnowledgeChunkRole.Leaf, KnowledgeIndexDocument.role returnedDocument)

                Assert.AreEqual(
                    KnowledgeIndexDocument.documentId first,
                    KnowledgeIndexDocument.documentId returnedDocument
                )

                Assert.AreEqual(
                    KnowledgeIndexDocument.documentVersion first,
                    KnowledgeIndexDocument.documentVersion returnedDocument
                )

                Assert.AreEqual(
                    KnowledgeIndexDocument.sourceTrust first,
                    KnowledgeIndexDocument.sourceTrust returnedDocument
                )

                Assert.AreEqual(
                    KnowledgeIndexDocument.versionTrust first,
                    KnowledgeIndexDocument.versionTrust returnedDocument
                )

                Assert.AreEqual(
                    KnowledgeIndexDocument.parentChunkId first,
                    KnowledgeIndexDocument.parentChunkId returnedDocument
                )

                CollectionAssert.AreEqual(
                    KnowledgeIndexDocument.locations first |> List.toArray,
                    KnowledgeIndexDocument.locations returnedDocument |> List.toArray
                )

                let parentChunkId = KnowledgeIndexDocument.parentChunkId first |> Option.get
                let parentText = "hierarchy context"
                let parentHash = ContentHash.computeText parentText

                let parent =
                    KnowledgeIndexDocument.restore
                        namespaceName
                        (KnowledgeIndexDocument.owner first)
                        (KnowledgeIndexDocument.access first)
                        (KnowledgeIndexDocument.sourceId first)
                        (KnowledgeIndexDocument.documentId first)
                        (KnowledgeIndexDocument.documentVersion first)
                        (KnowledgeIndexDocument.documentVersionId first)
                        parentChunkId
                        KnowledgeChunkRole.Parent
                        None
                        [ SourceLocation.create
                              0L
                              (int64 parentText.Length)
                              0
                              parentText.Length
                              None
                              (Some 1)
                              (Some 1)
                              None ]
                        parentHash
                        parentText
                        (KnowledgeIndexDocument.metadata first)
                        (KnowledgeIndexDocument.sourceTrust first)
                        (KnowledgeIndexDocument.versionTrust first)
                        (KnowledgeIndexDocument.sourceModifiedAt first)
                        (KnowledgeIndexDocument.ingestedAt first)
                        (KnowledgeIndexDocument.indexedAt first)

                index.ApplyAsync
                    owner
                    (KnowledgeIndexMutation.create namespaceName [ parent ] [] None [])
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()
                |> ignore

                let parentResolver = PostgresKnowledgeParentResolver.create dataSource config

                let resolvedParents =
                    parentResolver.ResolveAsync
                        owner
                        (KnowledgeParentResolutionRequest.create
                            namespaceName
                            KnowledgeIndexFilter.none
                            [ parentChunkId ]
                            1)
                        CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                CollectionAssert.AreEqual([| parent |], resolvedParents |> List.toArray)

                index.ApplyAsync
                    owner
                    (KnowledgeIndexMutation.create namespaceName [] [ parentChunkId ] None [])
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()
                |> ignore

                Assert.IsTrue(ranked |> List.head |> LexicalIndexHit.score |> (fun score -> score > 0.0))

                let rebuildResult =
                    KnowledgeIndexRebuild.lexicalAsync
                        index
                        owner
                        namespaceName
                        [ first; second ]
                        CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                Assert.AreEqual(2L, KnowledgeIndexRebuildResult.previousRecords rebuildResult)
                Assert.AreEqual(2L, KnowledgeIndexRebuildResult.currentRecords rebuildResult)

                let rebuilt = lexicalQuery owner index "alpha OR beta" KnowledgeIndexFilter.none
                Assert.AreEqual(ranked, rebuilt)

                let reader = authorization readerUser [] Set.empty
                let readerHits = lexicalQuery reader index "alpha OR beta" KnowledgeIndexFilter.none
                Assert.AreEqual(1, readerHits.Length)

                let outsider = authorization (UserId.parse "lexical-outsider") [] Set.empty
                Assert.AreEqual(0, lexicalQuery outsider index "alpha" KnowledgeIndexFilter.none |> List.length)

                let metadataFilter =
                    KnowledgeIndexFilter.create [] [] (Map.ofList [ "chunk.section", "appendix" ]) None None None None

                let filtered = lexicalQuery owner index "beta" metadataFilter
                Assert.AreEqual(1, filtered.Length)

                Assert.AreEqual(
                    secondChunkId,
                    filtered
                    |> List.head
                    |> LexicalIndexHit.document
                    |> KnowledgeIndexDocument.chunkId
                )

                Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
                    index.ApplyAsync
                        reader
                        (KnowledgeIndexMutation.create namespaceName [ first ] [] None [])
                        CancellationToken.None
                    |> _.GetAwaiter().GetResult()
                    |> ignore)
                |> ignore

                let sourceFilter =
                    KnowledgeIndexFilter.create [ firstSourceId ] [] Map.empty None None None None

                let replacement =
                    KnowledgeIndexMutation.create namespaceName [ second ] [] (Some sourceFilter) []

                let replacementResult =
                    index.ApplyAsync owner replacement CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                Assert.AreEqual(1, KnowledgeIndexMutationResult.upsertedRecords replacementResult)
                Assert.AreEqual(1, KnowledgeIndexMutationResult.deletedRecords replacementResult)
                Assert.AreEqual(0, lexicalQuery owner index "alpha" KnowledgeIndexFilter.none |> List.length)

                let secondSourceId = KnowledgeIndexDocument.sourceId second

                let retirement =
                    KnowledgeIndexMutation.create namespaceName [] [] None [ secondSourceId ]

                let retirementResult =
                    index.ApplyAsync owner retirement CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                Assert.AreEqual(1, KnowledgeIndexMutationResult.deletedRecords retirementResult)
                Assert.AreEqual(1, KnowledgeIndexMutationResult.retiredSources retirementResult)
                Assert.AreEqual(0, lexicalQuery owner index "beta" KnowledgeIndexFilter.none |> List.length)

                let retryResult =
                    index.ApplyAsync owner retirement CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                Assert.AreEqual(0, KnowledgeIndexMutationResult.deletedRecords retryResult)
                Assert.AreEqual(1, KnowledgeIndexMutationResult.retiredSources retryResult)

                Assert.ThrowsExactly<InvalidOperationException>(fun () ->
                    index.ApplyAsync
                        owner
                        (KnowledgeIndexMutation.create namespaceName [ second ] [] None [])
                        CancellationToken.None
                    |> _.GetAwaiter().GetResult()
                    |> ignore)
                |> ignore
            finally
                use connection = new NpgsqlConnection(connectionString)
                connection.Open()
                use command = connection.CreateCommand()
                command.CommandText <- $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"
                command.ExecuteNonQuery() |> ignore

    [<TestMethod>]
    [<TestCategory("ExternalDatabase")>]
    [<TestCategory("Container")>]
    [<TestCategory("Benchmark")>]
    member _.``PostgreSQL indexes emit reproducible benchmark evidence``() =
        match connectionString with
        | None -> Assert.Inconclusive "Set NAO_TEST_POSTGRES to run PostgreSQL index benchmarks."
        | Some connectionString ->
            let schema = "nao_benchmark_" + Guid.NewGuid().ToString("N").Substring(0, 16)
            let vectorConfig = PgvectorIndexConfig.create schema 8 32
            let lexicalConfig = PostgresLexicalIndexConfig.create schema "pg_catalog.simple"
            use dataSource = PostgresKnowledgeDataSource.create connectionString

            let benchmarkProfile =
                EmbeddingCapabilityDescriptor.create
                    (ComponentVersion.create "benchmark-embedding" "1")
                    8
                    EmbeddingNormalization.UnitLength
                    VectorDistanceMetric.Cosine
                    (ComponentVersion.create "benchmark-preprocessing" "1")
                    (EmbeddingCapabilityLimits.create 64 1000 100 800)

            try
                let vectorIndex =
                    PgvectorKnowledgeIndex.createAsync dataSource benchmarkProfile vectorConfig CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                let lexicalIndex =
                    PostgresLexicalKnowledgeIndex.createAsync dataSource lexicalConfig CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                let owner = authorization ownerUser [] Set.empty

                let records =
                    [ for topic in 0..7 do
                          for document in 0..7 do
                              let values = Array.zeroCreate<float> 8
                              values[topic] <- 1.0

                              createRecordForProfile
                                  benchmarkProfile
                                  owner
                                  $"benchmark-{topic}-{document}"
                                  $"topic{topic} benchmark document {document}"
                                  $"topic{topic}"
                                  KnowledgeIndexAccess.ownerOnly
                                  values
                              |> fun (record, _, _) -> record ]

                let relevant topic =
                    records
                    |> Seq.map VectorIndexRecord.document
                    |> Seq.filter (fun document ->
                        KnowledgeIndexDocument.metadata document
                        |> Map.find "chunk.section"
                        |> (=) $"topic{topic}")
                    |> Seq.map KnowledgeIndexDocument.chunkId
                    |> Set.ofSeq

                let vectorQueries =
                    [ for topic in 0..7 do
                          let values = Array.zeroCreate<float> 8
                          values[topic] <- 1.0

                          KnowledgeIndexBenchmarkQuery.create
                              $"vector-topic-{topic}"
                              (relevant topic)
                              (fun cancellationToken ->
                                  task {
                                      let! hits =
                                          vectorIndex.QueryAsync
                                              owner
                                              (VectorIndexQuery.create
                                                  namespaceName
                                                  benchmarkProfile
                                                  values
                                                  KnowledgeIndexFilter.none
                                                  8)
                                              cancellationToken

                                      return
                                          hits |> List.map (VectorIndexHit.document >> KnowledgeIndexDocument.chunkId)
                                  }) ]

                let vectorWorkload =
                    KnowledgeIndexBenchmarkWorkload.create
                        "pgvector-64x8"
                        records.Length
                        (fun cancellationToken ->
                            task {
                                let! _ =
                                    vectorIndex.ApplyAsync
                                        owner
                                        (KnowledgeIndexMutation.create namespaceName records [] None [])
                                        cancellationToken

                                return ()
                            })
                        vectorQueries
                        (PgvectorKnowledgeIndex.measureStorageBytesAsync dataSource benchmarkProfile vectorConfig)
                        (fun _ -> Task.FromResult 0m)

                let config = KnowledgeIndexBenchmarkConfig.create 1 3

                let vectorReport =
                    KnowledgeIndexBenchmark.runAsync config vectorWorkload CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                let documents = records |> List.map VectorIndexRecord.document

                let lexicalQueries =
                    [ for topic in 0..7 do
                          KnowledgeIndexBenchmarkQuery.create
                              $"lexical-topic-{topic}"
                              (relevant topic)
                              (fun cancellationToken ->
                                  task {
                                      let! hits =
                                          lexicalIndex.QueryAsync
                                              owner
                                              (LexicalIndexQuery.create
                                                  namespaceName
                                                  $"topic{topic}"
                                                  KnowledgeIndexFilter.none
                                                  8)
                                              cancellationToken

                                      return
                                          hits |> List.map (LexicalIndexHit.document >> KnowledgeIndexDocument.chunkId)
                                  }) ]

                let lexicalWorkload =
                    KnowledgeIndexBenchmarkWorkload.create
                        "postgres-lexical-64x8"
                        documents.Length
                        (fun cancellationToken ->
                            task {
                                let! _ =
                                    lexicalIndex.ApplyAsync
                                        owner
                                        (KnowledgeIndexMutation.create namespaceName documents [] None [])
                                        cancellationToken

                                return ()
                            })
                        lexicalQueries
                        (PostgresLexicalKnowledgeIndex.measureStorageBytesAsync dataSource lexicalConfig)
                        (fun _ -> Task.FromResult 0m)

                let lexicalReport =
                    KnowledgeIndexBenchmark.runAsync config lexicalWorkload CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                let verify report =
                    Assert.AreEqual(64, KnowledgeIndexBenchmarkReport.corpusRecords report)
                    Assert.AreEqual(24, KnowledgeIndexBenchmarkReport.queryExecutions report)
                    Assert.AreEqual(1.0, KnowledgeIndexBenchmarkReport.recall report, 0.000001)
                    Assert.IsTrue(KnowledgeIndexBenchmarkReport.indexRecordsPerSecond report > 0.0)
                    Assert.IsTrue(KnowledgeIndexBenchmarkReport.storageBytes report > 0L)
                    Assert.IsTrue(KnowledgeIndexBenchmarkReport.managedAllocatedBytes report > 0L)
                    Assert.AreEqual(0m, KnowledgeIndexBenchmarkReport.estimatedCostUsd report)

                    printfn "BENCHMARK %s" (KnowledgeIndexBenchmarkReport.toJson report)

                verify vectorReport
                verify lexicalReport
            finally
                use connection = new NpgsqlConnection(connectionString)
                connection.Open()
                use command = connection.CreateCommand()
                command.CommandText <- $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"
                command.ExecuteNonQuery() |> ignore

    [<TestMethod>]
    [<TestCategory("ExternalDatabase")>]
    [<TestCategory("Container")>]
    member _.``Managed PostgreSQL indexes preserve incremental lifecycle through backup and restore``() =
        match connectionString with
        | None -> Assert.Inconclusive "Set NAO_TEST_POSTGRES to run PostgreSQL lifecycle integration tests."
        | Some connectionString ->
            let schema = "nao_test_" + Guid.NewGuid().ToString("N").Substring(0, 16)
            let vectorConfig = PgvectorIndexConfig.create schema 8 32
            let lexicalConfig = PostgresLexicalIndexConfig.create schema "pg_catalog.simple"
            use dataSource = PostgresKnowledgeDataSource.create connectionString

            try
                let managedVector =
                    PgvectorKnowledgeIndex.createManaged dataSource profile vectorConfig

                let managedLexical =
                    PostgresLexicalKnowledgeIndex.createManaged dataSource lexicalConfig

                Assert.AreEqual(
                    KnowledgeIndexCreationResult.Created,
                    managedVector.Management.EnsureCreatedAsync CancellationToken.None
                    |> _.GetAwaiter().GetResult()
                )

                Assert.AreEqual(
                    KnowledgeIndexCreationResult.Created,
                    managedLexical.Management.EnsureCreatedAsync CancellationToken.None
                    |> _.GetAwaiter().GetResult()
                )

                Assert.AreEqual(
                    KnowledgeIndexCreationResult.AlreadyExists,
                    managedVector.Management.EnsureCreatedAsync CancellationToken.None
                    |> _.GetAwaiter().GetResult()
                )

                let owner = authorization ownerUser [] Set.empty

                let first, firstSourceId, firstChunkId =
                    createRecord
                        owner
                        "lifecycle-first"
                        "alpha lifecycle original"
                        "original"
                        KnowledgeIndexAccess.ownerOnly
                        [| 1.0; 0.0; 0.0 |]

                let second, _, secondChunkId =
                    createRecord
                        owner
                        "lifecycle-second"
                        "beta lifecycle retained"
                        "retained"
                        KnowledgeIndexAccess.ownerOnly
                        [| 0.0; 1.0; 0.0 |]

                let vectorRecords = [ first; second ]
                let lexicalRecords = vectorRecords |> List.map VectorIndexRecord.document

                managedVector.Index.ApplyAsync
                    owner
                    (KnowledgeIndexMutation.create namespaceName vectorRecords [] None [])
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()
                |> ignore

                managedLexical.Index.ApplyAsync
                    owner
                    (KnowledgeIndexMutation.create namespaceName lexicalRecords [] None [])
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()
                |> ignore

                managedVector.Index.ApplyAsync
                    owner
                    (KnowledgeIndexMutation.create namespaceName [ first ] [] None [])
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()
                |> ignore

                managedLexical.Index.ApplyAsync
                    owner
                    (KnowledgeIndexMutation.create namespaceName [ VectorIndexRecord.document first ] [] None [])
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()
                |> ignore

                Assert.AreEqual(
                    2,
                    query owner managedVector.Index [| 1.0; 0.0; 0.0 |] KnowledgeIndexFilter.none
                    |> List.length
                )

                let vectorBackup =
                    managedVector.Management.BackupAsync owner namespaceName CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                let lexicalBackup =
                    managedLexical.Management.BackupAsync owner namespaceName CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                CollectionAssert.AreEqual(
                    vectorRecords
                    |> List.sortBy (VectorIndexRecord.document >> KnowledgeIndexDocument.chunkId)
                    |> List.toArray,
                    KnowledgeIndexBackup.records vectorBackup |> List.toArray
                )

                CollectionAssert.AreEqual(
                    lexicalRecords |> List.sortBy KnowledgeIndexDocument.chunkId |> List.toArray,
                    KnowledgeIndexBackup.records lexicalBackup |> List.toArray
                )

                let replacement, _, replacementChunkId =
                    createRecord
                        owner
                        "lifecycle-first"
                        "gamma lifecycle replacement"
                        "replacement"
                        KnowledgeIndexAccess.ownerOnly
                        [| 0.0; 0.0; 1.0 |]

                let sourceFilter =
                    KnowledgeIndexFilter.create [ firstSourceId ] [] Map.empty None None None None

                managedVector.Index.ApplyAsync
                    owner
                    (KnowledgeIndexMutation.create namespaceName [ replacement ] [] (Some sourceFilter) [])
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()
                |> ignore

                managedLexical.Index.ApplyAsync
                    owner
                    (KnowledgeIndexMutation.create
                        namespaceName
                        [ VectorIndexRecord.document replacement ]
                        []
                        (Some sourceFilter)
                        [])
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()
                |> ignore

                managedVector.Index.ApplyAsync
                    owner
                    (KnowledgeIndexMutation.create namespaceName [] [ secondChunkId ] None [])
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()
                |> ignore

                managedLexical.Index.ApplyAsync
                    owner
                    (KnowledgeIndexMutation.create namespaceName [] [ secondChunkId ] None [])
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()
                |> ignore

                let changed =
                    query owner managedVector.Index [| 0.0; 0.0; 1.0 |] KnowledgeIndexFilter.none

                Assert.AreEqual(1, changed.Length)

                Assert.AreEqual(
                    replacementChunkId,
                    changed
                    |> List.exactlyOne
                    |> VectorIndexHit.document
                    |> KnowledgeIndexDocument.chunkId
                )

                Assert.AreEqual(
                    0,
                    lexicalQuery owner managedLexical.Index "alpha OR beta" KnowledgeIndexFilter.none
                    |> List.length
                )

                let vectorRestore =
                    managedVector.Management.RestoreAsync owner vectorBackup CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                let lexicalRestore =
                    managedLexical.Management.RestoreAsync owner lexicalBackup CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                Assert.AreEqual(2L, KnowledgeIndexRebuildResult.currentRecords vectorRestore)
                Assert.AreEqual(2L, KnowledgeIndexRebuildResult.currentRecords lexicalRestore)

                let restoredVector =
                    query owner managedVector.Index [| 1.0; 0.0; 0.0 |] KnowledgeIndexFilter.none

                CollectionAssert.AreEquivalent(
                    [| firstChunkId; secondChunkId |],
                    restoredVector
                    |> List.map (VectorIndexHit.document >> KnowledgeIndexDocument.chunkId)
                    |> List.toArray
                )

                Assert.AreEqual(
                    2,
                    lexicalQuery owner managedLexical.Index "alpha OR beta" KnowledgeIndexFilter.none
                    |> List.length
                )

                let vectorHealth =
                    managedVector.Management.CheckHealthAsync CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                let lexicalHealth =
                    managedLexical.Management.CheckHealthAsync CancellationToken.None
                    |> _.GetAwaiter().GetResult()

                Assert.AreEqual(KnowledgeIndexHealthStatus.Healthy, KnowledgeIndexHealth.status vectorHealth)
                Assert.AreEqual(Some 2L, KnowledgeIndexHealth.searchableRecords vectorHealth)
                Assert.AreEqual(KnowledgeIndexHealthStatus.Healthy, KnowledgeIndexHealth.status lexicalHealth)
                Assert.AreEqual(Some 2L, KnowledgeIndexHealth.searchableRecords lexicalHealth)

                let outsider = authorization (UserId.parse "lifecycle-outsider") [] Set.empty

                Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
                    managedVector.Management.RestoreAsync outsider vectorBackup CancellationToken.None
                    |> _.GetAwaiter().GetResult()
                    |> ignore)
                |> ignore
            finally
                use connection = new NpgsqlConnection(connectionString)
                connection.Open()
                use command = connection.CreateCommand()
                command.CommandText <- $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"
                command.ExecuteNonQuery() |> ignore

    [<TestMethod>]
    member _.``PostgreSQL index configuration rejects unsafe identifiers and invalid settings``() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            PgvectorIndexConfig.create "unsafe; drop schema public" 16 64 |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () -> PgvectorIndexConfig.create "safe_schema" 64 32 |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            PostgresLexicalIndexConfig.create "unsafe schema" "pg_catalog.simple" |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () -> PostgresLexicalIndexConfig.create "safe_schema" " " |> ignore)
        |> ignore
