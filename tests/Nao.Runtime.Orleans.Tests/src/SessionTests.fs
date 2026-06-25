namespace Nao.Runtime.Orleans.Tests

open System
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Core
open Nao.Loader
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains

/// A minimal LLM provider for testing — echoes input
type EchoProvider() =
    interface ILlmProvider with
        member _.Name = "echo"
        member _.CompleteAsync(conversation: Conversation) (_options: CompletionOptions) =
            let lastMsg =
                conversation
                |> List.tryLast
                |> Option.map (fun m -> m.Content)
                |> Option.defaultValue ""
            Task.FromResult({ Content = sprintf "echo: %s" lastMsg; FinishReason = "stop"; TokensUsed = Some 10 })

/// Helper to create workspace definitions for testing
module TestWorkspace =

    let agentDef name =
        { Name = name
          Version = None
          Description = sprintf "Test agent: %s" name
          Provider = "echo"
          Model = "test"
          Prompt = { Prompt.Empty with Role = "You are a test agent." }
          Tools = []
          SubAgents = []
          Options = CompletionOptions.Default
          MaxRounds = 1
          IsAsync = false
          Provenance = None }

    let toolDef name =
        { Name = name
          Version = None
          Description = sprintf "Test tool: %s" name
          Kind = ExecutableTool (ToolExecutionDef.Process ("echo", ["test-output"]), "", false)
          OutputContentType = ""
          VerifyExecution = None
          RevertExecution = None
          Provenance = None }

    let empty : WorkspaceDefinitions =
        { AgentDefs = []
          ToolDefs = []
          EvalSuiteDefs = []
          ConstitutionDefs = []
          Agents = []
          Tools = []
          Evaluators = []
          Errors = [] }

    let withAgent name (defs: WorkspaceDefinitions) : WorkspaceDefinitions =
        { defs with AgentDefs = defs.AgentDefs @ [agentDef name] }

    let withTool name (defs: WorkspaceDefinitions) : WorkspaceDefinitions =
        { defs with ToolDefs = defs.ToolDefs @ [toolDef name] }

    let withBuiltTool (tool: Tool) (defs: WorkspaceDefinitions) : WorkspaceDefinitions =
        { defs with Tools = defs.Tools @ [tool] }

[<TestClass>]
type GrainStateTests() =

    [<TestMethod>]
    member _.MessageRecord_RoundTrips() =
        let original = { Role = User; Content = "hello world" }
        let record = GrainStateMapping.fromMessage original
        let restored = GrainStateMapping.toMessage record
        Assert.AreEqual(original.Role, restored.Role)
        Assert.AreEqual(original.Content, restored.Content)

    [<TestMethod>]
    member _.MessageRecord_HandlesAllRoles() =
        for (role, roleStr) in [(User, "User"); (Assistant, "Assistant"); (System, "System")] do
            let msg = { Role = role; Content = "test" }
            let record = GrainStateMapping.fromMessage msg
            Assert.AreEqual(roleStr, record.Role)
            let restored = GrainStateMapping.toMessage record
            Assert.AreEqual(role, restored.Role)

    [<TestMethod>]
    member _.MemoryRecord_Converts() =
        let entry: MemoryEntry =
            { Key = "user-name"
              Value = "Alice"
              Timestamp = DateTimeOffset.UtcNow
              Tags = ["personal"; "identity"] }
        let record = MemoryRecord(Key = entry.Key, Value = entry.Value, Timestamp = entry.Timestamp, Tags = ResizeArray(entry.Tags))
        let roundtripped = GrainStateMapping.toMemoryEntry record
        Assert.AreEqual(entry.Key, roundtripped.Key)
        Assert.AreEqual(entry.Value, roundtripped.Value)
        Assert.AreEqual(entry.Tags, roundtripped.Tags)

