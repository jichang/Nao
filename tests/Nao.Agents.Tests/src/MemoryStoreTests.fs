namespace Nao.Agents.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass>]
type MemoryStoreTests() =

    let scope session =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") (Some(SessionId.parse session))
        |> Option.get

    let authorization = scope "test-session"

    let makeEntry key value =
        { Key = key
          Value = value
          Timestamp = DateTimeOffset.UtcNow
          Tags = [] }

    [<TestMethod>]
    member _.SaveAndRecall_ReturnsSavedEntry() =
        let store = InMemoryStore.create ()
        let entry = makeEntry "user-name" "Alice"
        store.SaveAsync authorization entry |> fun t -> t.Wait()

        let results = (store.RecallAsync authorization "user-name").Result
        Assert.AreEqual(1, results.Length)
        Assert.AreEqual("Alice", results.[0].Value)

    [<TestMethod>]
    member _.SaveMultiple_RecallsByPrefix() =
        let store = InMemoryStore.create ()

        store.SaveAsync authorization (makeEntry "user-name" "Alice")
        |> fun t -> t.Wait()

        store.SaveAsync authorization (makeEntry "user-email" "alice@example.com")
        |> fun t -> t.Wait()

        store.SaveAsync authorization (makeEntry "preference-theme" "dark")
        |> fun t -> t.Wait()

        let userResults = (store.RecallAsync authorization "user").Result
        Assert.AreEqual(2, userResults.Length)

        let prefResults = (store.RecallAsync authorization "preference").Result
        Assert.AreEqual(1, prefResults.Length)

    [<TestMethod>]
    member _.Save_OverwritesExistingKey() =
        let store = InMemoryStore.create ()
        store.SaveAsync authorization (makeEntry "name" "Alice") |> fun t -> t.Wait()
        store.SaveAsync authorization (makeEntry "name" "Bob") |> fun t -> t.Wait()

        let results = (store.RecallAsync authorization "name").Result
        Assert.AreEqual(1, results.Length)
        Assert.AreEqual("Bob", results.[0].Value)

    [<TestMethod>]
    member _.Forget_RemovesEntry() =
        let store = InMemoryStore.create ()
        store.SaveAsync authorization (makeEntry "temp" "value") |> fun t -> t.Wait()
        store.ForgetAsync authorization "temp" |> fun t -> t.Wait()

        let results = (store.RecallAsync authorization "temp").Result
        Assert.AreEqual(0, results.Length)

    [<TestMethod>]
    member _.DeleteOwner_RemovesAllEntries() =
        let store = InMemoryStore.create ()
        store.SaveAsync authorization (makeEntry "a" "1") |> fun t -> t.Wait()
        store.SaveAsync authorization (makeEntry "b" "2") |> fun t -> t.Wait()

        match store.DeleteOwnerAsync authorization |> _.Result with
        | Error failure -> Assert.Fail(failure.Message)
        | Ok deleted -> Assert.AreEqual(2, deleted)

        let results = (store.RecallAllAsync authorization).Result
        Assert.AreEqual(0, results.Length)

    [<TestMethod>]
    member _.RecallAll_ReturnsAllEntries() =
        let store = InMemoryStore.create ()
        store.SaveAsync authorization (makeEntry "x" "1") |> fun t -> t.Wait()
        store.SaveAsync authorization (makeEntry "y" "2") |> fun t -> t.Wait()
        store.SaveAsync authorization (makeEntry "z" "3") |> fun t -> t.Wait()

        let results = (store.RecallAllAsync authorization).Result
        Assert.AreEqual(3, results.Length)

    [<TestMethod>]
    member _.IsolatesBetweenSessions() =
        let store = InMemoryStore.create ()
        let agent1 = scope "session-1"
        let agent2 = scope "session-2"

        store.SaveAsync agent1 (makeEntry "data" "from-1") |> fun t -> t.Wait()
        store.SaveAsync agent2 (makeEntry "data" "from-2") |> fun t -> t.Wait()

        let r1 = (store.RecallAsync agent1 "data").Result
        let r2 = (store.RecallAsync agent2 "data").Result
        Assert.AreEqual("from-1", r1.[0].Value)
        Assert.AreEqual("from-2", r2.[0].Value)

