module PersistenceTests

open System
open System.IO
open System.Text.Json
open Microsoft.VisualStudio.TestTools.UnitTesting
open Microsoft.Data.Sqlite
open Nao.Agents
open Nao.Persistence

let private agent = "test-agent"

/// Create a SQLite-backed connection factory over a fresh temp database file.
let private sqliteFactory () =
    let path = Path.Combine(Path.GetTempPath(), sprintf "nao-test-%s.db" (Guid.NewGuid().ToString("N")))
    let cs = sprintf "Data Source=%s" path
    DbConnectionFactory.ofFunc (fun () -> new SqliteConnection(cs) :> Data.Common.DbConnection), path

let private tempDir () =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "nao-test-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    dir

let private memEntry key value =
    { Key = key
      Value = value
      Timestamp = DateTimeOffset.UtcNow
      Tags = [ "t1"; "t2" ] }

// ---------------- MemoryStore ----------------

let private runMemoryStoreRoundTrip (store: MemoryStore) =
    task {
        do! store.SaveAsync agent (memEntry "alpha" "v1")
        do! store.SaveAsync agent (memEntry "beta" "v2")
        let! all = store.RecallAllAsync agent
        Assert.AreEqual(2, all.Length)

        let! recalled = store.RecallAsync agent "alph"
        Assert.AreEqual(1, recalled.Length)
        Assert.AreEqual("v1", recalled.Head.Value)
        Assert.AreEqual(2, recalled.Head.Tags.Length)

        // Overwrite by key
        do! store.SaveAsync agent (memEntry "alpha" "v1-updated")
        let! afterUpdate = store.RecallAsync agent "alpha"
        Assert.AreEqual(1, afterUpdate.Length)
        Assert.AreEqual("v1-updated", afterUpdate.Head.Value)

        do! store.ForgetAsync agent "alpha"
        let! afterForget = store.RecallAllAsync agent
        Assert.AreEqual(1, afterForget.Length)

        do! store.ClearAsync agent
        let! afterClear = store.RecallAllAsync agent
        Assert.AreEqual(0, afterClear.Length)
    }

