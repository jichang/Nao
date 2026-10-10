namespace Nao.Knowledge.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type ParentChildExpansionTests() =
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

    let selection candidates maximumCandidates =
        let normalized =
            KnowledgeRetrievalRequest.create
                "query"
                authorization
                namespaceName
                filter
                KnowledgeTaskIntent.KnowledgeSeeking
                KnowledgeRetrievalStrategy.Hybrid
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

    [<TestMethod>]
    member _.``Expansion resolves each parent once and evidences unavailable and limited parents``() =
        let versionId = version "shared"
        let parentOne = document versionId 0 KnowledgeChunkRole.Parent None "parent one"
        let parentTwo = document versionId 3 KnowledgeChunkRole.Parent None "parent two"
        let parentThree = document versionId 5 KnowledgeChunkRole.Parent None "parent three"

        let childOne =
            document versionId 1 KnowledgeChunkRole.Leaf (Some(KnowledgeIndexDocument.chunkId parentOne)) "child one"

        let childTwo =
            document versionId 2 KnowledgeChunkRole.Leaf (Some(KnowledgeIndexDocument.chunkId parentOne)) "child two"

        let childThree =
            document versionId 4 KnowledgeChunkRole.Leaf (Some(KnowledgeIndexDocument.chunkId parentTwo)) "child three"

        let childFour =
            document versionId 6 KnowledgeChunkRole.Leaf (Some(KnowledgeIndexDocument.chunkId parentThree)) "child four"

        let selection = selection [ childOne; childTwo; childThree; childFour ] 6
        let mutable calls = 0

        let resolver =
            { ResolveAsync =
                fun actualAuthorization request cancellationToken ->
                    cancellationToken.ThrowIfCancellationRequested()
                    calls <- calls + 1
                    Assert.AreSame(authorization, actualAuthorization)
                    Assert.AreEqual(namespaceName, KnowledgeParentResolutionRequest.namespaceName request)
                    Assert.AreSame(filter, KnowledgeParentResolutionRequest.filter request)
                    Assert.AreEqual(2, KnowledgeParentResolutionRequest.maximumParents request)

                    Assert.AreEqual(
                        Set.ofList
                            [ KnowledgeIndexDocument.chunkId parentOne
                              KnowledgeIndexDocument.chunkId parentTwo ],
                        KnowledgeParentResolutionRequest.parentChunkIds request
                    )

                    Task.FromResult [ parentOne ] }

        let expansion =
            KnowledgeParentChildExpansionExecutor.expandAsync
                resolver
                (KnowledgeParentExpansionConfig.create 3)
                selection
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(1, calls)

        Assert.AreEqual(
            KnowledgeParentChildExpansionExecutor.descriptor,
            KnowledgeParentChildExpansion.expander expansion
        )

        Assert.AreSame(selection, KnowledgeParentChildExpansion.selection expansion)
        CollectionAssert.AreEqual([| parentOne |], KnowledgeParentChildExpansion.parents expansion |> List.toArray)

        let evidence = KnowledgeParentChildExpansion.evidence expansion
        Assert.AreEqual(3, evidence.Length)
        Assert.AreEqual(2, evidence[0] |> KnowledgeParentExpansionEvidence.childChunkIds |> List.length)

        match evidence |> List.map KnowledgeParentExpansionEvidence.outcome with
        | [ KnowledgeParentExpansionOutcome.Expanded actual
            KnowledgeParentExpansionOutcome.Unavailable
            KnowledgeParentExpansionOutcome.ParentLimit ] -> Assert.AreSame(parentOne, actual)
        | outcomes -> Assert.Fail $"Unexpected parent expansion outcomes: {outcomes}"

    [<TestMethod>]
    member _.``Expansion with no child relationships requires no resolver``() =
        let standalone =
            document (version "standalone") 0 KnowledgeChunkRole.Leaf None "standalone"

        let selection = selection [ standalone ] 2

        let expansion =
            KnowledgeParentChildExpansionExecutor.expandAsync
                Unchecked.defaultof<KnowledgeParentResolver>
                (KnowledgeParentExpansionConfig.create 1)
                selection
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(0, KnowledgeParentChildExpansion.parents expansion |> List.length)
        Assert.AreEqual(0, KnowledgeParentChildExpansion.evidence expansion |> List.length)

    [<TestMethod>]
    member _.``Expansion rejects invalid parent relationships and actively cancels on timeout``() =
        let versionId = version "invalid"
        let parent = document versionId 0 KnowledgeChunkRole.Parent None "parent"

        let child =
            document versionId 1 KnowledgeChunkRole.Leaf (Some(KnowledgeIndexDocument.chunkId parent)) "child"

        let selection = selection [ child ] 2
        let invalidParent = document versionId 2 KnowledgeChunkRole.Leaf None "not a parent"

        let invalidResolver =
            { ResolveAsync = fun _ _ _ -> Task.FromResult [ invalidParent ] }

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeParentChildExpansionExecutor.expandAsync
                invalidResolver
                (KnowledgeParentExpansionConfig.create 1)
                selection
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let cancelled =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let slowResolver =
            { ResolveAsync =
                fun _ _ cancellationToken ->
                    task {
                        use _registration =
                            cancellationToken.Register(fun () -> cancelled.TrySetResult() |> ignore)

                        try
                            do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                            return []
                        finally
                            if cancellationToken.IsCancellationRequested then
                                cancelled.TrySetResult() |> ignore
                    } }

        Assert.ThrowsExactly<TimeoutException>(fun () ->
            KnowledgeParentChildExpansionExecutor.expandAsync
                slowResolver
                (KnowledgeParentExpansionConfig.create 1)
                selection
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        cancelled.Task.Wait(TimeSpan.FromSeconds 5.0) |> Assert.IsTrue
