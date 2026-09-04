namespace Nao.Agents.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Agents

[<TestClass>]
type ConversationWindowTests() =

    let makeMsg role content = { Role = role; Content = content }

    let sampleConversation =
        [ makeMsg System "You are helpful."
          makeMsg User "Hello"
          makeMsg Assistant "Hi there!"
          makeMsg User "What is 2+2?"
          makeMsg Assistant "4"
          makeMsg User "Thanks"
          makeMsg Assistant "You're welcome!" ]

    [<TestMethod>]
    member _.LastN_KeepsOnlyLastNMessages() =
        let result = ConversationWindow.applyLastN 3 sampleConversation
        Assert.AreEqual(3, result.Length)
        // Keeps last 3: index 4="4", 5="Thanks", 6="You're welcome!"
        Assert.AreEqual("4", result.[0].Content)
        Assert.AreEqual("Thanks", result.[1].Content)
        Assert.AreEqual("You're welcome!", result.[2].Content)

    [<TestMethod>]
    member _.LastN_ReturnsAllWhenFewerThanN() =
        let result = ConversationWindow.applyLastN 100 sampleConversation
        Assert.AreEqual(7, result.Length)

    [<TestMethod>]
    member _.LastN_ReturnsEmptyForZero() =
        let result = ConversationWindow.applyLastN 0 sampleConversation
        Assert.AreEqual(0, result.Length)

    [<TestMethod>]
    member _.TokenBudget_KeepsMessagesWithinBudget() =
        // Each message is roughly (content.Length + 3) / 4 tokens
        // "You're welcome!" = 19 chars -> ~5 tokens
        // "Thanks" = 6 chars -> ~2 tokens
        // Budget of 10 should keep the last few messages
        let result = ConversationWindow.applyTokenBudget 10 sampleConversation
        Assert.IsTrue(result.Length > 0)
        Assert.IsTrue(result.Length < sampleConversation.Length)
        // Last message should always be included
        Assert.AreEqual("You're welcome!", (result |> List.last).Content)

    [<TestMethod>]
    member _.TokenBudget_KeepsAllWhenBudgetIsLarge() =
        let result = ConversationWindow.applyTokenBudget 10000 sampleConversation
        Assert.AreEqual(7, result.Length)

    [<TestMethod>]
    member _.PartitionForSummary_SplitsCorrectly() =
        let (toSummarize, recent) =
            ConversationWindow.partitionForSummary 3 sampleConversation

        Assert.AreEqual(4, toSummarize.Length)
        Assert.AreEqual(3, recent.Length)
        // Recent keeps last 3: "4", "Thanks", "You're welcome!"
        Assert.AreEqual("4", recent.[0].Content)

    [<TestMethod>]
    member _.PartitionForSummary_NoSplitWhenBelowThreshold() =
        let (toSummarize, recent) =
            ConversationWindow.partitionForSummary 100 sampleConversation

        Assert.AreEqual(0, toSummarize.Length)
        Assert.AreEqual(7, recent.Length)

    [<TestMethod>]
    member _.Apply_LastNStrategy() =
        let result = ConversationWindow.apply (LastN 2) sampleConversation
        Assert.AreEqual(2, result.Length)

    [<TestMethod>]
    member _.Apply_TokenBudgetStrategy() =
        let result = ConversationWindow.apply (TokenBudget 5) sampleConversation
        Assert.IsTrue(result.Length > 0)
        Assert.IsTrue(result.Length < 7)

    [<TestMethod>]
    member _.Apply_SummarizeAfterStrategy_WithoutProvider() =
        // Without a provider, SummarizeAfter just keeps recent messages
        let result = ConversationWindow.apply (SummarizeAfter 3) sampleConversation
        Assert.AreEqual(3, result.Length)

    [<TestMethod>]
    member _.EstimateTokens_ReturnsReasonableValue() =
        let msg = makeMsg User "Hello world"
        let tokens = ConversationWindow.estimateTokens msg
        // "Hello world" = 11 chars -> (11+3)/4 = 3 tokens
        Assert.AreEqual(3, tokens)
