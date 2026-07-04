module PersistenceTests

open System
open System.IO
open Microsoft.VisualStudio.TestTools.UnitTesting
open Microsoft.Data.Sqlite
open Nao.Agents
open Nao.Agents
open Nao.Persistence

let private agent = { Name = "test-agent"; Description = "A test agent" }

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

let private runMemoryStoreRoundTrip (store: IMemoryStore) =
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

// ---------------- ExecutionJournal ----------------

let private execRecord tool (at: DateTimeOffset) =
    { ToolName = tool
      Input = "in"
      Output = "out"
      ContentMeta = ContentMeta.WithMeta "text/plain" [ "k", "v" ]
      ExecutedAt = at
      Reverted = false
      Metadata = Map.ofList [ "m", "1" ] }

let private runJournalRoundTrip (journal: IExecutionJournal) =
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
        Assert.AreEqual("v", history.Head.ContentMeta.Metadata.["k"])

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

let private runSemanticRoundTrip (memory: ISemanticMemory) =
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
        let provider = SimpleEmbeddingProvider() :> IEmbeddingProvider
        (runSemanticRoundTrip (SemanticMemories.ado provider factory)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.FileSemanticMemory_RoundTrips() =
        let dir = tempDir ()
        let provider = SimpleEmbeddingProvider() :> IEmbeddingProvider
        (runSemanticRoundTrip (SemanticMemories.file provider dir)).GetAwaiter().GetResult()

// ---------------- AuditLog ----------------

let private auditEntry permitted execId =
    { Id = Guid.NewGuid()
      Timestamp = DateTimeOffset.UtcNow
      AgentId = agent
      Action = AuditAction.ToolInvocation "search"
      Input = Some "query"
      Output = Some "result"
      Permitted = permitted
      PermissionLevel = PermissionLevel.AllowWithAudit
      ConstitutionViolations = [ "none" ]
      ExecutionId = execId
      Metadata = Map.ofList [ "src", "test" ] }

let private runAuditRoundTrip (log: IAuditLog) =
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
