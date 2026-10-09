namespace Nao.Knowledge.Tests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type LexicalRetrievalTests() =
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

    let document namespaceValue sourceValue suffix =
        let text = "content " + suffix
        let contentHash = ContentHash.computeText text

        let documentVersionId =
            KnowledgeDocumentVersionId.derive (KnowledgeDocumentId.parse ("document-" + suffix)) 1 contentHash

        let chunkId = KnowledgeChunkId.derive documentVersionId "test-chunker" 0 contentHash
        let now = DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero)

        KnowledgeIndexDocument.restore
            namespaceValue
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
                                      EstimatedCostUsd = 0m } } }

        KnowledgeQueryUnderstanding.planAsync model normalized CancellationToken.None
        |> _.GetAwaiter().GetResult()

    let index queryAsync =
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

    [<TestMethod>]
    member _.``Skipped understanding never invokes the lexical index``() =
        let normalized =
            KnowledgeRetrievalRequest.create
                "query"
                authorization
                namespaceName
                filter
                KnowledgeTaskIntent.NonKnowledge
                KnowledgeRetrievalStrategy.Lexical
                KnowledgeQueryTransformationStrategy.None
                KnowledgeRetrievalLimits.defaultLimits
                KnowledgeRetrievalBudget.deterministic
            |> KnowledgeQueryNormalizer.normalize

        let skipped =
            KnowledgeQueryUnderstanding.planAsync None normalized CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let mutable calls = 0

        let result =
            KnowledgeLexicalRetrievalExecutor.executeAsync
                (index (fun _ _ _ ->
                    calls <- calls + 1
                    Task.FromResult []))
                skipped
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(0, calls)

        match result, skipped with
        | KnowledgeLexicalRetrievalResult.Skipped actual, KnowledgeQueryUnderstandingResult.RetrievalSkipped expected ->
            Assert.AreSame(expected, actual)
        | _ -> Assert.Fail "Explicit non-knowledge intent must remain skipped."

        match
            KnowledgeLexicalRetrievalExecutor.executeAsync
                Unchecked.defaultof<KnowledgeLexicalIndex>
                skipped
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
        with
        | KnowledgeLexicalRetrievalResult.Skipped _ -> ()
        | KnowledgeLexicalRetrievalResult.Retrieved _ -> Assert.Fail "Skipped retrieval must not require an index."

    [<TestMethod>]
    member _.``Lexical retrieval executes each variant once with immutable boundaries and ranked evidence``() =
        let limits = KnowledgeRetrievalLimits.create 100 200 4 3 6

        let understanding =
            understanding
                KnowledgeRetrievalStrategy.Hybrid
                KnowledgeQueryTransformationStrategy.MultiQuery
                limits
                (KnowledgeRetrievalBudget.create (TimeSpan.FromSeconds 1.0) 1 0 20 0m)
                [ "alternative query" ]

        let calls = ConcurrentBag<string>()
        let first = document namespaceName sourceId "first"
        let second = document namespaceName sourceId "second"

        let lexicalIndex =
            index (fun actualAuthorization query cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()
                Assert.AreSame(authorization, actualAuthorization)
                Assert.AreEqual(namespaceName, LexicalIndexQuery.namespaceName query)
                Assert.AreSame(filter, LexicalIndexQuery.filter query)
                Assert.AreEqual(3, LexicalIndexQuery.limit query)
                let text = LexicalIndexQuery.query query
                calls.Add text

                match text with
                | "original query" ->
                    Task.FromResult [ LexicalIndexHit.create first 0.9; LexicalIndexHit.create second 0.7 ]
                | "alternative query" -> Task.FromResult [ LexicalIndexHit.create second 0.8 ]
                | _ -> invalidOp "Unexpected lexical query.")

        let result =
            KnowledgeLexicalRetrievalExecutor.executeAsync lexicalIndex understanding CancellationToken.None
            |> _.GetAwaiter().GetResult()

        match result with
        | KnowledgeLexicalRetrievalResult.Skipped _ -> Assert.Fail "Retrieval was unexpectedly skipped."
        | KnowledgeLexicalRetrievalResult.Retrieved retrieval ->
            let plan =
                match understanding with
                | KnowledgeQueryUnderstandingResult.RetrievalPlanned(_, plan) -> plan
                | _ -> invalidOp "Expected a retrieval plan."

            Assert.AreSame(plan, KnowledgeLexicalRetrieval.plan retrieval)
            Assert.AreSame(KnowledgeQueryTransformationPlan.request plan, KnowledgeLexicalRetrieval.request retrieval)
            Assert.AreEqual(KnowledgeLexicalRetrievalExecutor.descriptor, KnowledgeLexicalRetrieval.executor retrieval)
            Assert.AreEqual(2, calls.Count)
            CollectionAssert.AreEquivalent([| "original query"; "alternative query" |], calls.ToArray())
            Assert.AreEqual(2, KnowledgeLexicalRetrieval.queryCount retrieval)
            Assert.AreEqual(3, KnowledgeLexicalRetrieval.candidateCount retrieval)
            Assert.AreEqual(0m, KnowledgeLexicalRetrieval.estimatedCostUsd retrieval)
            let filterEvidence = KnowledgeLexicalRetrieval.filterEvidence retrieval
            Assert.AreSame(filter, KnowledgeRetrievalFilterEvidence.filter filterEvidence)
            Assert.AreEqual(3, KnowledgeRetrievalFilterEvidence.validatedCandidates filterEvidence)

            let variants = KnowledgeLexicalRetrieval.variants retrieval
            Assert.AreEqual(2, variants.Length)
            let candidates = variants[0] |> KnowledgeLexicalVariantEvidence.candidates
            Assert.AreEqual(2, candidates.Length)
            Assert.AreEqual(1, candidates[0] |> KnowledgeLexicalCandidate.rank)
            Assert.AreEqual(0.9, candidates[0] |> KnowledgeLexicalCandidate.score)
            Assert.AreSame(first, candidates[0] |> KnowledgeLexicalCandidate.document)

    [<TestMethod>]
    member _.``Lexical retrieval rejects vector-only requests and invalid adapter output``() =
        let limits = KnowledgeRetrievalLimits.create 100 200 2 2 2
        let budget = KnowledgeRetrievalBudget.create (TimeSpan.FromSeconds 1.0) 0 0 0 0m

        let vectorOnly =
            understanding KnowledgeRetrievalStrategy.Vector KnowledgeQueryTransformationStrategy.None limits budget []

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeLexicalRetrievalExecutor.executeAsync
                (index (fun _ _ _ -> Task.FromResult []))
                vectorOnly
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let lexical =
            understanding KnowledgeRetrievalStrategy.Lexical KnowledgeQueryTransformationStrategy.None limits budget []

        let nullHits =
            index (fun _ _ _ -> Task.FromResult(Unchecked.defaultof<LexicalIndexHit list>))

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeLexicalRetrievalExecutor.executeAsync nullHits lexical CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let wrongSource =
            document namespaceName (KnowledgeSourceId.parse "other-source") "wrong-source"

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeLexicalRetrievalExecutor.executeAsync
                (index (fun _ _ _ -> Task.FromResult [ LexicalIndexHit.create wrongSource 1.0 ]))
                lexical
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Lexical retrieval enforces aggregate candidate and duration budgets``() =
        let limits = KnowledgeRetrievalLimits.create 100 200 3 3 4

        let planned =
            understanding
                KnowledgeRetrievalStrategy.Lexical
                KnowledgeQueryTransformationStrategy.MultiQuery
                limits
                (KnowledgeRetrievalBudget.create (TimeSpan.FromSeconds 1.0) 1 0 20 0m)
                [ "alternative query" ]

        let hits =
            [ 1..3 ]
            |> List.map (fun value ->
                LexicalIndexHit.create (document namespaceName sourceId (string value)) (1.0 / float value))

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeLexicalRetrievalExecutor.executeAsync
                (index (fun _ _ _ -> Task.FromResult hits))
                planned
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let timeoutBudget =
            KnowledgeRetrievalBudget.create (TimeSpan.FromMilliseconds 20.0) 0 0 0 0m

        let timed =
            understanding
                KnowledgeRetrievalStrategy.Lexical
                KnowledgeQueryTransformationStrategy.None
                limits
                timeoutBudget
                []

        let cancelled =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let slow =
            index (fun _ _ cancellationToken ->
                task {
                    use _registration =
                        cancellationToken.Register(fun () -> cancelled.TrySetResult() |> ignore)

                    do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    return []
                })

        Assert.ThrowsExactly<TimeoutException>(fun () ->
            KnowledgeLexicalRetrievalExecutor.executeAsync slow timed CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        cancelled.Task.Wait(TimeSpan.FromSeconds 1.0) |> Assert.IsTrue