[<TestClass>]
type SessionInfoTests() =

    [<TestMethod>]
    member _.SessionInfo_HasCorrectDefaults() =
        let info = SessionInfo()
        Assert.AreEqual("", info.AgentName)
        Assert.AreEqual("", info.SessionId)
        Assert.AreEqual("", info.UserId)
        Assert.AreEqual(true, info.IsActive)
        Assert.AreEqual(0, info.ToolNames.Count)

    [<TestMethod>]
    member _.SessionGrainState_StartsEmpty() =
        let state = SessionGrainState()
        Assert.AreEqual(0, state.Conversations.Count)
        Assert.AreEqual(0, state.Memories.Count)
        Assert.AreEqual("", state.Info.AgentName)

[<TestClass>]
type SessionGrainKeyTests() =

    [<TestMethod>]
    member _.BuildKey_CombinesUserAndSession() =
        let key = SessionGrain.buildKey "user-1" "session-abc"
        Assert.AreEqual("user-1/session-abc", key)

    [<TestMethod>]
    member _.BuildKey_SupportsMultipleSessionsPerUser() =
        let k1 = SessionGrain.buildKey "user-1" "session-1"
        let k2 = SessionGrain.buildKey "user-1" "session-2"
        Assert.AreNotEqual(k1, k2)
        Assert.IsTrue(k1.StartsWith("user-1/"))
        Assert.IsTrue(k2.StartsWith("user-1/"))

[<TestClass>]
type WorkspaceResolutionTests() =

    [<TestMethod>]
    member _.EmptyWorkspace_HasNoAgents() =
        let ws = TestWorkspace.empty
        Assert.AreEqual(0, ws.AgentDefs.Length)
        Assert.AreEqual(0, ws.Agents.Length)

    [<TestMethod>]
    member _.WithAgent_AddsDefinition() =
        let ws = TestWorkspace.empty |> TestWorkspace.withAgent "test-agent"
        Assert.AreEqual(1, ws.AgentDefs.Length)
        Assert.AreEqual("test-agent", ws.AgentDefs.[0].Name)

    [<TestMethod>]
    member _.WithTool_AddsDefinition() =
        let ws = TestWorkspace.empty |> TestWorkspace.withTool "my-tool"
        Assert.AreEqual(1, ws.ToolDefs.Length)
        Assert.AreEqual("my-tool", ws.ToolDefs.[0].Name)

    [<TestMethod>]
    member _.DefinitionBuilder_BuildsAgentFromDef() =
        let provider = EchoProvider() :> ILlmProvider
        let def = TestWorkspace.agentDef "builder-test"
        let agent = DefinitionBuilder.buildAgent provider [] [] def
        // Orchestrator uses its own internal id name
        Assert.AreEqual("orchestrator", agent.Id.Name)

    [<TestMethod>]
    member _.DefinitionBuilder_AgentResponds() =
        let provider = EchoProvider() :> ILlmProvider
        let def = TestWorkspace.agentDef "echo-agent"
        let agent = DefinitionBuilder.buildAgent provider [] [] def
        let result = agent.RunAsync("hello").Result
        // Orchestrator sends the conversation to provider, gets echo back
        Assert.IsTrue(result.Length > 0)

    [<TestMethod>]
    member _.DefinitionBuilder_EachBuildIsIsolated() =
        let provider = EchoProvider() :> ILlmProvider
        let def = TestWorkspace.agentDef "isolated-agent"

        let a1 = DefinitionBuilder.buildAgent provider [] [] def
        let a2 = DefinitionBuilder.buildAgent provider [] [] def

        let _ = a1.RunAsync("msg1").Result
        Assert.IsTrue(a1.State.Conversation.Length > 0)
        Assert.AreEqual(0, a2.State.Conversation.Length)

    [<TestMethod>]
    member _.BuiltTool_ResolvedByName() =
        let myTool: Tool =
            Tool.Create("greet", "greets",
                fun name -> Task.FromResult(sprintf "Hello %s" name))
        let ws = TestWorkspace.empty |> TestWorkspace.withBuiltTool myTool
        let found = ws.Tools |> List.tryFind (fun t -> t.Name = "greet")
        Assert.IsTrue(found.IsSome)
        let result = (found.Value.Execute ToolContext.allowAll "World").Result
        Assert.AreEqual("Hello World", result)

