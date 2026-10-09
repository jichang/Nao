namespace Nao.Knowledge.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Determinism"); TestCategory("FailClosed")>]
type ReciprocalRankFusionTests() =
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

    let document suffix =
        let text = "content-" + suffix
        let contentHash = ContentHash.computeText text

        let versionId =
            KnowledgeDocumentVersionId.derive (KnowledgeDocumentId.parse ("document-" + suffix)) 1 contentHash

        let chunkId = KnowledgeChunkId.derive versionId "test-chunker" 0 contentHash
        let now = DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero)

        KnowledgeIndexDocument.restore
            namespaceName
            (AuthorizationScope.identity authorization)
            KnowledgeIndexAccess.ownerOnly
            sourceId
            versionId
            chunkId
            KnowledgeChunkRole.Leaf
            None
            contentHash
            text
            Map.empty
            (Some now)
            now
            now

    let understanding limits transformation =
        let budget =
            match transformation with
            | KnowledgeQueryTransformationStrategy.None ->
                KnowledgeRetrievalBudget.create (TimeSpan.FromSeconds 1.0) 0 1 10 0.10m
            | _ -> KnowledgeRetrievalBudget.create (TimeSpan.FromSeconds 1.0) 1 1 20 0.10m

        let normalized =
            KnowledgeRetrievalRequest.create
                "original query"
                authorization
                namespaceName
                KnowledgeIndexFilter.none
                KnowledgeTaskIntent.KnowledgeSeeking
                KnowledgeRetrievalStrategy.Hybrid
                transformation
                limits
                budget
            |> KnowledgeQueryNormalizer.normalize

        let model =
            match transformation with
            | KnowledgeQueryTransformationStrategy.None -> None
            | _ ->
                Some
                    { Descriptor =
                        { Model = ComponentVersion.create "query-model" "1"
                          ConfigurationHash = ContentHash.computeText "configuration"
                          Deterministic = true }
                      TransformAsync =
                        fun _ _ _ ->
                            Task.FromResult
                                { Variants = [ "alternative query" ]
                                  Usage =
                                    { ModelCalls = 1
                                      InputTokens = 2
                                      OutputTokens = 2
                                      EstimatedCostUsd = 0.01m } } }

        KnowledgeQueryUnderstanding.planAsync model normalized CancellationToken.None
        |> _.GetAwaiter().GetResult()

    let lexicalRetrieval planned retrieve =
        let index: KnowledgeLexicalIndex =
            { ApplyAsync = fun _ _ _ -> invalidOp "Mutation is not used by this test."
              QueryAsync =
                fun _ query _ ->
                    retrieve (LexicalIndexQuery.query query)
                    |> List.map (fun (document, score) -> LexicalIndexHit.create document score)
                    |> Task.FromResult }

        match
            KnowledgeLexicalRetrievalExecutor.executeAsync index planned CancellationToken.None
            |> _.GetAwaiter().GetResult()
        with
        | KnowledgeLexicalRetrievalResult.Retrieved retrieval -> retrieval
        | KnowledgeLexicalRetrievalResult.Skipped _ -> invalidOp "Fusion test retrieval was unexpectedly skipped."

    let vectorRetrieval planned retrieve =
        let embedder =
            { Profile = profile
              EmbedQueriesAsync =
                fun _ request _ ->
                    let embeddings =
                        KnowledgeQueryEmbeddingRequest.inputs request
                        |> List.map (fun input ->
                            let values =
                                match KnowledgeQueryEmbeddingInput.text input with
                                | "original query" -> [| 1.0; 0.0 |]
                                | "alternative query" -> [| 0.0; 1.0 |]
                                | value -> invalidOp $"Unexpected query text {value}."

                            KnowledgeQueryEmbedding.create (KnowledgeQueryEmbeddingInput.contentHash input) values)

                    KnowledgeQueryEmbeddingResult.create
                        embeddings
                        (KnowledgeQueryEmbeddingUsage.create 1 embeddings.Length 0.01m)
                    |> Task.FromResult }

        let index: KnowledgeVectorIndex =
            { ApplyAsync = fun _ _ _ -> invalidOp "Mutation is not used by this test."
              QueryAsync =
                fun _ query _ ->
                    retrieve (VectorIndexQuery.values query)
                    |> List.map (fun (document, score) -> VectorIndexHit.create document profile.ProfileId score)
                    |> Task.FromResult }

        match
            KnowledgeVectorRetrievalExecutor.executeAsync embedder index planned CancellationToken.None
            |> _.GetAwaiter().GetResult()
        with
        | KnowledgeVectorRetrievalResult.Retrieved retrieval -> retrieval
        | KnowledgeVectorRetrievalResult.Skipped _ -> invalidOp "Fusion test retrieval was unexpectedly skipped."

    [<TestMethod>]
    member _.``Weighted reciprocal rank fusion retains every contribution and ignores raw score scale``() =
        let first = document "first"
        let second = document "second"
        let third = document "third"

        let planned =
            understanding
                (KnowledgeRetrievalLimits.create 100 200 2 3 8)
                KnowledgeQueryTransformationStrategy.MultiQuery

        let lexical =
            lexicalRetrieval planned (function
                | "original query" -> [ first, 1000.0; second, 900.0 ]
                | "alternative query" -> [ third, 0.0002; first, 0.0001 ]
                | value -> invalidOp $"Unexpected lexical query {value}.")

        let vector =
            vectorRetrieval planned (fun values ->
                if values[0] = 1.0 then
                    [ second, 0.99; first, 0.98 ]
                else
                    [ first, -1000.0; third, -2000.0 ])

        let fusion =
            KnowledgeReciprocalRankFusionExecutor.fuse
                (KnowledgeReciprocalRankFusionConfig.create 60 2.0 1.0)
                lexical
                vector

        Assert.AreEqual(KnowledgeReciprocalRankFusionExecutor.descriptor, KnowledgeReciprocalRankFusion.fusion fusion)
        Assert.AreSame(KnowledgeLexicalRetrieval.request lexical, KnowledgeReciprocalRankFusion.request fusion)
        Assert.AreEqual(4, KnowledgeReciprocalRankFusion.inputLists fusion)
        Assert.AreEqual(8, KnowledgeReciprocalRankFusion.inputCandidates fusion)

        let candidates = KnowledgeReciprocalRankFusion.candidates fusion
        Assert.AreEqual(3, candidates.Length)
        Assert.AreSame(first, candidates[0] |> KnowledgeFusedCandidate.document)
        Assert.AreEqual(1, candidates[0] |> KnowledgeFusedCandidate.rank)

        Assert.AreEqual(3.0 / 61.0 + 3.0 / 62.0, candidates[0] |> KnowledgeFusedCandidate.score, 0.000000001)

        let contributions = candidates[0] |> KnowledgeFusedCandidate.contributions
        Assert.AreEqual(4, contributions.Length)

        Assert.AreEqual(
            2,
            contributions
            |> List.filter (KnowledgeRankFusionContribution.leg >> (=) KnowledgeRetrievalLeg.Lexical)
            |> List.length
        )

        contributions
        |> List.iter (fun contribution ->
            Assert.AreEqual(
                KnowledgeRankFusionContribution.weight contribution
                / (60.0 + float (KnowledgeRankFusionContribution.rank contribution)),
                KnowledgeRankFusionContribution.contribution contribution,
                0.000000001
            ))

    [<TestMethod>]
    member _.``Fusion caps output and rejects evidence from different plans``() =
        let first = document "first"
        let second = document "second"
        let third = document "third"
        let limits = KnowledgeRetrievalLimits.create 100 200 1 2 2
        let planned = understanding limits KnowledgeQueryTransformationStrategy.None

        let lexical = lexicalRetrieval planned (fun _ -> [ first, 0.9; second, 0.8 ])

        let vector = vectorRetrieval planned (fun _ -> [ second, 0.7; third, 0.6 ])

        let fusion =
            KnowledgeReciprocalRankFusionExecutor.fuse KnowledgeReciprocalRankFusionConfig.defaultConfig lexical vector

        let candidates = KnowledgeReciprocalRankFusion.candidates fusion
        Assert.AreEqual(2, candidates.Length)
        Assert.AreSame(second, candidates[0] |> KnowledgeFusedCandidate.document)

        let otherPlan = understanding limits KnowledgeQueryTransformationStrategy.None
        let otherVector = vectorRetrieval otherPlan (fun _ -> [ second, 0.7 ])

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeReciprocalRankFusionExecutor.fuse
                KnowledgeReciprocalRankFusionConfig.defaultConfig
                lexical
                otherVector
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Fusion rejects duplicate chunks within one ranking list and invalid configuration``() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeReciprocalRankFusionConfig.create 0 1.0 1.0 |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeReciprocalRankFusionConfig.create 60 Double.NaN 1.0 |> ignore)
        |> ignore

        let candidate = document "candidate"

        let planned =
            understanding (KnowledgeRetrievalLimits.create 100 200 1 3 3) KnowledgeQueryTransformationStrategy.None

        let lexical = lexicalRetrieval planned (fun _ -> [ candidate, 0.9; candidate, 0.8 ])

        let vector = vectorRetrieval planned (fun _ -> [ candidate, 0.7 ])

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeReciprocalRankFusionExecutor.fuse KnowledgeReciprocalRankFusionConfig.defaultConfig lexical vector
            |> ignore)
        |> ignore
