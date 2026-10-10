namespace Nao.Knowledge.Tests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type HybridRetrievalPipelineTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

    let namespaceName = KnowledgeIndexNamespace.parse "primary"
    let sourceId = KnowledgeSourceId.parse "source"

    let profile =
        EmbeddingCapabilityDescriptor.create
            (ComponentVersion.create "embedding-model" "1")
            2
            EmbeddingNormalization.UnitLength
            VectorDistanceMetric.Cosine
            (ComponentVersion.create "query-preprocessing" "1")
            (EmbeddingCapabilityLimits.create 8 100 50 200)

    let version suffix =
        KnowledgeDocumentVersionId.derive
            (KnowledgeDocumentId.parse ("document-" + suffix))
            1
            (ContentHash.computeText ("version-" + suffix))

    let document versionId sequence role parentChunkId text =
        let contentHash = ContentHash.computeText text
        let chunkId = KnowledgeChunkId.derive versionId "test-chunker" sequence contentHash
        let now = DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero)

        KnowledgeIndexDocument.restore
            namespaceName
            (AuthorizationScope.identity authorization)
            KnowledgeIndexAccess.ownerOnly
            sourceId
            (KnowledgeDocumentId.parse ("document-" + KnowledgeDocumentVersionId.value versionId))
            1
            versionId
            chunkId
            role
            parentChunkId
            [ SourceLocation.create 0L (int64 text.Length) 0 text.Length None (Some 1) (Some 1) None ]
            contentHash
            text
            Map.empty
            (TrustAssessment.create KnowledgeTrust.OrganizationVerified "test" now)
            (TrustAssessment.create KnowledgeTrust.OrganizationVerified "test" now)
            (Some now)
            now
            now

    let request strategy transformation maximumCandidates duration =
        KnowledgeRetrievalRequest.create
            "query"
            authorization
            namespaceName
            KnowledgeIndexFilter.none
            KnowledgeTaskIntent.KnowledgeSeeking
            strategy
            transformation
            (KnowledgeRetrievalLimits.create 100 200 2 (min 2 maximumCandidates) maximumCandidates)
            (KnowledgeRetrievalBudget.create duration 1 1 7 0.05m)

    let embedder delay =
        { Profile = profile
          EmbedQueriesAsync =
            fun _ embeddingRequest cancellationToken ->
                task {
                    do! delay cancellationToken

                    let embeddings =
                        KnowledgeQueryEmbeddingRequest.inputs embeddingRequest
                        |> List.map (fun input ->
                            KnowledgeQueryEmbedding.create
                                (KnowledgeQueryEmbeddingInput.contentHash input)
                                [| 1.0; 0.0 |])

                    return
                        KnowledgeQueryEmbeddingResult.create embeddings (KnowledgeQueryEmbeddingUsage.create 1 4 0.03m)
                } }

    let config parentStage graphStage maximumCandidates =
        KnowledgeHybridRetrievalConfig.create
            KnowledgeReciprocalRankFusionConfig.defaultConfig
            (KnowledgeRetrievalDiversityConfig.unrestricted maximumCandidates)
            parentStage
            graphStage

    [<TestMethod>]
    member _.``Pipeline accounts aggregate usage and reserves graph capacity after parent expansion``() =
        let versionId = version "aggregate"
        let parent = document versionId 0 KnowledgeChunkRole.Parent None "parent"

        let child =
            document versionId 1 KnowledgeChunkRole.Leaf (Some(KnowledgeIndexDocument.chunkId parent)) "child"

        let neighbor = document versionId 2 KnowledgeChunkRole.Leaf None "neighbor"

        let model =
            { Descriptor =
                { Model = ComponentVersion.create "rewrite-model" "1"
                  ConfigurationHash = ContentHash.computeText "rewrite-config"
                  Deterministic = true }
              TransformAsync =
                fun _ _ _ ->
                    Task.FromResult
                        { Variants = [ "generated" ]
                          Usage =
                            { ModelCalls = 1
                              InputTokens = 2
                              OutputTokens = 1
                              EstimatedCostUsd = 0.02m } } }

        let lexicalIndex: KnowledgeLexicalIndex =
            { ApplyAsync = fun _ _ _ -> invalidOp "Mutation is not used by this test."
              QueryAsync = fun _ _ _ -> Task.FromResult [ LexicalIndexHit.create child 1.0 ] }

        let vectorIndex: KnowledgeVectorIndex =
            { ApplyAsync = fun _ _ _ -> invalidOp "Mutation is not used by this test."
              QueryAsync = fun _ _ _ -> Task.FromResult [ VectorIndexHit.create child profile.ProfileId 0.9 ] }

        let parentStage =
            KnowledgeParentExpansionStage.create
                { ResolveAsync = fun _ _ _ -> Task.FromResult [ parent ] }
                (KnowledgeParentExpansionConfig.create 2)

        let graphIndex: KnowledgeGraphIndex =
            { UpsertEdgesAsync = fun _ _ _ -> Task.CompletedTask
              DeleteChunksAsync = fun _ _ _ _ -> Task.FromResult 0
              QueryAsync =
                fun _ query _ ->
                    Assert.AreEqual(1, GraphIndexQuery.limit query)

                    Task.FromResult
                        [ GraphIndexHit.create
                              (KnowledgeIndexDocument.chunkId neighbor)
                              1
                              0.8
                              [ KnowledgeIndexDocument.chunkId child
                                KnowledgeIndexDocument.chunkId neighbor ] ] }

        let graphStage =
            KnowledgeGraphExpansionStage.create
                graphIndex
                { ResolveAsync = fun _ _ _ -> Task.FromResult [ neighbor ] }
                (KnowledgeGraphExpansionConfig.create [] 1 3)

        let result =
            KnowledgeHybridRetrievalPipeline.executeAsync
                (Some model)
                lexicalIndex
                (embedder (fun _ -> Task.FromResult()))
                vectorIndex
                (config (Some parentStage) (Some graphStage) 3)
                (request
                    KnowledgeRetrievalStrategy.HybridWithGraph
                    KnowledgeQueryTransformationStrategy.Rewrite
                    3
                    (TimeSpan.FromSeconds 2.0))
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        match result with
        | KnowledgeHybridRetrievalResult.Skipped _ ->
            Assert.Fail "Knowledge-seeking hybrid retrieval was unexpectedly skipped."
        | KnowledgeHybridRetrievalResult.Retrieved retrieval ->
            Assert.AreEqual(KnowledgeHybridRetrievalPipeline.descriptor, KnowledgeHybridRetrieval.executor retrieval)

            let usage = KnowledgeHybridRetrieval.usage retrieval
            Assert.AreEqual(1, KnowledgeHybridRetrievalUsage.modelCalls usage)
            Assert.AreEqual(1, KnowledgeHybridRetrievalUsage.embeddingCalls usage)
            Assert.AreEqual(6, KnowledgeHybridRetrievalUsage.inputTokens usage)
            Assert.AreEqual(1, KnowledgeHybridRetrievalUsage.outputTokens usage)
            Assert.AreEqual(7L, KnowledgeHybridRetrievalUsage.totalTokens usage)
            Assert.AreEqual(0.05m, KnowledgeHybridRetrievalUsage.estimatedCostUsd usage)
            Assert.AreEqual(2, KnowledgeHybridRetrievalUsage.lexicalCandidates usage)
            Assert.AreEqual(2, KnowledgeHybridRetrievalUsage.vectorCandidates usage)
            Assert.AreEqual(3, KnowledgeHybridRetrievalUsage.finalCandidates usage)

            let candidates =
                retrieval
                |> KnowledgeHybridRetrieval.candidates
                |> KnowledgeRetrievalCandidates.candidates

            CollectionAssert.AreEqual(
                [| child; parent; neighbor |],
                candidates |> List.map KnowledgeScoredCandidate.document |> List.toArray
            )

    [<TestMethod>]
    member _.``Pipeline requires graph infrastructure and actively cancels the aggregate duration``() =
        let hybridWithGraph =
            request
                KnowledgeRetrievalStrategy.HybridWithGraph
                KnowledgeQueryTransformationStrategy.None
                2
                (TimeSpan.FromSeconds 1.0)

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeHybridRetrievalPipeline.executeAsync
                None
                Unchecked.defaultof<KnowledgeLexicalIndex>
                Unchecked.defaultof<KnowledgeQueryEmbedder>
                Unchecked.defaultof<KnowledgeVectorIndex>
                (config None None 2)
                hybridWithGraph
                CancellationToken.None
            |> ignore)
        |> ignore

        let versionId = version "timeout"
        let candidate = document versionId 0 KnowledgeChunkRole.Leaf None "candidate"

        let lexicalIndex: KnowledgeLexicalIndex =
            { ApplyAsync = fun _ _ _ -> invalidOp "Mutation is not used by this test."
              QueryAsync =
                fun _ _ cancellationToken ->
                    task {
                        do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        return [ LexicalIndexHit.create candidate 1.0 ]
                    } }

        let vectorIndex: KnowledgeVectorIndex =
            { ApplyAsync = fun _ _ _ -> invalidOp "Mutation is not used by this test."
              QueryAsync =
                fun _ _ cancellationToken ->
                    task {
                        do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        return [ VectorIndexHit.create candidate profile.ProfileId 0.9 ]
                    } }

        let timer = Stopwatch.StartNew()

        let error =
            Assert.ThrowsExactly<TimeoutException>(fun () ->
                KnowledgeHybridRetrievalPipeline.executeAsync
                    None
                    lexicalIndex
                    (embedder (fun _ -> Task.FromResult()))
                    vectorIndex
                    (config None None 1)
                    (request
                        KnowledgeRetrievalStrategy.Hybrid
                        KnowledgeQueryTransformationStrategy.None
                        1
                        (TimeSpan.FromMilliseconds 200.0))
                    CancellationToken.None
                |> _.GetAwaiter().GetResult()
                |> ignore)

        timer.Stop()
        StringAssert.Contains(error.Message, "aggregate duration budget")
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds 2.0)
