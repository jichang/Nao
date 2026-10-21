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

    let tenantId = TenantId.parse "tenant"

    let workspaceId =
        WorkspaceVersionId.create "workspace" "1" |> WorkspaceVersionId.workspaceId

    let userId = UserId.parse "user"

    let scope (sessionReference: string) =
        let sessionId =
            RuntimeAddress.tryParseSessionKey sessionReference
            |> Option.map RuntimeAddress.sessionId
            |> Option.defaultWith (fun () -> sessionReference.Split('/') |> Array.last |> SessionId.parse)

        let principal = SecurityPrincipal.create tenantId userId []

        AuthorizationScope.tryCreate principal None workspaceId (Some sessionId)
        |> Option.get

    let sessionKey sessionId =
        RuntimeAddress.session tenantId workspaceId userId (SessionId.parse sessionId)
        |> RuntimeAddress.sessionKey

    [<TestMethod>]
    member _.RejectsDeletionKeyOutsideAuthenticatedScope() =
        for key in
            [ "user/session"
              sessionKey "other-session"
              (RuntimeAddress.session (TenantId.parse "other-tenant") workspaceId userId (SessionId.parse "session")
               |> RuntimeAddress.sessionKey) ] do
            Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
                SessionDeletion.request key (scope "session") |> ignore)
            |> ignore

        let principal = SecurityPrincipal.create tenantId userId []

        let workspaceScope =
            AuthorizationScope.tryCreate principal None workspaceId None |> Option.get

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            SessionDeletion.request (sessionKey "session") workspaceScope |> ignore)
        |> ignore

    [<TestMethod>]
    member _.CanonicalSessionRoutingKeyMatchesCompleteScope() =
        SessionDeletion.request (sessionKey "default") (scope "default") |> ignore

    [<TestMethod>]
    member _.MemoryDeletionReceivesCompleteTenantScope() =
        let authorized = scope "user/session"

        let otherPrincipal =
            SecurityPrincipal.create (TenantId.parse "other-tenant") (UserId.parse "user") []

        let other =
            AuthorizationScope.tryCreate
                otherPrincipal
                None
                (WorkspaceId.parse "workspace@1")
                (Some(SessionId.parse "session"))
            |> Option.get

        let memories = InMemoryStore.create ()

        let entry =
            { Key = "key"
              Value = "value"
              Timestamp = DateTimeOffset.UtcNow
              Tags = [] }

        memories.SaveAsync authorized entry |> _.Wait()
        memories.SaveAsync other entry |> _.Wait()
        let mutable observed = None

        let deletion =
            SessionDeletion.create
                (fun _ -> System.Threading.Tasks.Task.CompletedTask)
                (fun _ -> System.Threading.Tasks.Task.FromResult(Ok 0))
                (fun authorization ->
                    observed <- Some authorization
                    memories.DeleteOwnerAsync authorization)
                (fun _ -> System.Threading.Tasks.Task.FromResult(Ok 0))
                (fun _ -> System.Threading.Tasks.Task.FromResult(Ok 0))
                (fun _ -> System.Threading.Tasks.Task.FromResult(Ok 0))
                (fun _ _ -> System.Threading.Tasks.Task.CompletedTask)
                (fun () -> System.Threading.Tasks.Task.CompletedTask)

        let result =
            SessionDeletion.executeAsync (SessionDeletion.request (sessionKey "session") authorized) deletion
            |> _.Result

        Assert.AreEqual(Ok(), result)
        Assert.AreEqual(Some authorized, observed)
        Assert.IsTrue((memories.RecallAllAsync authorized).Result.IsEmpty)
        Assert.AreEqual([ entry ], memories.RecallAllAsync other |> _.Result)

    [<TestMethod>]
    member _.DeletesOnlyTheRequestedSession() =
        let root =
            Path.Combine(Path.GetTempPath(), "nao-session-deletion-" + Guid.NewGuid().ToString("N"))

        try
            let conversations = FileConversationStore.create root
            let memories = InMemoryStore.create ()
            let metrics = InMemoryMetricsCollector.create ()
            let traces = InMemoryTraceStore.create ()
            let directoryEntries = HashSet<string>([ "session-a"; "session-b" ])
            let clearedStates = HashSet<string>()
            let sessionA = sessionKey "session-a"
            let sessionB = sessionKey "session-b"
            let owner = scope

            let journalKey session =
                owner session |> AuthorizationScope.identity |> AuthorizationIdentity.storageKey

            let turnOwners = HashSet<string>([ journalKey sessionA; journalKey sessionB ])
            let journalOwners = HashSet<string>([ journalKey sessionA; journalKey sessionB ])
            let correlation = CorrelationContext.root ()

            let memory key value =
                { Key = key
                  Value = value
                  Timestamp = DateTimeOffset.UtcNow
                  Tags = [] }

            conversations.SaveAsync (owner sessionA) "default" [||] |> _.Wait()
            conversations.SaveAsync (owner sessionB) "default" [||] |> _.Wait()
            memories.SaveAsync (owner sessionA) (memory "a" "one") |> _.Wait()
            memories.SaveAsync (owner sessionB) (memory "b" "two") |> _.Wait()

            metrics.Record
                (owner sessionA)
                (MetricRecord.llmCall correlation DateTimeOffset.UtcNow (ProviderTokenUsage.Split(10, 5)) 20L)

            metrics.Record
                (owner sessionB)
                (MetricRecord.llmCall correlation DateTimeOffset.UtcNow (ProviderTokenUsage.Split(20, 10)) 30L)

            traces.SaveAsync (owner sessionA) (Verification.startTrace correlation "agent" "a")
            |> _.Wait()

            traces.SaveAsync (owner sessionB) (Verification.startTrace correlation "agent" "b")
            |> _.Wait()

            let deletion =
                SessionDeletion.create
                    conversations.DeleteScopeAsync
                    (fun authorization ->
                        let deleted =
                            if
                                turnOwners.Remove(
                                    AuthorizationScope.identity authorization |> AuthorizationIdentity.storageKey
                                )
                            then
                                1
                            else
                                0

                        System.Threading.Tasks.Task.FromResult(Ok deleted))
                    memories.DeleteOwnerAsync
                    metrics.DeleteScopeAsync
                    traces.DeleteScopeAsync
                    (fun authorization ->
                        let deleted =
                            if
                                journalOwners.Remove(
                                    AuthorizationScope.identity authorization |> AuthorizationIdentity.storageKey
                                )
                            then
                                1
                            else
                                0

                        System.Threading.Tasks.Task.FromResult(Ok deleted))
                    (fun _ sessionId ->
                        directoryEntries.Remove sessionId |> ignore
                        System.Threading.Tasks.Task.CompletedTask)
                    (fun () ->
                        clearedStates.Add sessionA |> ignore
                        System.Threading.Tasks.Task.CompletedTask)

            let request = SessionDeletion.request sessionA (owner sessionA)

            match SessionDeletion.executeAsync request deletion |> _.Result with
            | Error failure -> Assert.Fail(failure.Message)
            | Ok() -> ()

            Assert.AreEqual(0, conversations.ListConversationsAsync(owner sessionA).Result.Length)
            Assert.AreEqual(1, conversations.ListConversationsAsync(owner sessionB).Result.Length)
            Assert.AreEqual(0, memories.RecallAllAsync(owner sessionA).Result.Length)
            Assert.AreEqual(1, memories.RecallAllAsync(owner sessionB).Result.Length)
            Assert.AreEqual(0, (metrics.GetMetrics(owner sessionA)).TotalLlmCalls)
            Assert.AreEqual(1, (metrics.GetMetrics(owner sessionB)).TotalLlmCalls)
            Assert.AreEqual(0, traces.GetTracesAsync (owner sessionA) "agent" 10 |> _.Result |> List.length)
            Assert.AreEqual(1, traces.GetTracesAsync (owner sessionB) "agent" 10 |> _.Result |> List.length)
            CollectionAssert.AreEquivalent([| journalKey sessionB |], turnOwners |> Seq.toArray)
            CollectionAssert.AreEquivalent([| journalKey sessionB |], journalOwners |> Seq.toArray)
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
                (fun _ -> System.Threading.Tasks.Task.FromResult(Ok 0))
                (fun _ ->
                    journalDeleted <- true
                    System.Threading.Tasks.Task.FromResult(Ok 0))
                (fun _ _ ->
                    directoryRemoved <- true
                    System.Threading.Tasks.Task.CompletedTask)
                (fun () ->
                    runtimeCleared <- true
                    System.Threading.Tasks.Task.CompletedTask)

        let request = SessionDeletion.request (sessionKey "session") (scope "session")

        let result = SessionDeletion.executeAsync request deletion |> _.Result

        Assert.AreEqual(Error failure, result)
        Assert.IsFalse(metricsDeleted)
        Assert.IsFalse(journalDeleted)
        Assert.IsFalse(directoryRemoved)
        Assert.IsFalse(runtimeCleared)

    [<TestMethod>]
    member _.StopsWhenMetricsDeletionFails() =
        let mutable tracesDeleted = false
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
                    tracesDeleted <- true
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

        let request = SessionDeletion.request (sessionKey "session") (scope "session")

        let result = SessionDeletion.executeAsync request deletion |> _.Result

        Assert.AreEqual(Error failure, result)
        Assert.IsFalse(tracesDeleted)
        Assert.IsFalse(journalDeleted)
        Assert.IsFalse(directoryRemoved)
        Assert.IsFalse(runtimeCleared)

    [<TestMethod>]
    member _.StopsWhenTraceDeletionFails() =
        let mutable journalDeleted = false
        let mutable directoryRemoved = false
        let mutable runtimeCleared = false

        let failure =
            PlatformFailure.create PlatformErrorCategory.TransientDependency "trace store unavailable" true None

        let deletion =
            SessionDeletion.create
                (fun _ -> System.Threading.Tasks.Task.CompletedTask)
                (fun _ -> System.Threading.Tasks.Task.FromResult(Ok 1))
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

        let request = SessionDeletion.request (sessionKey "session") (scope "session")
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
                (fun _ -> System.Threading.Tasks.Task.FromResult(Ok 1))
                (fun _ -> System.Threading.Tasks.Task.FromResult(Error failure))
                (fun _ _ ->
                    directoryRemoved <- true
                    System.Threading.Tasks.Task.CompletedTask)
                (fun () ->
                    runtimeCleared <- true
                    System.Threading.Tasks.Task.CompletedTask)

        let request = SessionDeletion.request (sessionKey "session") (scope "session")

        let result = SessionDeletion.executeAsync request deletion |> _.Result

        Assert.AreEqual(Error failure, result)
        Assert.IsFalse(directoryRemoved)
        Assert.IsFalse(runtimeCleared)