[<TestClass>]
type MemoryToolAuthorizationTests() =
    let scope tenant =
        let principal =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse tenant)
                (UserId.parse "user")
                (Some(ServiceId.parse "private-service"))
                []
                [ "private-role" ]
                (Map.ofList [ "private-claim", "private-claim-value" ])
                AuthenticationStrength.MultiFactor

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") (Some(SessionId.parse "session"))
        |> Option.get

    let config =
        { MemoryToolConfig.Default with
            ForgetEnabled = true }

    let searchInput =
        """{"query":"preference","intent":"recall preference","owner":"other-tenant","tenantId":"other-tenant"}"""

    let rememberInput =
        """{"key":"preference","value":"my preference","owner":"other-tenant","tenantId":"other-tenant"}"""

    let forgetInput =
        """{"key":"preference","reason":"user requested","confirmedByUser":true,"owner":"other-tenant"}"""

    let requireSuccess (result: ToolRunResult) =
        match result with
        | Ok output -> output
        | Error failure ->
            Assert.Fail failure.Message
            ""

    [<TestMethod>]
    member _.MissingScopeDeniesEveryMemoryToolBeforeStorageAccess() =
        let mutable calls = 0

        let store =
            { InMemoryStore.create () with
                SaveAsync =
                    fun _ _ ->
                        calls <- calls + 1
                        Task.FromResult()
                RecallAllAsync =
                    fun _ ->
                        calls <- calls + 1
                        Task.FromResult []
                ForgetAsync =
                    fun _ _ ->
                        calls <- calls + 1
                        Task.FromResult() }

        let tools = MemoryTools.create config store

        let expectedPermissions =
            Map.ofList
                [ "memory_search", ResourceAccess.memory "durable" MemoryAction.Read
                  "memory_remember", ResourceAccess.memory "durable" MemoryAction.Write
                  "memory_forget", ResourceAccess.memory "durable" MemoryAction.Delete ]

        for tool in tools do
            Assert.AreEqual([ ResourceAccess.tool tool.Name; expectedPermissions.[tool.Name] ], tool.Permissions)

        for name, input in
            [ "memory_search", searchInput
              "memory_remember", rememberInput
              "memory_forget", forgetInput ] do
            let tool = tools |> List.find (fun tool -> tool.Name = name)

            match tool.RunAsync (AgentContext.unrestrictedForTests ()) input |> _.Result with
            | Ok _ -> Assert.Fail "Unauthenticated memory access must be denied."
            | Error failure ->
                Assert.AreEqual(ToolFailureKind.PermissionDenied, failure.Kind)
                Assert.IsFalse failure.Retryable

        Assert.AreEqual(0, calls)

    [<TestMethod>]
    member _.ToolsUseInvocationScopeRatherThanRoutingKeysOrModelOwnerFields() =
        let store = InMemoryStore.create ()
        let own, other = scope "own-tenant", scope "other-tenant"

        let foreign =
            { Key = "preference"
              Value = "foreign preference"
              Timestamp = DateTimeOffset.UtcNow
              Tags = [] }

        store.SaveAsync other foreign |> _.Wait()
        let tools = MemoryTools.create config store

        let invoke context name input =
            let tool = tools |> List.find (fun tool -> tool.Name = name)
            tool.RunAsync context input |> _.Result |> requireSuccess

        let context =
            { AgentContext.unrestrictedForTests () with
                Authorization = Some own
                SessionKey = "other-tenant/user/session" }

        invoke context "memory_remember" rememberInput |> ignore
        let output = invoke context "memory_search" searchInput
        StringAssert.Contains(output, "my preference")
        Assert.IsFalse(output.Contains "foreign preference")

        for excluded in [ "private-service"; "private-role"; "private-claim" ] do
            Assert.IsFalse(output.Contains excluded)

        let otherOutput =
            invoke
                { context with
                    Authorization = Some other }
                "memory_search"
                searchInput

        StringAssert.Contains(otherOutput, "foreign preference")
        invoke context "memory_forget" forgetInput |> ignore
        Assert.IsTrue((store.RecallAllAsync own).Result.IsEmpty)
        Assert.AreEqual([ foreign ], store.RecallAllAsync other |> _.Result)

    [<TestMethod>]
    member _.DelegatedMemoryToolsRemainBoundToHarnessScopeWhenContextIsSubstituted() =
        let store = InMemoryStore.create ()
        let own, other = scope "own-tenant", scope "other-tenant"

        let foreign =
            { Key = "preference"
              Value = "foreign preference"
              Timestamp = DateTimeOffset.UtcNow
              Tags = [] }

        store.SaveAsync other foreign |> _.Wait()
        let protocol = MemoryTools.create config store |> ToolProtocol.fromTools

        let child =
            Agent.create "memory-child" "memory-child" "test" 0 [] AgentContract.Text (fun context _ ->
                task {
                    let authorization = AgentContext.requireAuthorization context
                    Assert.AreEqual(AuthorizationScope.identity own, AuthorizationScope.identity authorization)

                    Assert.AreEqual(
                        [ "memory-parent"; "memory-child" ],
                        AuthorizationScope.agentLineage authorization |> List.map _.AgentId
                    )

                    AuthorizationScope.requireAgent "memory-child" context.Correlation.ExecutionId authorization

                    let substituted =
                        { context with
                            Authorization = Some other }

                    let! saved =
                        protocol.InvokeAsync substituted (context.NextToolCallKey()) "memory_remember" rememberInput

                    Assert.IsTrue(saved.Success, saved.Output)

                    let! found =
                        protocol.InvokeAsync substituted (context.NextToolCallKey()) "memory_search" searchInput

                    Assert.IsTrue(found.Success, found.Output)
                    return found.Output
                })

        let parent =
            Agent.create "memory-parent" "memory-parent" "test" 0 [] AgentContract.Text (fun context input ->
                task {
                    let authorization = AgentContext.requireAuthorization context

                    Assert.AreEqual(
                        [ "memory-parent" ],
                        AuthorizationScope.agentLineage authorization |> List.map _.AgentId
                    )

                    AuthorizationScope.requireAgent "memory-parent" context.Correlation.ExecutionId authorization
                    let! response = ExecutionRuntime.runAgent context child input

                    match response with
                    | Ok value -> return value
                    | Error failure -> return PlatformFailure.raiseException failure
                })

        let context =
            { AgentContext.unrestrictedForTests () with
                Authorization = Some other }

        let request =
            ExecutionRequest.create
                own
                (TurnId.parse "turn")
                "conversation"
                parent.Metadata.Id
                "input"
                SandboxConfig.Default
                Map.empty
                Map.empty
                context.Correlation

        let result =
            EtclovgHarness.runAsync
                { EtclovgConfig.Default with
                    ExecutionJournal = Some(InMemoryExecutionJournal.create ()) }
                context
                parent
                request
                CancellationToken.None
            |> _.Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, result.Status)
        let output = result.Outputs.Response |> Option.get
        StringAssert.Contains(output, "my preference")
        Assert.IsFalse(output.Contains "foreign preference")
        Assert.AreEqual([ foreign ], store.RecallAllAsync other |> _.Result)
        Assert.AreEqual("my preference", (store.RecallAllAsync own).Result.Head.Value)
