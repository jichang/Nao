namespace Nao.Runtime.Orleans.Tests

open System
open System.Collections.Generic
open System.IO
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence
open Nao.Runtime.Orleans

[<TestClass>]
type SessionDeletionTests() =

    [<TestMethod>]
    member _.DeletesOnlyTheRequestedSession() =
        let root =
            Path.Combine(Path.GetTempPath(), "nao-session-deletion-" + Guid.NewGuid().ToString("N"))

        try
            let conversations = FileConversationStore.create root
            let memories = InMemoryStore.create ()
            let metrics = InMemoryMetricsCollector.create ()
            let directoryEntries = HashSet<string>([ "session-a"; "session-b" ])
            let clearedStates = HashSet<string>()
            let sessionA = "user-1/session-a"
            let sessionB = "user-1/session-b"
            let turnOwners = HashSet<string>([ sessionA; sessionB ])
            let journalOwners = HashSet<string>([ sessionA; sessionB ])
            let owner sessionKey = "session:" + sessionKey
            let correlation = CorrelationContext.root ()

            let memory key value =
                { Key = key
                  Value = value
                  Timestamp = DateTimeOffset.UtcNow
                  Tags = [] }

            conversations.SaveAsync sessionA "default" [||] |> _.Wait()
            conversations.SaveAsync sessionB "default" [||] |> _.Wait()
            memories.SaveAsync (owner sessionA) (memory "a" "one") |> _.Wait()
            memories.SaveAsync (owner sessionB) (memory "b" "two") |> _.Wait()
            metrics.Record(MetricRecord.llmCall correlation sessionA DateTimeOffset.UtcNow 10 5 20L)
            metrics.Record(MetricRecord.llmCall correlation sessionB DateTimeOffset.UtcNow 20 10 30L)

            let deletion =
                SessionDeletion.create
                    conversations.DeleteSessionAsync
                    (fun sessionKey ->
                        let deleted = if turnOwners.Remove sessionKey then 1 else 0
                        System.Threading.Tasks.Task.FromResult(Ok deleted))
                    memories.DeleteOwnerAsync
                    metrics.DeleteOwnerAsync
                    (fun sessionKey ->
                        let deleted = if journalOwners.Remove sessionKey then 1 else 0
                        System.Threading.Tasks.Task.FromResult(Ok deleted))
                    (fun _ sessionId ->
                        directoryEntries.Remove sessionId |> ignore
                        System.Threading.Tasks.Task.CompletedTask)
                    (fun () ->
                        clearedStates.Add sessionA |> ignore
                        System.Threading.Tasks.Task.CompletedTask)

            let request = SessionDeletion.request sessionA (owner sessionA) "user-1" "session-a"

            match SessionDeletion.executeAsync request deletion |> _.Result with
            | Error failure -> Assert.Fail(failure.Message)
            | Ok() -> ()

            Assert.IsFalse(Directory.Exists(SessionPaths.sessionDir root sessionA))
            Assert.IsTrue(Directory.Exists(SessionPaths.sessionDir root sessionB))
            Assert.AreEqual(0, memories.RecallAllAsync(owner sessionA).Result.Length)
            Assert.AreEqual(1, memories.RecallAllAsync(owner sessionB).Result.Length)
            Assert.AreEqual(0, (metrics.GetMetrics sessionA).TotalLlmCalls)
            Assert.AreEqual(1, (metrics.GetMetrics sessionB).TotalLlmCalls)
            CollectionAssert.AreEquivalent([| sessionB |], turnOwners |> Seq.toArray)
            CollectionAssert.AreEquivalent([| sessionB |], journalOwners |> Seq.toArray)
            CollectionAssert.AreEquivalent([| "session-b" |], directoryEntries |> Seq.toArray)
            CollectionAssert.AreEquivalent([| sessionA |], clearedStates |> Seq.toArray)
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)

    [<TestMethod>]
    member _.StopsWhenMemoryDeletionFails() =
        let mutable metricsDeleted = false
        let mutable journalDeleted = false
        let mutable directoryRemoved = false
        let mutable runtimeCleared = false

        let failure =
            PlatformFailure.create PlatformErrorCategory.TransientDependency "memory unavailable" true None

        let deletion =
            SessionDeletion.create
                (fun _ -> System.Threading.Tasks.Task.CompletedTask)
                (fun _ -> System.Threading.Tasks.Task.FromResult(Ok 1))
                (fun _ -> System.Threading.Tasks.Task.FromResult(Error failure))
                (fun _ ->
                    metricsDeleted <- true
                    System.Threading.Tasks.Task.FromResult(Ok 0))
                (fun _ ->
                    journalDeleted <- true
                    System.Threading.Tasks.Task.FromResult(Ok 0))
                (fun _ _ ->
                    directoryRemoved <- true
                    System.Threading.Tasks.Task.CompletedTask)
                (fun () ->
                    runtimeCleared <- true
                    System.Threading.Tasks.Task.CompletedTask)

        let request =
            SessionDeletion.request "user/session" "session:user/session" "user" "session"

        let result = SessionDeletion.executeAsync request deletion |> _.Result

        Assert.AreEqual(Error failure, result)
        Assert.IsFalse(metricsDeleted)
        Assert.IsFalse(journalDeleted)
        Assert.IsFalse(directoryRemoved)
        Assert.IsFalse(runtimeCleared)

    [<TestMethod>]
    member _.StopsWhenMetricsDeletionFails() =
        let mutable journalDeleted = false
        let mutable directoryRemoved = false
        let mutable runtimeCleared = false

        let failure =
            PlatformFailure.create PlatformErrorCategory.TransientDependency "metrics unavailable" true None

        let deletion =
            SessionDeletion.create
                (fun _ -> System.Threading.Tasks.Task.CompletedTask)
                (fun _ -> System.Threading.Tasks.Task.FromResult(Ok 1))
                (fun _ -> System.Threading.Tasks.Task.FromResult(Ok 1))
                (fun _ -> System.Threading.Tasks.Task.FromResult(Error failure))
                (fun _ ->
                    journalDeleted <- true
                    System.Threading.Tasks.Task.FromResult(Ok 0))
                (fun _ _ ->
                    directoryRemoved <- true
                    System.Threading.Tasks.Task.CompletedTask)
                (fun () ->
                    runtimeCleared <- true
                    System.Threading.Tasks.Task.CompletedTask)

        let request =
            SessionDeletion.request "user/session" "session:user/session" "user" "session"

        let result = SessionDeletion.executeAsync request deletion |> _.Result

        Assert.AreEqual(Error failure, result)
        Assert.IsFalse(journalDeleted)
        Assert.IsFalse(directoryRemoved)
        Assert.IsFalse(runtimeCleared)

    [<TestMethod>]
    member _.StopsWhenJournalDeletionFails() =
        let mutable directoryRemoved = false
        let mutable runtimeCleared = false

        let failure =
            PlatformFailure.create PlatformErrorCategory.TransientDependency "journal unavailable" true None

        let deletion =
            SessionDeletion.create
                (fun _ -> System.Threading.Tasks.Task.CompletedTask)
                (fun _ -> System.Threading.Tasks.Task.FromResult(Ok 1))
                (fun _ -> System.Threading.Tasks.Task.FromResult(Ok 1))
                (fun _ -> System.Threading.Tasks.Task.FromResult(Ok 1))
                (fun _ -> System.Threading.Tasks.Task.FromResult(Error failure))
                (fun _ _ ->
                    directoryRemoved <- true
                    System.Threading.Tasks.Task.CompletedTask)
                (fun () ->
                    runtimeCleared <- true
                    System.Threading.Tasks.Task.CompletedTask)

        let request =
            SessionDeletion.request "user/session" "session:user/session" "user" "session"

        let result = SessionDeletion.executeAsync request deletion |> _.Result

        Assert.AreEqual(Error failure, result)
        Assert.IsFalse(directoryRemoved)
        Assert.IsFalse(runtimeCleared)
