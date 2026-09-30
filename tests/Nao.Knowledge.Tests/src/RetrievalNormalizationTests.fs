namespace Nao.Knowledge.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass>]
type RetrievalNormalizationTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

    let createRequest limits query =
        KnowledgeRetrievalRequest.create
            query
            authorization
            (KnowledgeIndexNamespace.parse "primary")
            KnowledgeIndexFilter.none
            KnowledgeTaskIntent.Unspecified
            KnowledgeRetrievalStrategy.Hybrid
            KnowledgeQueryTransformationStrategy.None
            limits
            KnowledgeRetrievalBudget.deterministic

    [<TestMethod>]
    member _.``Query normalization is deterministic and preserves request evidence``() =
        let original = " \tＦｕｌｌ\u00A0  Cafe\u0301\r\nTEST  "
        let request = createRequest KnowledgeRetrievalLimits.defaultLimits original
        let first = KnowledgeQueryNormalizer.normalize request
        let second = KnowledgeQueryNormalizer.normalize request

        Assert.AreEqual("Full Café TEST", KnowledgeNormalizedQuery.text first)
        Assert.AreEqual(KnowledgeNormalizedQuery.text first, KnowledgeNormalizedQuery.text second)
        Assert.AreSame(request, KnowledgeNormalizedQuery.request first)
        Assert.AreEqual(original, KnowledgeNormalizedQuery.originalQuery first)
        Assert.AreEqual(KnowledgeQueryNormalizer.descriptor, KnowledgeNormalizedQuery.normalizer first)
        Assert.AreEqual(ContentHash.computeText original, KnowledgeNormalizedQuery.originalHash first)

        Assert.AreEqual(ContentHash.computeText "Full Café TEST", KnowledgeNormalizedQuery.normalizedHash first)

        Assert.AreEqual(KnowledgeNormalizedQuery.normalizedHash first, KnowledgeNormalizedQuery.normalizedHash second)

    [<TestMethod>]
    member _.``Query normalization is idempotent``() =
        let limits = KnowledgeRetrievalLimits.defaultLimits

        let first =
            createRequest limits "  alpha\t beta  " |> KnowledgeQueryNormalizer.normalize

        let second =
            first
            |> KnowledgeNormalizedQuery.text
            |> createRequest limits
            |> KnowledgeQueryNormalizer.normalize

        Assert.AreEqual(KnowledgeNormalizedQuery.text first, KnowledgeNormalizedQuery.text second)

        Assert.AreEqual(KnowledgeNormalizedQuery.normalizedHash first, KnowledgeNormalizedQuery.normalizedHash second)

    [<TestMethod>]
    member _.``Query normalization rejects controls and compatibility expansion beyond limits``() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            createRequest KnowledgeRetrievalLimits.defaultLimits "alpha\u0000beta"
            |> KnowledgeQueryNormalizer.normalize
            |> ignore)
        |> ignore

        let limits = KnowledgeRetrievalLimits.create 2 4 1 1 1

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            createRequest limits "ﬃ" |> KnowledgeQueryNormalizer.normalize |> ignore)
        |> ignore
