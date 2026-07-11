namespace Nao.Agents.Tests

open System
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Agents

[<TestClass>]
type ContextCompactionTests() =

    let makeMsg role content = { Role = role; Content = content }

    [<TestMethod>]
    member _.EstimateTokensUsesCharHeuristic() =
        let msg = makeMsg User "Hello world" // 11 chars -> ~2 tokens
        let tokens = ContextCompaction.estimateTokens msg
        Assert.AreEqual(2, tokens) // 11/4 = 2

    [<TestMethod>]
    member _.EstimateConversationTokensSumsAll() =
        let conv = [ makeMsg User "Hello"; makeMsg Assistant "Hi there" ] // 5 + 8 = 13 / 4 = 3
        let tokens = ContextCompaction.estimateConversationTokens conv
        Assert.AreEqual(3, tokens)

    [<TestMethod>]
    member _.DropOldestKeepsWithinBudget() =
        // Create a large conversation
        let conv = [ for i in 1..10 -> makeMsg User (String.replicate 100 "x") ] // 100 chars each = 25 tokens each
        let result = ContextCompaction.dropOldest 50 conv // budget 50 tokens -> keeps 2 messages
        Assert.IsTrue(result.Compacted.Length < conv.Length)
        Assert.IsTrue(result.MessagesRemoved > 0)
        Assert.IsTrue(result.TokensSaved > 0)

    [<TestMethod>]
    member _.DropOldestKeepsAllIfUnderBudget() =
        let conv = [ makeMsg User "Hi"; makeMsg Assistant "Hey" ]
        let result = ContextCompaction.dropOldest 10000 conv
        Assert.AreEqual(conv.Length, result.Compacted.Length)
        Assert.AreEqual(0, result.MessagesRemoved)

    [<TestMethod>]
    member _.ApplyAsyncDropOldestStrategy() =
        let conv = [ for i in 1..20 -> makeMsg User (String.replicate 40 "a") ] // 10 tokens each = 200 total
        let result = (ContextCompaction.applyAsync CompactionStrategy.DropOldest 50 conv).Result
        Assert.IsTrue(result.Compacted.Length <= 5)
        Assert.IsTrue(result.MessagesRemoved > 0)

    [<TestMethod>]
    member _.ApplyAsyncRelevanceFilterStrategy() =
        let conv =
            [ makeMsg User "important data here"
              makeMsg User "x"
              makeMsg User "another important thing"
              makeMsg User "y" ]
        // Keep only messages with content length > 5
        let scorer = fun (msg: Message) -> if msg.Content.Length > 5 then 1.0 else 0.0
        let strategy = CompactionStrategy.RelevanceFilter (scorer, 0.5)
        let result = (ContextCompaction.applyAsync strategy 10000 conv).Result
        Assert.AreEqual(2, result.Compacted.Length)
        Assert.AreEqual(2, result.MessagesRemoved)

[<TestClass>]
type MemoryTierTests() =

    [<TestMethod>]
    member _.TieredMemoryEntryHasCorrectTier() =
        let entry =
            { Key = "fact"; Value = "Earth is round"
              Tier = MemoryTier.LongTerm
              Timestamp = DateTimeOffset.UtcNow
              AccessCount = 0; Relevance = 0.9; Tags = ["science"] }
        Assert.AreEqual(MemoryTier.LongTerm, entry.Tier)
        Assert.AreEqual("fact", entry.Key)

    [<TestMethod>]
    member _.TieredMemoryConfigDefaults() =
        let config = TieredMemoryConfig.Default
        Assert.AreEqual(20, config.ShortTermCapacity)
        Assert.AreEqual(100, config.MidTermCapacity)
        Assert.IsTrue(config.AutoEvict)