[<TestClass>]
type MemoryStoreTests() =

    [<TestMethod>]
    member _.AdoMemoryStore_RoundTrips() =
        let factory, _ = sqliteFactory ()
        (runMemoryStoreRoundTrip (MemoryStores.ado factory)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.FileMemoryStore_RoundTrips() =
        let dir = tempDir ()
        (runMemoryStoreRoundTrip (MemoryStores.file dir)).GetAwaiter().GetResult()

[<TestClass>]
type MemoryToolTests() =

    let runTool (tool: Tool) input =
        match tool.RunAsync AgentContext.allowAll input |> fun task -> task.GetAwaiter().GetResult() with
        | Ok output -> output
        | Error failure -> Assert.Fail(failure.Message); ""

    [<TestMethod>]
    member _.RememberedFactCanBeDeliberatelySearched() =
        let store = InMemoryStore.create ()
        let owner = "session:user/one"
        let tools = MemoryTools.create MemoryToolConfig.Default store (fun () -> owner)
        let remember = tools |> List.find (fun tool -> tool.Name = "memory_remember")
        let search = tools |> List.find (fun tool -> tool.Name = "memory_search")

        runTool remember "{\"key\":\"preferred-format\",\"value\":\"Use HTML previews\",\"tags\":[\"preference\",\"documents\"]}" |> ignore
        let result = runTool search "{\"query\":\"HTML format\",\"intent\":\"Recall the user's document preference\",\"tags\":[\"preference\"]}"
        use document = JsonDocument.Parse result

        StringAssert.Contains(result, "preferred-format")
        StringAssert.Contains(result, "Use HTML previews")
        Assert.AreEqual("Recall the user's document preference", document.RootElement.GetProperty("intent").GetString())

    [<TestMethod>]
    member _.SearchIsBoundedAndOwnerScoped() =
        let store = InMemoryStore.create ()
        let owner = "session:user/active"
        let otherOwner = "session:user/other"
        let itemCount = Random.Shared.Next(3, 8)
        let maxResults = Random.Shared.Next(1, itemCount)
        for index in 1 .. itemCount do
            (store.SaveAsync owner (memEntry (sprintf "project-%d" index) (sprintf "Project decision %d" index))).GetAwaiter().GetResult()
        (store.SaveAsync otherOwner (memEntry "project-private" "Other session secret")).GetAwaiter().GetResult()

        let config = { MemoryToolConfig.Default with MaxSearchResults = maxResults }
        let search = MemoryTools.create config store (fun () -> owner) |> List.find (fun tool -> tool.Name = "memory_search")
        let result = runTool search "{\"query\":\"project decision\",\"intent\":\"Find earlier project decisions\",\"limit\":999}"
        use document = JsonDocument.Parse result
        let entries = document.RootElement.GetProperty("entries")

        Assert.AreEqual(maxResults, entries.GetArrayLength())
        Assert.IsFalse(result.Contains("Other session secret"))

    [<TestMethod>]
    member _.ForgetRequiresPolicyAndExplicitConfirmation() =
        let store = InMemoryStore.create ()
        let owner = "session:user/forget"
        (store.SaveAsync owner (memEntry "obsolete-decision" "Use the old format")).GetAwaiter().GetResult()

        let defaultTools = MemoryTools.create MemoryToolConfig.Default store (fun () -> owner)
        Assert.IsFalse(defaultTools |> List.exists (fun tool -> tool.Name = "memory_forget"))

        let config = { MemoryToolConfig.Default with ForgetEnabled = true }
        let forget = MemoryTools.create config store (fun () -> owner) |> List.find (fun tool -> tool.Name = "memory_forget")
        match forget.RunAsync AgentContext.allowAll "{\"key\":\"obsolete-decision\",\"reason\":\"Replace old decision\",\"confirmedByUser\":false}" |> fun task -> task.GetAwaiter().GetResult() with
        | Ok _ -> Assert.Fail("Unconfirmed deletion unexpectedly succeeded.")
        | Error failure -> Assert.AreEqual(ToolFailureKind.InputContract, failure.Kind)

        runTool forget "{\"key\":\"obsolete-decision\",\"reason\":\"User explicitly asked to forget it\",\"confirmedByUser\":true}" |> ignore
        let remaining = store.RecallAllAsync(owner).GetAwaiter().GetResult()
        Assert.IsTrue(remaining.IsEmpty)

// ---------------- ExecutionJournal ----------------

let private execRecord (tool: string) (at: DateTimeOffset) =
    { ToolName = tool
      Input = "in"
      Output = "out"
      ExecutedAt = at
      Reverted = false
      Metadata = Map.ofList [ "m", "1" ] }

let private runJournalRoundTrip (journal: ExecutionJournal) =
    task {
        let t0 = DateTimeOffset.UtcNow
        let r1 = execRecord "tool-a" (t0.AddSeconds 1.0)
        let r2 = execRecord "tool-b" (t0.AddSeconds 2.0)
        do! journal.RecordAsync r1
        do! journal.RecordAsync r2

        let! history = journal.GetHistoryAsync()
        Assert.AreEqual(2, history.Length)
        // Most recent first
        Assert.AreEqual("tool-b", history.Head.ToolName)
        Assert.AreEqual("1", history.Head.Metadata.["m"])

        let! revertible = journal.GetRevertibleAsync()
        Assert.AreEqual(2, revertible.Length)

        do! journal.MarkRevertedAsync r2
        let! afterRevert = journal.GetRevertibleAsync()
        Assert.AreEqual(1, afterRevert.Length)
        Assert.AreEqual("tool-a", afterRevert.Head.ToolName)
    }

[<TestClass>]
type ExecutionJournalTests() =

    [<TestMethod>]
    member _.AdoExecutionJournal_RoundTrips() =
        let factory, _ = sqliteFactory ()
        (runJournalRoundTrip (ExecutionJournals.ado factory)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.FileExecutionJournal_RoundTrips() =
        let dir = tempDir ()
        (runJournalRoundTrip (ExecutionJournals.file dir)).GetAwaiter().GetResult()

// ---------------- SemanticMemory ----------------

let private runSemanticRoundTrip (memory: SemanticMemory) =
    task {
        do! memory.StoreAsync agent "doc1" "the quick brown fox"
        do! memory.StoreAsync agent "doc2" "lazy dog sleeps"
        let! results = memory.RetrieveAsync agent "quick fox" 1
        Assert.AreEqual(1, results.Length)
        Assert.AreEqual("doc1", results.Head.Key)

        do! memory.RemoveAsync agent "doc1"
        let! afterRemove = memory.RetrieveAsync agent "quick fox" 5
        Assert.IsFalse(afterRemove |> List.exists (fun e -> e.Key = "doc1"))
    }

[<TestClass>]
type SemanticMemoryTests() =

    [<TestMethod>]
    member _.AdoSemanticMemory_RoundTrips() =
        let factory, _ = sqliteFactory ()
        let provider = SimpleEmbeddingProvider.create ()
        (runSemanticRoundTrip (SemanticMemories.ado provider factory)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.FileSemanticMemory_RoundTrips() =
        let dir = tempDir ()
        let provider = SimpleEmbeddingProvider.create ()
        (runSemanticRoundTrip (SemanticMemories.file provider dir)).GetAwaiter().GetResult()

// ---------------- AuditLog ----------------

let private auditEntry permitted execId : AuditEntry =
    { Id = Guid.NewGuid(); Timestamp = DateTimeOffset.UtcNow; AgentId = agent; Action = AuditAction.ToolInvocation "search"; Input = Some "query"; Output = Some "result"; Permitted = permitted; Decision = PermissionDecision.Allow; ConstitutionViolations = [ "none" ]; ExecutionId = execId; Metadata = Map.ofList [ "src", "test" ] }

let private runAuditRoundTrip (log: AuditLog) =
    task {
        let exec = Guid.NewGuid()
        let since = DateTimeOffset.UtcNow.AddMinutes -1.0
        do! log.RecordAsync(auditEntry true (Some exec))
        do! log.RecordAsync(auditEntry false (Some exec))

        let! entries = log.QueryAsync agent since
        Assert.AreEqual(2, entries.Length)
        match entries.Head.Action with
        | AuditAction.ToolInvocation t -> Assert.AreEqual("search", t)
        | other -> Assert.Fail(sprintf "Unexpected action: %A" other)

        let! byExec = log.QueryByExecutionAsync exec
        Assert.AreEqual(2, byExec.Length)

        let! denied = log.GetDeniedCountAsync agent since
        Assert.AreEqual(1, denied)
    }

[<TestClass>]
type AuditLogTests() =

    [<TestMethod>]
    member _.AdoAuditLog_RoundTrips() =
        let factory, _ = sqliteFactory ()
        (runAuditRoundTrip (AuditLogs.ado factory)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.FileAuditLog_RoundTrips() =
        let dir = tempDir ()
        (runAuditRoundTrip (AuditLogs.file dir)).GetAwaiter().GetResult()
