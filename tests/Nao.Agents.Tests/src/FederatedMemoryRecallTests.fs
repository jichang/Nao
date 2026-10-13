namespace Nao.Agents.Tests

open System
open System.Text.Json
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass>]
type FederatedMemoryRecallTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate
                principal
                None
                (WorkspaceId.parse "workspace")
                (Some(SessionId.parse "session"))
            |> Option.get

    let context =
        { AgentContext.unrestrictedForTests () with
            Authorization = Some authorization }

    let run (tool: Tool) input =
        match tool.RunAsync context input |> _.Result with
        | Ok output -> output
        | Error failure ->
            Assert.Fail failure.Message
            ""

    let knowledgeEntry =
        KnowledgeMemoryRecallEntry.create
            "The published support policy requires manager approval."
            0.92
            (KnowledgeMemoryRecallEvidence.create
                "support-policies"
                "escalation-policy"
                "escalation-policy-v3"
                "escalation-policy-v3-chunk-2"
                (Uri "https://knowledge.example/support/escalation")
                "0123456789abcdef")

    [<TestMethod>]
    member _.``Federated search keeps memory and knowledge in separate result collections``() =
        let store = InMemoryStore.create ()

        store.SaveAsync
            authorization
            { Key = "preference.response-style"
              Value = "Use concise answers"
              Timestamp = DateTimeOffset(2026, 10, 13, 3, 10, 0, TimeSpan.Zero)
              Tags = [ "preference" ] }
        |> _.Wait()

        let mutable observed = None

        let knowledge =
            MemoryKnowledgeRecall.create
                (ResourceAccess.knowledge "workspace-knowledge" KnowledgeAction.Retrieve)
                (fun actual request _ ->
                    observed <- Some(actual, request)
                    Task.FromResult [ knowledgeEntry ])

        let search =
            MemoryTools.create MemoryToolConfig.Default store (Some knowledge)
            |> List.find (fun tool -> tool.Name = "memory_search")

        Assert.AreEqual(
            [ ResourceAccess.tool "memory_search"
              ResourceAccess.memory "durable" MemoryAction.Read
              ResourceAccess.knowledge "workspace-knowledge" KnowledgeAction.Retrieve ],
            search.Permissions
        )

        let output =
            run
                search
                """{"query":"support policy concise","intent":"Prepare a concise support answer","source":"all","memoryTags":["preference"]}"""

        use document = JsonDocument.Parse output
        let memories = document.RootElement.GetProperty("memories")
        let passages = document.RootElement.GetProperty("knowledge")
        Assert.AreEqual(1, memories.GetArrayLength())
        Assert.AreEqual("preference.response-style", memories[0].GetProperty("key").GetString())
        Assert.AreEqual(1, passages.GetArrayLength())
        Assert.AreEqual("support-policies", passages[0].GetProperty("provenance").GetProperty("sourceId").GetString())

        match observed with
        | Some(actual, request) ->
            Assert.AreEqual(AuthorizationScope.identity authorization, AuthorizationScope.identity actual)
            Assert.AreEqual("support policy concise", KnowledgeMemoryRecallRequest.query request)
            Assert.AreEqual(5, KnowledgeMemoryRecallRequest.limit request)
        | None -> Assert.Fail "Expected source-backed knowledge recall."

    [<TestMethod>]
    member _.``Knowledge-only search does not read or mutate memory storage``() =
        let mutable memoryCalls = 0

        let store =
            { InMemoryStore.create () with
                RecallAllAsync =
                    fun _ ->
                        memoryCalls <- memoryCalls + 1
                        Task.FromResult []
                SaveAsync =
                    fun _ _ ->
                        memoryCalls <- memoryCalls + 1
                        Task.FromResult()
                ForgetAsync =
                    fun _ _ ->
                        memoryCalls <- memoryCalls + 1
                        Task.FromResult() }

        let knowledge =
            MemoryKnowledgeRecall.create
                (ResourceAccess.knowledge "workspace-knowledge" KnowledgeAction.Retrieve)
                (fun _ _ _ -> Task.FromResult [ knowledgeEntry ])

        let search =
            MemoryTools.create MemoryToolConfig.Default store (Some knowledge)
            |> List.find (fun tool -> tool.Name = "memory_search")

        let output =
            run search """{"query":"support policy","intent":"Find published policy","source":"knowledge"}"""

        use document = JsonDocument.Parse output
        Assert.AreEqual(0, document.RootElement.GetProperty("memories").GetArrayLength())
        Assert.AreEqual(1, document.RootElement.GetProperty("knowledge").GetArrayLength())
        Assert.AreEqual(0, memoryCalls)

    [<TestMethod>]
    member _.``Unavailable knowledge recall fails explicitly while memory search remains available``() =
        let search =
            MemoryTools.create MemoryToolConfig.Default (InMemoryStore.create ()) None
            |> List.find (fun tool -> tool.Name = "memory_search")

        match
            search.RunAsync
                context
                """{"query":"support policy","intent":"Find published policy","source":"knowledge"}"""
            |> _.Result
        with
        | Ok _ -> Assert.Fail "Knowledge search unexpectedly succeeded without a configured capability."
        | Error failure -> Assert.AreEqual(ToolFailureKind.InputContract, failure.Kind)

        let output =
            run search """{"query":"support policy","intent":"Recall learned memory","source":"memory"}"""

        use document = JsonDocument.Parse output
        Assert.AreEqual(0, document.RootElement.GetProperty("knowledge").GetArrayLength())
