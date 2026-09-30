namespace Nao.Eval.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval
open Nao.Knowledge

[<TestClass>]
type KnowledgeRetrievalComparisonTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

    let chunk suffix =
        KnowledgeChunkId.parse ("chunk_" + String.replicate 63 "0" + suffix)

    let plan strategy outputs =
        let request =
            KnowledgeRetrievalRequest.create
                "original query"
                authorization
                (KnowledgeIndexNamespace.parse "primary")
                (KnowledgeIndexFilter.create [ KnowledgeSourceId.parse "source" ] [] Map.empty None None None None)
                KnowledgeTaskIntent.KnowledgeSeeking
                KnowledgeRetrievalStrategy.Hybrid
                strategy
                KnowledgeRetrievalLimits.defaultLimits
                (KnowledgeRetrievalBudget.create (TimeSpan.FromSeconds 1.0) 1 0 100 0.01m)
            |> KnowledgeQueryNormalizer.normalize

        let model =
            { Descriptor =
                { Model = ComponentVersion.create "query-model" "1"
                  ConfigurationHash = ContentHash.computeText "prompt"
                  Deterministic = true }
              TransformAsync =
                fun _ _ _ ->
                    Task.FromResult
                        { Variants = outputs
                          Usage =
                            { ModelCalls = 1
                              InputTokens = 5
                              OutputTokens = 5
                              EstimatedCostUsd = 0m } } }

        KnowledgeQueryTransformation.transformAsync (Some model) request CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Evaluation compares original with aggregate transformed retrieval``() =
        let plan =
            plan KnowledgeQueryTransformationStrategy.MultiQuery [ "alternative one"; "alternative two" ]

        let request = KnowledgeQueryTransformationPlan.request plan
        let requests = Collections.Concurrent.ConcurrentBag<KnowledgeRetrievalRequest>()

        let retriever =
            { RetrieveAsync =
                fun actualRequest variant _ ->
                    requests.Add actualRequest

                    match KnowledgeQueryVariant.ordinal variant with
                    | 0 -> Task.FromResult [ chunk "1"; chunk "4" ]
                    | 1 -> Task.FromResult [ chunk "1"; chunk "2"; chunk "4" ]
                    | 2 -> Task.FromResult [ chunk "3" ]
                    | ordinal -> invalidOp $"Unexpected query ordinal {ordinal}." }

        let comparison =
            KnowledgeRetrievalComparison.evaluateAsync
                retriever
                plan
                [ chunk "1"; chunk "2"; chunk "3" ]
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(3, requests.Count)
        requests |> Seq.iter (fun actual -> Assert.AreSame(request, actual))
        Assert.AreSame(plan, KnowledgeRetrievalComparison.plan comparison)
        Assert.AreSame(request, KnowledgeRetrievalComparison.request comparison)
        Assert.AreEqual(3, KnowledgeRetrievalComparison.variants comparison |> List.length)

        let original = KnowledgeRetrievalComparison.original comparison
        Assert.AreEqual(1.0 / 3.0, KnowledgeRetrievalQualityMeasurement.recall original, 0.000001)
        Assert.AreEqual(0.5, KnowledgeRetrievalQualityMeasurement.precision original, 0.000001)

        let transformed = KnowledgeRetrievalComparison.transformed comparison
        Assert.AreEqual(2, KnowledgeRetrievalQualityMeasurement.queryCount transformed)
        Assert.AreEqual(1.0, KnowledgeRetrievalQualityMeasurement.recall transformed, 0.000001)
        Assert.AreEqual(0.75, KnowledgeRetrievalQualityMeasurement.precision transformed, 0.000001)
        Assert.AreEqual(2.0 / 3.0, KnowledgeRetrievalComparison.recallDelta comparison, 0.000001)
        Assert.AreEqual(0.25, KnowledgeRetrievalComparison.precisionDelta comparison, 0.000001)

    [<TestMethod>]
    member _.``Evaluation without generated variants compares original to itself``() =
        let request =
            KnowledgeRetrievalRequest.create
                "original"
                authorization
                (KnowledgeIndexNamespace.parse "primary")
                KnowledgeIndexFilter.none
                KnowledgeTaskIntent.Unspecified
                KnowledgeRetrievalStrategy.Lexical
                KnowledgeQueryTransformationStrategy.None
                KnowledgeRetrievalLimits.defaultLimits
                KnowledgeRetrievalBudget.deterministic
            |> KnowledgeQueryNormalizer.normalize

        let plan =
            KnowledgeQueryTransformation.transformAsync None request CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let retriever = { RetrieveAsync = fun _ _ _ -> Task.FromResult [ chunk "1" ] }

        let comparison =
            KnowledgeRetrievalComparison.evaluateAsync retriever plan [ chunk "1" ] CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(0.0, KnowledgeRetrievalComparison.recallDelta comparison)
        Assert.AreEqual(0.0, KnowledgeRetrievalComparison.precisionDelta comparison)

        Assert.AreEqual(
            KnowledgeRetrievalComparison.original comparison,
            KnowledgeRetrievalComparison.transformed comparison
        )

    [<TestMethod>]
    member _.``Evaluation rejects missing relevance evidence and null retrieval results``() =
        let plan = plan KnowledgeQueryTransformationStrategy.Rewrite [ "rewrite" ]

        let retriever =
            { RetrieveAsync = fun _ _ _ -> Task.FromResult(Unchecked.defaultof<KnowledgeChunkId list>) }

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeRetrievalComparison.evaluateAsync retriever plan [] CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeRetrievalComparison.evaluateAsync retriever plan [ chunk "1" ] CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let excessive =
            { RetrieveAsync =
                fun _ _ _ ->
                    [ 1..51 ]
                    |> List.map (fun value -> KnowledgeChunkId.parse ("chunk_" + value.ToString("x64")))
                    |> Task.FromResult }

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            KnowledgeRetrievalComparison.evaluateAsync excessive plan [ chunk "1" ] CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore
