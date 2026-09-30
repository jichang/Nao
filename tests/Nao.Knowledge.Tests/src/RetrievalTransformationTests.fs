namespace Nao.Knowledge.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type RetrievalTransformationTests() =
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

    let limits = KnowledgeRetrievalLimits.create 100 300 4 10 20

    let budget =
        KnowledgeRetrievalBudget.create (TimeSpan.FromSeconds 2.0) 1 0 100 0.01m

    let request strategy =
        KnowledgeRetrievalRequest.create
            "  ORIGINAL query "
            authorization
            (KnowledgeIndexNamespace.parse "primary")
            (KnowledgeIndexFilter.create [ KnowledgeSourceId.parse "source" ] [] Map.empty None None None None)
            KnowledgeTaskIntent.KnowledgeSeeking
            KnowledgeRetrievalStrategy.Hybrid
            strategy
            limits
            budget
        |> KnowledgeQueryNormalizer.normalize

    let usage =
        { ModelCalls = 1
          InputTokens = 10
          OutputTokens = 20
          EstimatedCostUsd = 0.005m }

    let descriptor =
        { Model = ComponentVersion.create "query-model" "1"
          ConfigurationHash = ContentHash.computeText "query-prompt-v1"
          Deterministic = true }

    let run model normalized =
        KnowledgeQueryTransformation.transformAsync model normalized CancellationToken.None
        |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``No transformation preserves only the normalized original without a model call``() =
        let normalized = request KnowledgeQueryTransformationStrategy.None
        let plan = run None normalized
        let variants = KnowledgeQueryTransformationPlan.variants plan

        Assert.AreSame(normalized, KnowledgeQueryTransformationPlan.normalizedQuery plan)
        Assert.AreEqual(KnowledgeQueryTransformationUsage.zero, KnowledgeQueryTransformationPlan.usage plan)
        Assert.AreEqual(1, variants.Length)
        Assert.AreEqual(KnowledgeQueryVariantKind.Original, variants |> List.head |> KnowledgeQueryVariant.kind)
        Assert.AreEqual("ORIGINAL query", variants |> List.head |> KnowledgeQueryVariant.text)

    [<TestMethod>]
    member _.``Every model strategy returns normalized provenance-bound variants``() =
        let cases =
            [ KnowledgeQueryTransformationStrategy.Rewrite, [ " rewritten\tquery " ], KnowledgeQueryVariantKind.Rewrite
              KnowledgeQueryTransformationStrategy.Decompose,
              [ " first part "; "second\u00A0part" ],
              KnowledgeQueryVariantKind.Decomposition
              KnowledgeQueryTransformationStrategy.HypotheticalDocument,
              [ String.replicate 120 "x" ],
              KnowledgeQueryVariantKind.HypotheticalDocument
              KnowledgeQueryTransformationStrategy.MultiQuery,
              [ " alternative one "; "alternative   two" ],
              KnowledgeQueryVariantKind.MultiQuery ]

        for strategy, outputs, expectedKind in cases do
            let normalized = request strategy
            let mutable captured = None

            let model =
                { Descriptor = descriptor
                  TransformAsync =
                    fun actualAuthorization input cancellationToken ->
                        cancellationToken.ThrowIfCancellationRequested()
                        Assert.AreEqual(authorization, actualAuthorization)
                        Assert.AreEqual(strategy, input.Strategy)
                        Assert.AreEqual("ORIGINAL query", input.Query)
                        Assert.AreEqual(3, input.MaximumGeneratedVariants)
                        captured <- Some input
                        Task.FromResult { Variants = outputs; Usage = usage } }

            let plan = run (Some model) normalized
            let variants = KnowledgeQueryTransformationPlan.variants plan
            Assert.AreSame(normalized, KnowledgeQueryTransformationPlan.normalizedQuery plan)
            Assert.AreSame(KnowledgeNormalizedQuery.request normalized, KnowledgeQueryTransformationPlan.request plan)
            Assert.AreEqual(usage, KnowledgeQueryTransformationPlan.usage plan)
            Assert.AreEqual(outputs.Length + 1, variants.Length)
            Assert.AreEqual(KnowledgeQueryVariantKind.Original, variants[0] |> KnowledgeQueryVariant.kind)

            variants
            |> List.tail
            |> List.iteri (fun index variant ->
                Assert.AreEqual(expectedKind, KnowledgeQueryVariant.kind variant)
                Assert.AreEqual(index + 1, KnowledgeQueryVariant.ordinal variant)
                Assert.AreEqual(Some descriptor, KnowledgeQueryVariant.generatedBy variant)

                Assert.AreEqual(
                    KnowledgeQueryVariant.text variant |> ContentHash.computeText,
                    KnowledgeQueryVariant.contentHash variant
                ))

            let captured = captured |> Option.get

            match strategy with
            | KnowledgeQueryTransformationStrategy.HypotheticalDocument ->
                Assert.AreEqual(300, captured.MaximumGeneratedCharacters)
            | _ -> Assert.AreEqual(100, captured.MaximumGeneratedCharacters)

    [<TestMethod>]
    member _.``Transformation rejects missing capacity duplicate output and budget violations``() =
        let zeroBudget = KnowledgeRetrievalBudget.create (TimeSpan.FromSeconds 1.0) 0 0 0 0m

        let zeroBudgetRequest =
            KnowledgeRetrievalRequest.create
                "query"
                authorization
                (KnowledgeIndexNamespace.parse "primary")
                KnowledgeIndexFilter.none
                KnowledgeTaskIntent.KnowledgeSeeking
                KnowledgeRetrievalStrategy.Lexical
                KnowledgeQueryTransformationStrategy.Rewrite
                limits
                zeroBudget
            |> KnowledgeQueryNormalizer.normalize

        Assert.ThrowsExactly<InvalidOperationException>(fun () -> run None zeroBudgetRequest |> ignore)
        |> ignore

        let model outputs actualUsage =
            { Descriptor = descriptor
              TransformAsync =
                fun _ _ _ ->
                    Task.FromResult
                        { Variants = outputs
                          Usage = actualUsage } }

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            run (Some(model [ "ORIGINAL query" ] usage)) (request KnowledgeQueryTransformationStrategy.Rewrite)
            |> ignore)
        |> ignore

        let excessiveUsage =
            { usage with
                OutputTokens = 100
                EstimatedCostUsd = 0.02m }

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            run (Some(model [ "rewritten" ] excessiveUsage)) (request KnowledgeQueryTransformationStrategy.Rewrite)
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Transformation enforces duration budget and cancels the model``() =
        let shortBudget =
            KnowledgeRetrievalBudget.create (TimeSpan.FromMilliseconds 50.0) 1 0 100 0.01m

        let normalized =
            KnowledgeRetrievalRequest.create
                "query"
                authorization
                (KnowledgeIndexNamespace.parse "primary")
                KnowledgeIndexFilter.none
                KnowledgeTaskIntent.KnowledgeSeeking
                KnowledgeRetrievalStrategy.Lexical
                KnowledgeQueryTransformationStrategy.Rewrite
                limits
                shortBudget
            |> KnowledgeQueryNormalizer.normalize

        let mutable cancelled = false

        let model =
            { Descriptor = descriptor
              TransformAsync =
                fun _ _ cancellationToken ->
                    cancellationToken.Register(fun () -> cancelled <- true) |> ignore

                    task {
                        do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)

                        return
                            { Variants = [ "unreachable" ]
                              Usage = usage }
                    } }

        Assert.ThrowsExactly<TimeoutException>(fun () -> run (Some model) normalized |> ignore)
        |> ignore

        Assert.IsTrue(cancelled)