[<TestClass>]
type FileConversationStoreLayoutTests() =

    let newRoot () =
        let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nao-conv-" + System.Guid.NewGuid().ToString("N"))
        System.IO.Directory.CreateDirectory(dir) |> ignore
        dir

    let cleanup (dir: string) =
        if System.IO.Directory.Exists dir then System.IO.Directory.Delete(dir, true)

    let sampleMessage role content : Nao.Runtime.Orleans.PersistedMessage =
        { Role = role
          Content = content
          Timestamp = System.DateTimeOffset.UtcNow
          TurnId = ""
          Steps = [||]
          Attachments = [||] }

    [<TestMethod>]
    member _.AppendAsync_WritesUnderSessionsKeyConversationsFolder() =
        let root = newRoot ()
        try
            let store = Nao.Runtime.Orleans.FileConversationStore(root) :> IConversationStore
            store.AppendAsync "dev/f5a15b5b" "default" [| sampleMessage "user" "hi" |] |> fun t -> t.Wait()
            // The data nests at <root>/<sanitized key>/conversations/, sharing the parent
            // with the session's files/observability/feedback folders.
            let convDir = System.IO.Path.Combine(root, "dev_f5a15b5b", "conversations")
            Assert.IsTrue(System.IO.Directory.Exists convDir, "conversations subfolder should exist")
            Assert.IsTrue(System.IO.File.Exists(System.IO.Path.Combine(convDir, "default.jsonl")))
            Assert.IsTrue(System.IO.File.Exists(System.IO.Path.Combine(convDir, "default.meta.json")))
        finally
            cleanup root

    [<TestMethod>]
    member _.RoundTrip_LoadReturnsAppendedMessages() =
        let root = newRoot ()
        try
            let store = Nao.Runtime.Orleans.FileConversationStore(root) :> IConversationStore
            store.AppendAsync "dev/abc" "default" [| sampleMessage "user" "one" |] |> fun t -> t.Wait()
            store.AppendAsync "dev/abc" "default" [| sampleMessage "assistant" "two" |] |> fun t -> t.Wait()
            let loaded = (store.LoadAsync "dev/abc" "default").Result
            Assert.AreEqual(2, loaded.Length)
            Assert.AreEqual("one", loaded.[0].Content)
            Assert.AreEqual("two", loaded.[1].Content)
        finally
            cleanup root

    [<TestMethod>]
    member _.ListSessions_ReturnsOnlyConversationBearingSessions() =
        let root = newRoot ()
        try
            let store = Nao.Runtime.Orleans.FileConversationStore(root) :> IConversationStore
            store.AppendAsync "dev/s1" "default" [| sampleMessage "user" "hi" |] |> fun t -> t.Wait()
            // A session folder that only holds files (no conversations) must not be listed.
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "dev_s2", "files")) |> ignore
            let sessions = (store.ListSessionsAsync()).Result
            Assert.AreEqual(1, sessions.Length)
            Assert.AreEqual("dev_s1", sessions.[0])
        finally
            cleanup root

    [<TestMethod>]
    member _.DeleteSession_RemovesWholeSessionFolder() =
        let root = newRoot ()
        try
            let store = Nao.Runtime.Orleans.FileConversationStore(root) :> IConversationStore
            store.AppendAsync "dev/s1" "default" [| sampleMessage "user" "hi" |] |> fun t -> t.Wait()
            let sessionRoot = System.IO.Path.Combine(root, "dev_s1")
            // Sibling per-session data should be deleted alongside conversations.
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(sessionRoot, "files")) |> ignore
            store.DeleteSessionAsync "dev/s1" |> fun t -> t.Wait()
            Assert.IsFalse(System.IO.Directory.Exists sessionRoot)
        finally
            cleanup root
