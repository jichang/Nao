namespace Nao.Agents.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass>]
type MemoryStoreTests() =

    let agentId = "test-agent"

    let makeEntry key value =
        { Key = key
          Value = value
          Timestamp = DateTimeOffset.UtcNow
          Tags = [] }

    [<TestMethod>]
    member _.SaveAndRecall_ReturnsSavedEntry() =
        let store = InMemoryStore.create ()
        let entry = makeEntry "user-name" "Alice"
        store.SaveAsync agentId entry |> fun t -> t.Wait()

        let results = (store.RecallAsync agentId "user-name").Result
        Assert.AreEqual(1, results.Length)
        Assert.AreEqual("Alice", results.[0].Value)

    [<TestMethod>]
    member _.SaveMultiple_RecallsByPrefix() =
        let store = InMemoryStore.create ()
        store.SaveAsync agentId (makeEntry "user-name" "Alice") |> fun t -> t.Wait()
        store.SaveAsync agentId (makeEntry "user-email" "alice@example.com") |> fun t -> t.Wait()
        store.SaveAsync agentId (makeEntry "preference-theme" "dark") |> fun t -> t.Wait()

        let userResults = (store.RecallAsync agentId "user").Result
        Assert.AreEqual(2, userResults.Length)

        let prefResults = (store.RecallAsync agentId "preference").Result
        Assert.AreEqual(1, prefResults.Length)

    [<TestMethod>]
    member _.Save_OverwritesExistingKey() =
        let store = InMemoryStore.create ()
        store.SaveAsync agentId (makeEntry "name" "Alice") |> fun t -> t.Wait()
        store.SaveAsync agentId (makeEntry "name" "Bob") |> fun t -> t.Wait()

        let results = (store.RecallAsync agentId "name").Result
        Assert.AreEqual(1, results.Length)
        Assert.AreEqual("Bob", results.[0].Value)

    [<TestMethod>]
    member _.Forget_RemovesEntry() =
        let store = InMemoryStore.create ()
        store.SaveAsync agentId (makeEntry "temp" "value") |> fun t -> t.Wait()
        store.ForgetAsync agentId "temp" |> fun t -> t.Wait()

        let results = (store.RecallAsync agentId "temp").Result
        Assert.AreEqual(0, results.Length)

    [<TestMethod>]
    member _.Clear_RemovesAllEntries() =
        let store = InMemoryStore.create ()
        store.SaveAsync agentId (makeEntry "a" "1") |> fun t -> t.Wait()
        store.SaveAsync agentId (makeEntry "b" "2") |> fun t -> t.Wait()
        store.ClearAsync agentId |> fun t -> t.Wait()

        let results = (store.RecallAllAsync agentId).Result
        Assert.AreEqual(0, results.Length)

    [<TestMethod>]
    member _.RecallAll_ReturnsAllEntries() =
        let store = InMemoryStore.create ()
        store.SaveAsync agentId (makeEntry "x" "1") |> fun t -> t.Wait()
        store.SaveAsync agentId (makeEntry "y" "2") |> fun t -> t.Wait()
        store.SaveAsync agentId (makeEntry "z" "3") |> fun t -> t.Wait()

        let results = (store.RecallAllAsync agentId).Result
        Assert.AreEqual(3, results.Length)

    [<TestMethod>]
    member _.IsolatesBetweenAgents() =
        let store = InMemoryStore.create ()
        let agent1 = "agent-1"
        let agent2 = "agent-2"

        store.SaveAsync agent1 (makeEntry "data" "from-1") |> fun t -> t.Wait()
        store.SaveAsync agent2 (makeEntry "data" "from-2") |> fun t -> t.Wait()

        let r1 = (store.RecallAsync agent1 "data").Result
        let r2 = (store.RecallAsync agent2 "data").Result
        Assert.AreEqual("from-1", r1.[0].Value)
        Assert.AreEqual("from-2", r2.[0].Value)
