namespace Nao.Knowledge.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type GraphNeighborhoodExpansionTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

    let namespaceName = KnowledgeIndexNamespace.parse "primary"
    let sourceId = KnowledgeSourceId.parse "source"

    let filter =
        KnowledgeIndexFilter.create [ sourceId ] [] Map.empty None None None None

    let profile =
        EmbeddingCapabilityDescriptor.create
            (ComponentVersion.create "embedding-model" "1")
            2
            EmbeddingNormalization.UnitLength
            VectorDistanceMetric.Cosine
            (ComponentVersion.create "query-preprocessing" "1")
            (EmbeddingCapabilityLimits.create 8 100 50 200)

    let version suffix =
        let hash = ContentHash.computeText ("version-" + suffix)
        KnowledgeDocumentVersionId.derive (KnowledgeDocumentId.parse ("document-" + suffix)) 1 hash

    let indexedDocument source versionId sequence role parentChunkId text =
        let contentHash = ContentHash.computeText text
        let chunkId = KnowledgeChunkId.derive versionId "test-chunker" sequence contentHash
        let now = DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero)

        KnowledgeIndexDocument.restore
            namespaceName
            (AuthorizationScope.identity authorization)
            KnowledgeIndexAccess.ownerOnly
            source
            versionId
            chunkId
            role
            parentChunkId
            contentHash
            text
            Map.empty
            (Some now)
            now
            now

    let document source versionId sequence text =
        indexedDocument source versionId sequence KnowledgeChunkRole.Leaf None text

    let selection strategy candidates maximumCandidates =
        let normalized =
            KnowledgeRetrievalRequest.create
                "query"
                authorization
                namespaceName
                filter
                KnowledgeTaskIntent.KnowledgeSeeking
                strategy
                KnowledgeQueryTransformationStrategy.None
                (KnowledgeRetrievalLimits.create 100 200 1 maximumCandidates maximumCandidates)
                (KnowledgeRetrievalBudget.create (TimeSpan.FromMilliseconds 200.0) 0 1 10 0.10m)
            |> KnowledgeQueryNormalizer.normalize

        let planned =
            KnowledgeQueryUnderstanding.planAsync None normalized CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let lexicalIndex: KnowledgeLexicalIndex =
            { ApplyAsync = fun _ _ _ -> invalidOp "Mutation is not used by this test."
              QueryAsync =
                fun _ _ _ ->
                    candidates
                    |> List.mapi (fun index candidate -> LexicalIndexHit.create candidate (10.0 - float index))
                    |> Task.FromResult }

        let lexical =
            match
                KnowledgeLexicalRetrievalExecutor.executeAsync lexicalIndex planned CancellationToken.None
                |> _.GetAwaiter().GetResult()
            with
            | KnowledgeLexicalRetrievalResult.Retrieved value -> value
            | KnowledgeLexicalRetrievalResult.Skipped _ -> invalidOp "Test retrieval was unexpectedly skipped."

        let embedder =
            { Profile = profile
              EmbedQueriesAsync =
                fun _ request _ ->
                    let input = KnowledgeQueryEmbeddingRequest.inputs request |> List.exactlyOne

                    KnowledgeQueryEmbeddingResult.create
                        [ KnowledgeQueryEmbedding.create (KnowledgeQueryEmbeddingInput.contentHash input) [| 1.0; 0.0 |] ]
                        (KnowledgeQueryEmbeddingUsage.create 1 1 0.01m)
                    |> Task.FromResult }

        let vectorIndex: KnowledgeVectorIndex =
            { ApplyAsync = fun _ _ _ -> invalidOp "Mutation is not used by this test."
              QueryAsync =
                fun _ _ _ ->
                    candidates
                    |> List.mapi (fun index candidate ->
                        VectorIndexHit.create candidate profile.ProfileId (1.0 - float index / 10.0))
                    |> Task.FromResult }

        let vector =
            match
                KnowledgeVectorRetrievalExecutor.executeAsync embedder vectorIndex planned CancellationToken.None
                |> _.GetAwaiter().GetResult()
            with
            | KnowledgeVectorRetrievalResult.Retrieved value -> value
            | KnowledgeVectorRetrievalResult.Skipped _ -> invalidOp "Test retrieval was unexpectedly skipped."

        KnowledgeReciprocalRankFusionExecutor.fuse KnowledgeReciprocalRankFusionConfig.defaultConfig lexical vector
        |> KnowledgeRetrievalDiversitySelector.select (KnowledgeRetrievalDiversityConfig.unrestricted candidates.Length)

    let graphIndex
        (queryAsync: AuthorizationScope -> GraphIndexQuery -> CancellationToken -> Task<GraphIndexHit list>)
        : KnowledgeGraphIndex =
        { UpsertEdgesAsync = fun _ _ _ -> Task.CompletedTask
          DeleteChunksAsync = fun _ _ _ _ -> Task.FromResult 0
          QueryAsync = queryAsync }

    [<TestMethod>]
    member _.``Graph expansion materializes authorized neighbors and evidences selected and unavailable hits``() =
        let versionId = version "graph"
        let first = document sourceId versionId 0 "first"
        let second = document sourceId versionId 1 "second"
        let neighbor = document sourceId versionId 2 "neighbor"
        let unavailable = document sourceId versionId 3 "unavailable"

        let selection =
            selection KnowledgeRetrievalStrategy.HybridWithGraph [ first; second ] 5

        let firstId = KnowledgeIndexDocument.chunkId first
        let secondId = KnowledgeIndexDocument.chunkId second
        let neighborId = KnowledgeIndexDocument.chunkId neighbor
        let unavailableId = KnowledgeIndexDocument.chunkId unavailable

        let index =
            graphIndex (fun actualAuthorization query cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()
                Assert.AreSame(authorization, actualAuthorization)
                Assert.AreEqual(namespaceName, GraphIndexQuery.namespaceName query)
                Assert.AreEqual(Set.ofList [ firstId; secondId ], GraphIndexQuery.startChunkIds query)
                Assert.AreEqual(Set.ofList [ "references"; "related" ], GraphIndexQuery.edgeKinds query)
                Assert.AreEqual(2, GraphIndexQuery.maximumDepth query)
                Assert.AreEqual(3, GraphIndexQuery.limit query)

                Task.FromResult
                    [ GraphIndexHit.create neighborId 1 0.9 [ firstId; neighborId ]
                      GraphIndexHit.create secondId 1 0.8 [ firstId; secondId ]
                      GraphIndexHit.create unavailableId 2 0.7 [ secondId; neighborId; unavailableId ] ])

        let resolver: KnowledgeGraphDocumentResolver =
            { ResolveAsync =
                fun actualAuthorization request cancellationToken ->
                    cancellationToken.ThrowIfCancellationRequested()
                    Assert.AreSame(authorization, actualAuthorization)
                    Assert.AreEqual(namespaceName, KnowledgeGraphDocumentResolutionRequest.namespaceName request)
                    Assert.AreSame(filter, KnowledgeGraphDocumentResolutionRequest.filter request)

                    Assert.AreEqual(
                        Set.ofList [ neighborId; unavailableId ],
                        KnowledgeGraphDocumentResolutionRequest.chunkIds request
                    )

                    Assert.AreEqual(3, KnowledgeGraphDocumentResolutionRequest.maximumDocuments request)
                    Task.FromResult [ neighbor ] }

        let result =
            KnowledgeGraphNeighborhoodExpansionExecutor.expandAsync
                index
                resolver
                (KnowledgeGraphExpansionConfig.create [ "references"; "related" ] 2 4)
                selection
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        match result with
        | KnowledgeGraphNeighborhoodExpansionResult.NotRequested _ ->
            Assert.Fail "Graph expansion was unexpectedly skipped."
        | KnowledgeGraphNeighborhoodExpansionResult.Expanded expansion ->
            Assert.AreEqual(
                KnowledgeGraphNeighborhoodExpansionExecutor.descriptor,
                KnowledgeGraphNeighborhoodExpansion.expander expansion
            )

            Assert.AreSame(selection, KnowledgeGraphNeighborhoodExpansion.selection expansion)
            let neighbors = KnowledgeGraphNeighborhoodExpansion.neighbors expansion
            Assert.AreEqual(1, neighbors.Length)
            Assert.AreSame(neighbor, neighbors[0] |> KnowledgeGraphNeighbor.document)
            Assert.AreEqual(1, neighbors[0] |> KnowledgeGraphNeighbor.rank)
            Assert.AreEqual(0.9, neighbors[0] |> KnowledgeGraphNeighbor.score)

            match
                KnowledgeGraphNeighborhoodExpansion.evidence expansion
                |> List.map KnowledgeGraphExpansionEvidence.outcome
            with
            | [ KnowledgeGraphExpansionOutcome.Expanded(actual, 1)
                KnowledgeGraphExpansionOutcome.AlreadySelected
                KnowledgeGraphExpansionOutcome.Unavailable ] -> Assert.AreSame(neighbor, actual)
            | outcomes -> Assert.Fail $"Unexpected graph expansion outcomes: {outcomes}"

    [<TestMethod>]
    member _.``Non-graph strategies require no graph infrastructure``() =
        let candidate = document sourceId (version "hybrid") 0 "candidate"
        let selection = selection KnowledgeRetrievalStrategy.Hybrid [ candidate ] 2

        match
            KnowledgeGraphNeighborhoodExpansionExecutor.expandAsync
                Unchecked.defaultof<KnowledgeGraphIndex>
                Unchecked.defaultof<KnowledgeGraphDocumentResolver>
                (KnowledgeGraphExpansionConfig.create [] 1 1)
                selection
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
        with
        | KnowledgeGraphNeighborhoodExpansionResult.NotRequested actual -> Assert.AreSame(selection, actual)
        | KnowledgeGraphNeighborhoodExpansionResult.Expanded _ ->
            Assert.Fail "Hybrid retrieval must not invoke optional graph expansion."

    [<TestMethod>]
    member _.``Candidate assembly retains typed scores when parent and graph evidence converge``() =
        let versionId = version "assembled"

        let parent =
            indexedDocument sourceId versionId 0 KnowledgeChunkRole.Parent None "parent"

        let child =
            indexedDocument
                sourceId
                versionId
                1
                KnowledgeChunkRole.Leaf
                (Some(KnowledgeIndexDocument.chunkId parent))
                "child"

        let selection = selection KnowledgeRetrievalStrategy.HybridWithGraph [ child ] 3

        let parentExpansion =
            KnowledgeParentChildExpansionExecutor.expandAsync
                { ResolveAsync = fun _ _ _ -> Task.FromResult [ parent ] }
                (KnowledgeParentExpansionConfig.create 1)
                selection
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let childId = KnowledgeIndexDocument.chunkId child
        let parentId = KnowledgeIndexDocument.chunkId parent

        let graphExpansion =
            KnowledgeGraphNeighborhoodExpansionExecutor.expandAsync
                (graphIndex (fun _ _ _ -> Task.FromResult [ GraphIndexHit.create parentId 1 0.8 [ childId; parentId ] ]))
                { ResolveAsync = fun _ _ _ -> Task.FromResult [ parent ] }
                (KnowledgeGraphExpansionConfig.create [] 1 1)
                selection
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let assembled =
            KnowledgeRetrievalCandidateAssembler.assemble selection (Some parentExpansion) (Some graphExpansion)

        Assert.AreEqual(
            KnowledgeRetrievalCandidateAssembler.descriptor,
            KnowledgeRetrievalCandidates.assembler assembled
        )

        Assert.AreSame(selection, KnowledgeRetrievalCandidates.selection assembled)

        let candidates = KnowledgeRetrievalCandidates.candidates assembled
        Assert.AreEqual(2, candidates.Length)
        Assert.AreSame(child, candidates[0] |> KnowledgeScoredCandidate.document)
        Assert.AreEqual(1, candidates[0] |> KnowledgeScoredCandidate.rank)
        Assert.AreSame(parent, candidates[1] |> KnowledgeScoredCandidate.document)
        Assert.AreEqual(2, candidates[1] |> KnowledgeScoredCandidate.rank)

        let selectedScore =
            selection
            |> KnowledgeRetrievalDiversitySelection.candidates
            |> List.exactlyOne
            |> KnowledgeSelectedCandidate.score

        Assert.AreEqual(selectedScore, candidates[0] |> KnowledgeScoredCandidate.primaryScore)
        Assert.AreEqual(selectedScore, candidates[1] |> KnowledgeScoredCandidate.primaryScore)

        match candidates[1] |> KnowledgeScoredCandidate.evidence with
        | [ KnowledgeCandidateStrategyEvidence.ParentChildExpansion parentEvidence
            KnowledgeCandidateStrategyEvidence.GraphNeighborhoodExpansion graphEvidence ] ->
            Assert.AreEqual(selectedScore, KnowledgeParentCandidateEvidence.score parentEvidence)
            Assert.AreEqual(0.8, KnowledgeGraphNeighbor.score graphEvidence)

            Assert.AreSame(
                child,
                parentEvidence
                |> KnowledgeParentCandidateEvidence.supportingCandidates
                |> List.exactlyOne
                |> KnowledgeSelectedCandidate.document
            )
        | evidence -> Assert.Fail $"Unexpected assembled candidate evidence: {evidence}"

    [<TestMethod>]
    member _.``Candidate assembly rejects mixed selections and aggregate candidate overflow``() =
        let versionId = version "assembly-bounds"

        let parent =
            indexedDocument sourceId versionId 0 KnowledgeChunkRole.Parent None "parent"

        let child =
            indexedDocument
                sourceId
                versionId
                1
                KnowledgeChunkRole.Leaf
                (Some(KnowledgeIndexDocument.chunkId parent))
                "child"

        let neighbor = document sourceId versionId 2 "neighbor"

        let firstSelection =
            selection KnowledgeRetrievalStrategy.HybridWithGraph [ child ] 2

        let secondSelection =
            selection KnowledgeRetrievalStrategy.HybridWithGraph [ child ] 2

        let parentExpansion =
            KnowledgeParentChildExpansionExecutor.expandAsync
                { ResolveAsync = fun _ _ _ -> Task.FromResult [ parent ] }
                (KnowledgeParentExpansionConfig.create 1)
                firstSelection
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeRetrievalCandidateAssembler.assemble secondSelection (Some parentExpansion) None
            |> ignore)
        |> ignore

        let childId = KnowledgeIndexDocument.chunkId child
        let neighborId = KnowledgeIndexDocument.chunkId neighbor

        let graphExpansion =
            KnowledgeGraphNeighborhoodExpansionExecutor.expandAsync
                (graphIndex (fun _ _ _ ->
                    Task.FromResult [ GraphIndexHit.create neighborId 1 0.7 [ childId; neighborId ] ]))
                { ResolveAsync = fun _ _ _ -> Task.FromResult [ neighbor ] }
                (KnowledgeGraphExpansionConfig.create [] 1 1)
                firstSelection
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeRetrievalCandidateAssembler.assemble firstSelection (Some parentExpansion) (Some graphExpansion)
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Graph expansion rejects invalid paths resolver widening and timeout``() =
        let versionId = version "invalid"
        let selected = document sourceId versionId 0 "selected"
        let neighbor = document sourceId versionId 1 "neighbor"
        let selection = selection KnowledgeRetrievalStrategy.HybridWithGraph [ selected ] 2
        let selectedId = KnowledgeIndexDocument.chunkId selected
        let neighborId = KnowledgeIndexDocument.chunkId neighbor
        let config = KnowledgeGraphExpansionConfig.create [] 1 1

        let resolver: KnowledgeGraphDocumentResolver =
            { ResolveAsync = fun _ _ _ -> Task.FromResult [ neighbor ] }

        let invalidPath =
            graphIndex (fun _ _ _ ->
                Task.FromResult [ GraphIndexHit.create neighborId 1 1.0 [ neighborId; neighborId ] ])

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeGraphNeighborhoodExpansionExecutor.expandAsync
                invalidPath
                resolver
                config
                selection
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let validIndex =
            graphIndex (fun _ _ _ ->
                Task.FromResult [ GraphIndexHit.create neighborId 1 1.0 [ selectedId; neighborId ] ])

        let widened =
            document (KnowledgeSourceId.parse "other-source") versionId 2 "widened"

        let widenedResolver: KnowledgeGraphDocumentResolver =
            { ResolveAsync = fun _ _ _ -> Task.FromResult [ widened ] }

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeGraphNeighborhoodExpansionExecutor.expandAsync
                validIndex
                widenedResolver
                config
                selection
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let cancelled =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let slowIndex =
            graphIndex (fun _ _ cancellationToken ->
                task {
                    use _registration =
                        cancellationToken.Register(fun () -> cancelled.TrySetResult() |> ignore)

                    try
                        do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        return []
                    finally
                        if cancellationToken.IsCancellationRequested then
                            cancelled.TrySetResult() |> ignore
                })

        Assert.ThrowsExactly<TimeoutException>(fun () ->
            KnowledgeGraphNeighborhoodExpansionExecutor.expandAsync
                slowIndex
                resolver
                config
                selection
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        cancelled.Task.Wait(TimeSpan.FromSeconds 5.0) |> Assert.IsTrue
