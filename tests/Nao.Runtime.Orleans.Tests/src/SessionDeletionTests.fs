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
        let root = Path.Combine(Path.GetTempPath(), "nao-session-deletion-" + Guid.NewGuid().ToString("N"))

        try
            let conversations = FileConversationStore.create root
            let memories = InMemoryStore.create ()
            let directoryEntries = HashSet<string>([ "session-a"; "session-b" ])
            let clearedStates = HashSet<string>()
            let sessionA = "user-1/session-a"
            let sessionB = "user-1/session-b"
            let owner sessionKey = "session:" + sessionKey
            let memory key value =
                { Key = key
                  Value = value
                  Timestamp = DateTimeOffset.UtcNow
                  Tags = [] }

            conversations.SaveAsync sessionA "default" [||] |> _.Wait()
            conversations.SaveAsync sessionB "default" [||] |> _.Wait()
            memories.SaveAsync (owner sessionA) (memory "a" "one") |> _.Wait()
            memories.SaveAsync (owner sessionB) (memory "b" "two") |> _.Wait()

            let deletion =
                SessionDeletion.create
                    conversations.DeleteSessionAsync
                    memories.ClearAsync
                    (fun _ sessionId ->
                        directoryEntries.Remove sessionId |> ignore
                        System.Threading.Tasks.Task.CompletedTask)
                    (fun () ->
                        clearedStates.Add sessionA |> ignore
                        System.Threading.Tasks.Task.CompletedTask)
            let request = SessionDeletion.request sessionA (owner sessionA) "user-1" "session-a"

            SessionDeletion.executeAsync request deletion |> _.Wait()

            Assert.IsFalse(Directory.Exists(SessionPaths.sessionDir root sessionA))
            Assert.IsTrue(Directory.Exists(SessionPaths.sessionDir root sessionB))
            Assert.AreEqual(0, memories.RecallAllAsync(owner sessionA).Result.Length)
            Assert.AreEqual(1, memories.RecallAllAsync(owner sessionB).Result.Length)
            CollectionAssert.AreEquivalent([| "session-b" |], directoryEntries |> Seq.toArray)
            CollectionAssert.AreEquivalent([| sessionA |], clearedStates |> Seq.toArray)
        finally
            if Directory.Exists root then Directory.Delete(root, true)