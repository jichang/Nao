namespace Nao.Knowledge.Tests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type VectorRetrievalTests() =
    let authorization =
        SecurityPrincipal.createAuthenticated
            (TenantId.parse "tenant")
            (UserId.parse "user")
            None
            [ GroupId.parse "readers" ]
            [ "knowledge-reader" ]
            Map.empty
            AuthenticationStrength.MultiFactor
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
            3
            EmbeddingNormalization.UnitLength
            VectorDistanceMetric.Cosine
            (ComponentVersion.create "query-preprocessing" "1")
            (EmbeddingCapabilityLimits.create 8 200 100 400)

    let document sourceValue suffix =
        let text = "content " + suffix
        let contentHash = ContentHash.computeText text

        let documentVersionId =
            KnowledgeDocumentVersionId.derive (KnowledgeDocumentId.parse ("document-" + suffix)) 1 contentHash

        let chunkId = KnowledgeChunkId.derive documentVersionId "test-chunker" 0 contentHash
        let now = DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero)

        KnowledgeIndexDocument.restore
            namespaceName
            (AuthorizationScope.identity authorization)
            KnowledgeIndexAccess.ownerOnly
            sourceValue
            documentVersionId
            chunkId
            KnowledgeChunkRole.Leaf
            None
            contentHash
            text
            Map.empty
            (Some now)
            now
            now

    let understanding retrievalStrategy transformation limits budget outputs =
        let normalized =
            KnowledgeRetrievalRequest.create
                " original query "
                authorization
                namespaceName
                filter
                KnowledgeTaskIntent.KnowledgeSeeking
                retrievalStrategy
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
                                { Variants = outputs
                                  Usage =
                                    { ModelCalls = 1
                                      InputTokens = 2
                                      OutputTokens = 2
                                      EstimatedCostUsd = 0.01m } } }

        KnowledgeQueryUnderstanding.planAsync model normalized CancellationToken.None
        |> _.GetAwaiter().GetResult()

    let vectorIndex
        (queryAsync: AuthorizationScope -> VectorIndexQuery -> CancellationToken -> Task<VectorIndexHit list>)
        : KnowledgeVectorIndex =
        { ApplyAsync =
            fun _ mutation _ ->
                Task.FromResult(
                    KnowledgeIndexMutationResult.create
                        (KnowledgeIndexMutation.namespaceName mutation)
                        0
                        0
                        0
                        DateTimeOffset.UtcNow
                )
          QueryAsync = queryAsync }

    let embeddingResult
        (valuesByInput: float array array)
        (usage: KnowledgeQueryEmbeddingUsage)
        (request: KnowledgeQueryEmbeddingRequest)
        =
        KnowledgeQueryEmbeddingRequest.inputs request
        |> List.mapi (fun index input ->
            KnowledgeQueryEmbedding.create (KnowledgeQueryEmbeddingInput.contentHash input) valuesByInput[index])
        |> fun embeddings -> KnowledgeQueryEmbeddingResult.create embeddings usage

    [<TestMethod>]
    member _.``Skipped understanding requires neither an embedder nor a vector index``() =
        let normalized =
            KnowledgeRetrievalRequest.create
                "query"
                authorization
                namespaceName
                filter
                KnowledgeTaskIntent.NonKnowledge
                KnowledgeRetrievalStrategy.Vector
                KnowledgeQueryTransformationStrategy.None
                KnowledgeRetrievalLimits.defaultLimits
                KnowledgeRetrievalBudget.deterministic
            |> KnowledgeQueryNormalizer.normalize

        let skipped =
            KnowledgeQueryUnderstanding.planAsync None normalized CancellationToken.None
            |> _.GetAwaiter().GetResult()

        match
            KnowledgeVectorRetrievalExecutor.executeAsync
                Unchecked.defaultof<KnowledgeQueryEmbedder>
                Unchecked.defaultof<KnowledgeVectorIndex>
                skipped
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
        with
        | KnowledgeVectorRetrievalResult.Skipped admission ->
            match skipped with
            | KnowledgeQueryUnderstandingResult.RetrievalSkipped expected -> Assert.AreSame(expected, admission)
            | _ -> Assert.Fail "Expected skipped query understanding."
        | KnowledgeVectorRetrievalResult.Retrieved _ -> Assert.Fail "Explicit non-knowledge intent must remain skipped."

    [<TestMethod>]
    member _.``Vector retrieval embeds variants once and preserves profile rank and usage evidence``() =
        let limits = KnowledgeRetrievalLimits.create 100 200 3 3 6

        let planned =
            understanding
                KnowledgeRetrievalStrategy.Hybrid
                KnowledgeQueryTransformationStrategy.MultiQuery
                limits
                (KnowledgeRetrievalBudget.create (TimeSpan.FromSeconds 1.0) 1 2 20 0.10m)
                [ "alternative query" ]

        let mutable embeddingCalls = 0
        let embeddedTexts = ConcurrentBag<string>()
        let usage = KnowledgeQueryEmbeddingUsage.create 1 6 0.02m

        let embedder =
            { Profile = profile
              EmbedQueriesAsync =
                fun actualAuthorization request cancellationToken ->
                    cancellationToken.ThrowIfCancellationRequested()
                    embeddingCalls <- embeddingCalls + 1
                    Assert.AreSame(authorization, actualAuthorization)
                    let embeddingLimits = KnowledgeQueryEmbeddingRequest.limits request
                    Assert.AreEqual(2, KnowledgeQueryEmbeddingLimits.maximumProviderCalls embeddingLimits)
                    Assert.AreEqual(16, KnowledgeQueryEmbeddingLimits.maximumTokens embeddingLimits)
                    Assert.AreEqual(0.09m, KnowledgeQueryEmbeddingLimits.maximumCostUsd embeddingLimits)

                    KnowledgeQueryEmbeddingRequest.inputs request
                    |> List.iter (KnowledgeQueryEmbeddingInput.text >> embeddedTexts.Add)

                    embeddingResult [| [| 1.0; 0.0; 0.0 |]; [| 0.0; 1.0; 0.0 |] |] usage request
                    |> Task.FromResult }

        let first = document sourceId "first"
        let second = document sourceId "second"
        let queriedVectors = ConcurrentBag<float array>()

        let index =
            vectorIndex (fun actualAuthorization query cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()
                Assert.AreSame(authorization, actualAuthorization)
                Assert.AreEqual(namespaceName, VectorIndexQuery.namespaceName query)
                Assert.AreSame(filter, VectorIndexQuery.filter query)
                Assert.AreEqual(profile, VectorIndexQuery.profile query)
                Assert.AreEqual(3, VectorIndexQuery.limit query)
                let values = VectorIndexQuery.values query
                queriedVectors.Add values

                if values[0] = 1.0 then
                    Task.FromResult [ VectorIndexHit.create first profile.ProfileId 0.95 ]
                else
                    Task.FromResult [ VectorIndexHit.create second profile.ProfileId 0.85 ])

        let result =
            KnowledgeVectorRetrievalExecutor.executeAsync embedder index planned CancellationToken.None
            |> _.GetAwaiter().GetResult()

        match result with
        | KnowledgeVectorRetrievalResult.Skipped _ -> Assert.Fail "Vector retrieval was unexpectedly skipped."
        | KnowledgeVectorRetrievalResult.Retrieved retrieval ->
            Assert.AreEqual(1, embeddingCalls)
            CollectionAssert.AreEquivalent([| "original query"; "alternative query" |], embeddedTexts.ToArray())
            Assert.AreEqual(2, queriedVectors.Count)
            Assert.AreEqual(profile, KnowledgeVectorRetrieval.profile retrieval)
            Assert.AreEqual(usage, KnowledgeVectorRetrieval.embeddingUsage retrieval)
            Assert.AreEqual(2, KnowledgeVectorRetrieval.queryCount retrieval)
            Assert.AreEqual(2, KnowledgeVectorRetrieval.candidateCount retrieval)
            Assert.AreEqual(KnowledgeVectorRetrievalExecutor.descriptor, KnowledgeVectorRetrieval.executor retrieval)
            let filterEvidence = KnowledgeVectorRetrieval.filterEvidence retrieval
            Assert.AreSame(filter, KnowledgeRetrievalFilterEvidence.filter filterEvidence)
            Assert.AreEqual(2, KnowledgeRetrievalFilterEvidence.validatedCandidates filterEvidence)

            let candidates =
                KnowledgeVectorRetrieval.variants retrieval
                |> List.head
                |> KnowledgeVectorVariantEvidence.candidates

            Assert.AreEqual(1, candidates.Length)
            Assert.AreEqual(1, candidates[0] |> KnowledgeVectorCandidate.rank)
            Assert.AreEqual(0.95, candidates[0] |> KnowledgeVectorCandidate.score)
            Assert.AreEqual(profile.ProfileId, candidates[0] |> KnowledgeVectorCandidate.profileId)
            Assert.AreSame(first, candidates[0] |> KnowledgeVectorCandidate.document)

    [<TestMethod>]
    member _.``Vector retrieval rejects lexical-only requests and malformed embedding output``() =
        let limits = KnowledgeRetrievalLimits.create 100 200 2 2 2
        let budget = KnowledgeRetrievalBudget.create (TimeSpan.FromSeconds 1.0) 0 1 10 0.10m

        let lexical =
            understanding KnowledgeRetrievalStrategy.Lexical KnowledgeQueryTransformationStrategy.None limits budget []

        let mutable calls = 0

        let embedder =
            { Profile = profile
              EmbedQueriesAsync =
                fun _ request _ ->
                    calls <- calls + 1

                    KnowledgeQueryEmbeddingResult.create [] (KnowledgeQueryEmbeddingUsage.create 1 1 0m)
                    |> Task.FromResult }

        let emptyIndex = vectorIndex (fun _ _ _ -> Task.FromResult [])

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeVectorRetrievalExecutor.executeAsync embedder emptyIndex lexical CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        Assert.AreEqual(0, calls)

        let vector =
            understanding KnowledgeRetrievalStrategy.Vector KnowledgeQueryTransformationStrategy.None limits budget []

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeVectorRetrievalExecutor.executeAsync embedder emptyIndex vector CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        Assert.AreEqual(1, calls)

        let excessiveUsage =
            { Profile = profile
              EmbedQueriesAsync =
                fun _ request _ ->
                    embeddingResult [| [| 1.0; 0.0; 0.0 |] |] (KnowledgeQueryEmbeddingUsage.create 2 1 0m) request
                    |> Task.FromResult }

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeVectorRetrievalExecutor.executeAsync excessiveUsage emptyIndex vector CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let validEmbedder =
            { Profile = profile
              EmbedQueriesAsync =
                fun _ request _ ->
                    embeddingResult [| [| 1.0; 0.0; 0.0 |] |] (KnowledgeQueryEmbeddingUsage.create 1 1 0m) request
                    |> Task.FromResult }

        let otherProfile =
            EmbeddingCapabilityDescriptor.create
                (ComponentVersion.create "other-embedding-model" "1")
                3
                EmbeddingNormalization.UnitLength
                VectorDistanceMetric.Cosine
                (ComponentVersion.create "query-preprocessing" "1")
                profile.Limits

        let wrongProfileHit =
            VectorIndexHit.create (document sourceId "wrong-profile") otherProfile.ProfileId 1.0

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeVectorRetrievalExecutor.executeAsync
                validEmbedder
                (vectorIndex (fun _ _ _ -> Task.FromResult [ wrongProfileHit ]))
                vector
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Vector retrieval enforces aggregate candidate and duration budgets``() =
        let limits = KnowledgeRetrievalLimits.create 100 200 3 3 4

        let planned =
            understanding
                KnowledgeRetrievalStrategy.Vector
                KnowledgeQueryTransformationStrategy.MultiQuery
                limits
                (KnowledgeRetrievalBudget.create (TimeSpan.FromSeconds 1.0) 1 1 20 0.10m)
                [ "alternative query" ]

        let embedder =
            { Profile = profile
              EmbedQueriesAsync =
                fun _ request _ ->
                    embeddingResult
                        [| [| 1.0; 0.0; 0.0 |]; [| 0.0; 1.0; 0.0 |] |]
                        (KnowledgeQueryEmbeddingUsage.create 1 5 0.01m)
                        request
                    |> Task.FromResult }

        let hits =
            [ 1..3 ]
            |> List.map (fun value ->
                VectorIndexHit.create (document sourceId (string value)) profile.ProfileId (1.0 / float value))

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeVectorRetrievalExecutor.executeAsync
                embedder
                (vectorIndex (fun _ _ _ -> Task.FromResult hits))
                planned
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let timed =
            understanding
                KnowledgeRetrievalStrategy.Vector
                KnowledgeQueryTransformationStrategy.None
                limits
                (KnowledgeRetrievalBudget.create (TimeSpan.FromMilliseconds 20.0) 0 1 10 0.10m)
                []

        let cancelled =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let slowEmbedder =
            { Profile = profile
              EmbedQueriesAsync =
                fun _ _ cancellationToken ->
                    task {
                        use _registration =
                            cancellationToken.Register(fun () -> cancelled.TrySetResult() |> ignore)

                        do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        return Unchecked.defaultof<KnowledgeQueryEmbeddingResult>
                    } }

        Assert.ThrowsExactly<TimeoutException>(fun () ->
            KnowledgeVectorRetrievalExecutor.executeAsync
                slowEmbedder
                (vectorIndex (fun _ _ _ -> Task.FromResult []))
                timed
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        cancelled.Task.Wait(TimeSpan.FromSeconds 1.0) |> Assert.IsTrue
