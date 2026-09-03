namespace Nao.Agents.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass>]
type SemanticMemoryTests() =

    let agentId = "semantic-agent"
    let embeddingProvider = SimpleEmbeddingProvider.create ()

    [<TestMethod>]
    member _.StoreAndRetrieve_FindsRelevantMemory() =
        // Use a fresh embedding provider so all entries share the same vocabulary
        let provider = SimpleEmbeddingProvider.create ()
        let memory = InMemorySemanticMemory.create provider

        // Store entries with distinct vocabulary
        (memory.StoreAsync agentId "cat-fact" "cats are fluffy animals that purr").Result
        (memory.StoreAsync agentId "dog-fact" "dogs are loyal animals that bark").Result
        (memory.StoreAsync agentId "car-fact" "cars have engines and wheels for driving").Result

        // Query with words that overlap heavily with cat-fact
        let results = (memory.RetrieveAsync agentId "fluffy cats that purr" 2).Result
        Assert.IsTrue(results.Length > 0)
        Assert.AreEqual("cat-fact", results.[0].Key)

    [<TestMethod>]
    member _.StoreAndRetrieve_TopKLimitsResults() =
        let memory = InMemorySemanticMemory.create embeddingProvider
        (memory.StoreAsync agentId "a" "first memory content").Result
        (memory.StoreAsync agentId "b" "second memory content").Result
        (memory.StoreAsync agentId "c" "third memory content").Result

        let results = (memory.RetrieveAsync agentId "memory" 1).Result
        Assert.AreEqual(1, results.Length)

    [<TestMethod>]
    member _.Remove_DeletesMemory() =
        let memory = InMemorySemanticMemory.create embeddingProvider
        (memory.StoreAsync agentId "temp" "temporary data").Result
        (memory.RemoveAsync agentId "temp").Result

        let results = (memory.RetrieveAsync agentId "temporary" 10).Result
        Assert.AreEqual(0, results.Length)

    [<TestMethod>]
    member _.Store_OverwritesExistingKey() =
        let memory = InMemorySemanticMemory.create embeddingProvider
        (memory.StoreAsync agentId "fact" "old value").Result
        (memory.StoreAsync agentId "fact" "new updated value").Result

        let results = (memory.RetrieveAsync agentId "value" 10).Result
        Assert.AreEqual(1, results.Length)
        Assert.AreEqual("new updated value", results.[0].Content)

    [<TestMethod>]
    member _.Retrieve_ReturnsEmptyWhenNoMemories() =
        let memory = InMemorySemanticMemory.create embeddingProvider
        let results = (memory.RetrieveAsync agentId "anything" 5).Result
        Assert.AreEqual(0, results.Length)

    [<TestMethod>]
    member _.IsolatesBetweenAgents() =
        let memory = InMemorySemanticMemory.create embeddingProvider
        let agent1 = "agent-1"
        let agent2 = "agent-2"

        (memory.StoreAsync agent1 "secret" "agent 1 secret data").Result
        (memory.StoreAsync agent2 "secret" "agent 2 secret data").Result

        let r1 = (memory.RetrieveAsync agent1 "secret" 5).Result
        let r2 = (memory.RetrieveAsync agent2 "secret" 5).Result
        Assert.AreEqual(1, r1.Length)
        Assert.AreEqual("agent 1 secret data", r1.[0].Content)
        Assert.AreEqual("agent 2 secret data", r2.[0].Content)


[<TestClass>]
type CosineSimilarityTests() =

    [<TestMethod>]
    member _.IdenticalVectors_ReturnOne() =
        let v = [| 1.0; 2.0; 3.0 |]
        let result = SemanticSimilarity.cosineSimilarity v v
        Assert.IsTrue(abs(result - 1.0) < 0.001)

    [<TestMethod>]
    member _.OrthogonalVectors_ReturnZero() =
        let a = [| 1.0; 0.0 |]
        let b = [| 0.0; 1.0 |]
        let result = SemanticSimilarity.cosineSimilarity a b
        Assert.IsTrue(abs result < 0.001)

    [<TestMethod>]
    member _.EmptyVectors_ReturnZero() =
        let result = SemanticSimilarity.cosineSimilarity [||] [||]
        Assert.AreEqual(0.0, result)

    [<TestMethod>]
    member _.DifferentLengths_HandlesByZeroPadding() =
        let a = [| 1.0; 2.0 |]
        let b = [| 1.0; 2.0; 3.0 |]
        let result = SemanticSimilarity.cosineSimilarity a b
        // Should still compute similarity (zero-pad the shorter one)
        Assert.IsTrue(result > 0.0)
        Assert.IsTrue(result < 1.0)

    [<TestMethod>]
    member _.SimilarVectors_ReturnHighSimilarity() =
        let a = [| 1.0; 2.0; 3.0 |]
        let b = [| 1.1; 2.1; 3.1 |]
        let result = SemanticSimilarity.cosineSimilarity a b
        Assert.IsTrue(result > 0.99)
