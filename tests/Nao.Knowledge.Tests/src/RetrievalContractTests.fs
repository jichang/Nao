namespace Nao.Knowledge.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass>]
type RetrievalContractTests() =
    let tenant = TenantId.parse "retrieval-tenant"
    let user = UserId.parse "retrieval-user"
    let workspace = WorkspaceId.parse "retrieval-workspace"
    let namespaceName = KnowledgeIndexNamespace.parse "primary"

    let authorization =
        SecurityPrincipal.createAuthenticated
            tenant
            user
            (Some(ServiceId.parse "retrieval-service"))
            [ GroupId.parse "retrieval-group" ]
            [ "knowledge-reader" ]
            Map.empty
            AuthenticationStrength.MultiFactor
        |> fun principal ->
            AuthorizationScope.tryCreate
                principal
                (Some(GroupId.parse "retrieval-group"))
                workspace
                (Some(SessionId.parse "retrieval-session"))
            |> Option.get

    [<TestMethod>]
    member _.``Retrieval request preserves query authorization filters strategy limits and budget``() =
        let sourceId = KnowledgeSourceId.parse "source"

        let filter =
            KnowledgeIndexFilter.create [ sourceId ] [] Map.empty None None None None

        let limits = KnowledgeRetrievalLimits.create 100 200 4 10 20

        let budget =
            KnowledgeRetrievalBudget.create (TimeSpan.FromSeconds 2.0) 1 1 500 0.01m

        let request =
            KnowledgeRetrievalRequest.create
                "  Original query  "
                authorization
                namespaceName
                filter
                KnowledgeTaskIntent.KnowledgeSeeking
                KnowledgeRetrievalStrategy.Hybrid
                KnowledgeQueryTransformationStrategy.Rewrite
                limits
                budget

        Assert.AreEqual("  Original query  ", KnowledgeRetrievalRequest.query request)
        Assert.AreEqual(authorization, KnowledgeRetrievalRequest.authorization request)
        Assert.AreEqual(AuthorizationScope.identity authorization, KnowledgeRetrievalRequest.identity request)
        Assert.AreEqual(namespaceName, KnowledgeRetrievalRequest.namespaceName request)
        Assert.AreSame(filter, KnowledgeRetrievalRequest.filter request)
        Assert.AreEqual(KnowledgeTaskIntent.KnowledgeSeeking, KnowledgeRetrievalRequest.intent request)
        Assert.AreEqual(KnowledgeRetrievalStrategy.Hybrid, KnowledgeRetrievalRequest.strategy request)
        Assert.AreEqual(KnowledgeQueryTransformationStrategy.Rewrite, KnowledgeRetrievalRequest.transformation request)

        Assert.AreSame(limits, KnowledgeRetrievalRequest.limits request)
        Assert.AreSame(budget, KnowledgeRetrievalRequest.budget request)

    [<TestMethod>]
    member _.``Retrieval request rejects blank and over-limit queries``() =
        let limits = KnowledgeRetrievalLimits.create 4 8 1 1 1

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeRetrievalRequest.create
                " "
                authorization
                namespaceName
                KnowledgeIndexFilter.none
                KnowledgeTaskIntent.Unspecified
                KnowledgeRetrievalStrategy.Lexical
                KnowledgeQueryTransformationStrategy.None
                limits
                KnowledgeRetrievalBudget.deterministic
            |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeRetrievalRequest.create
                "12345"
                authorization
                namespaceName
                KnowledgeIndexFilter.none
                KnowledgeTaskIntent.Unspecified
                KnowledgeRetrievalStrategy.Lexical
                KnowledgeQueryTransformationStrategy.None
                limits
                KnowledgeRetrievalBudget.deterministic
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Retrieval limits and budgets fail closed``() =
        Assert.ThrowsExactly<ArgumentException>(fun () -> KnowledgeRetrievalLimits.create 100 200 1 11 10 |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeRetrievalBudget.create TimeSpan.Zero 0 0 0 0m |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            KnowledgeRetrievalBudget.create (TimeSpan.FromSeconds 1.0) 0 0 0 -0.01m
            |> ignore)
        |> ignore
