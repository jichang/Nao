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
    let path =
        Path.Combine(Path.GetTempPath(), sprintf "nao-test-%s.db" (Guid.NewGuid().ToString("N")))

    let cs = sprintf "Data Source=%s" path
    DbConnectionFactory.ofFunc (fun () -> new SqliteConnection(cs) :> Data.Common.DbConnection), path

let private tempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), sprintf "nao-test-%s" (Guid.NewGuid().ToString("N")))

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

        match! store.DeleteOwnerAsync agent with
        | Error failure -> Assert.Fail(failure.Message)
        | Ok deleted -> Assert.AreEqual(1, deleted)

        let! afterClear = store.RecallAllAsync agent
        Assert.AreEqual(0, afterClear.Length)
    }

[<TestClass>]
type MemoryStoreTests() =

    [<TestMethod>]
    member _.MemoryStorePurgesByOwnerAcrossBackends() =
        let dir = tempDir ()
        let factory, databasePath = sqliteFactory ()

        let stores =
            [ InMemoryStore.create (); MemoryStores.file dir; MemoryStores.ado factory ]

        try
            for store in stores do
                let ownerA = "owner-a"
                let ownerB = "owner-b"
                let cutoff = DateTimeOffset.UtcNow
                let expiredCount = Random.Shared.Next(1, 5)
                let retainedCount = Random.Shared.Next(1, 5)
                let otherCount = Random.Shared.Next(1, 5)

                let entry key timestamp =
                    { memEntry key key with
                        Timestamp = timestamp }

                for index in 1..expiredCount do
                    store.SaveAsync ownerA (entry (sprintf "expired-%d" index) (cutoff.AddMinutes(-1.0)))
                    |> _.Wait()

                for index in 1..retainedCount do
                    store.SaveAsync ownerA (entry (sprintf "retained-%d" index) cutoff) |> _.Wait()

                for index in 1..otherCount do
                    store.SaveAsync ownerB (entry (sprintf "other-%d" index) (cutoff.AddMinutes(-1.0)))
                    |> _.Wait()

                match store.DeleteExpiredAsync ownerA cutoff |> _.Result with
                | Error failure -> Assert.Fail(failure.Message)
                | Ok deleted -> Assert.AreEqual(expiredCount, deleted)

                Assert.AreEqual(retainedCount, store.RecallAllAsync ownerA |> _.Result |> List.length)
                Assert.AreEqual(otherCount, store.RecallAllAsync ownerB |> _.Result |> List.length)

                match store.DeleteOwnerAsync ownerA |> _.Result with
                | Error failure -> Assert.Fail(failure.Message)
                | Ok deleted -> Assert.AreEqual(retainedCount, deleted)

                Assert.AreEqual(0, store.RecallAllAsync ownerA |> _.Result |> List.length)
                Assert.AreEqual(otherCount, store.RecallAllAsync ownerB |> _.Result |> List.length)

                match store.DeleteExpiredAsync " " cutoff |> _.Result with
                | Ok _ -> Assert.Fail("Blank memory owner unexpectedly accepted.")
                | Error failure -> Assert.AreEqual(PlatformErrorCategory.InvalidInput, failure.Category)
        finally
            if Directory.Exists dir then
                Directory.Delete(dir, true)

            if File.Exists databasePath then
                File.Delete databasePath

    [<TestMethod>]
    member _.AdoMemoryStore_RoundTrips() =
        let factory, _ = sqliteFactory ()
        (runMemoryStoreRoundTrip (MemoryStores.ado factory)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.AdoMemoryStore_RejectsUnversionedTableBeforeMutation() =
        let factory, databasePath = sqliteFactory ()

        try
            Ado.executeNonQuery
                factory
                "CREATE TABLE nao_memory (agent TEXT NOT NULL, mem_key TEXT NOT NULL, mem_value TEXT NOT NULL, mem_ts TEXT NOT NULL, mem_tags TEXT NOT NULL, PRIMARY KEY (agent, mem_key))"
                []
            |> _.Wait()

            Ado.executeNonQuery
                factory
                "INSERT INTO nao_memory (agent, mem_key, mem_value, mem_ts, mem_tags) VALUES ('existing', 'key', 'value', 'timestamp', '[]')"
                []
            |> _.Wait()

            let store = MemoryStores.ado factory

            let error =
                Assert.ThrowsExactly<InvalidDataException>(fun () ->
                    store.SaveAsync agent (memEntry "new" "value") |> _.GetAwaiter().GetResult())

            StringAssert.Contains(error.Message, "unversioned 'nao_memory'")
            StringAssert.Contains(error.Message, "docs/migrations")

            let rows =
                Ado.query factory "SELECT agent FROM nao_memory" [] (fun reader -> Ado.getString reader "agent")
                |> _.GetAwaiter().GetResult()

            CollectionAssert.AreEqual([| "existing" |], rows |> List.toArray)

            let markerTables =
                Ado.query
                    factory
                    "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'nao_schema_versions'"
                    []
                    (fun reader -> Ado.getString reader "name")
                |> _.GetAwaiter().GetResult()

            Assert.AreEqual(0, markerTables.Length)
        finally
            if File.Exists databasePath then
                File.Delete databasePath

    [<TestMethod>]
    member _.AdoMemoryStore_RejectsUnsupportedVersionBeforeMutation() =
        let factory, databasePath = sqliteFactory ()

        try
            let store = MemoryStores.ado factory
            store.RecallAllAsync agent |> _.Wait()

            Ado.executeNonQuery
                factory
                "UPDATE nao_schema_versions SET schema_version = 2 WHERE component = 'memory'"
                []
            |> _.Wait()

            let error =
                Assert.ThrowsExactly<InvalidDataException>(fun () ->
                    store.SaveAsync agent (memEntry "new" "value") |> _.GetAwaiter().GetResult())

            StringAssert.Contains(error.Message, "version 2")
            StringAssert.Contains(error.Message, "expected 1")

            let rows =
                Ado.query factory "SELECT agent FROM nao_memory" [] (fun reader -> Ado.getString reader "agent")
                |> _.GetAwaiter().GetResult()

            Assert.AreEqual(0, rows.Length)
        finally
            if File.Exists databasePath then
                File.Delete databasePath

    [<TestMethod>]
    member _.AdoMemoryStore_RejectsCorruptRowBeforeMutation() =
        let factory, databasePath = sqliteFactory ()

        try
            let store = MemoryStores.ado factory
            store.RecallAllAsync agent |> _.Wait()

            Ado.executeNonQuery
                factory
                "INSERT INTO nao_memory (agent, mem_key, mem_value, mem_ts, mem_tags) VALUES ('owner', 'corrupt-key', 'value', 'not-a-time', '[]')"
                []
            |> _.Wait()

            let error =
                Assert.ThrowsExactly<InvalidDataException>(fun () ->
                    store.SaveAsync agent (memEntry "new" "value") |> _.GetAwaiter().GetResult())

            StringAssert.Contains(error.Message, "corrupt-key")
            StringAssert.Contains(error.Message, "docs/migrations")

            let rows =
                Ado.query factory "SELECT mem_key FROM nao_memory" [] (fun reader -> Ado.getString reader "mem_key")
                |> _.GetAwaiter().GetResult()

            CollectionAssert.AreEqual([| "corrupt-key" |], rows |> List.toArray)
        finally
            if File.Exists databasePath then
                File.Delete databasePath

    [<TestMethod>]
    member _.FileMemoryStore_RoundTrips() =
        let dir = tempDir ()
        (runMemoryStoreRoundTrip (MemoryStores.file dir)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.FileMemoryStore_RequiresVersionBeforeMutation() =
        let dir = tempDir ()

        try
            let store = MemoryStores.file dir
            let path = Path.Combine(dir, agent + ".json")
            store.SaveAsync agent (memEntry "alpha" "v1") |> _.GetAwaiter().GetResult()

            use document = JsonDocument.Parse(File.ReadAllText path)
            Assert.AreEqual(1, document.RootElement.GetProperty("schemaVersion").GetInt32())

            let currentDocument = File.ReadAllText path
            let withUnknownField = "{\"futureField\":true," + currentDocument.Substring(1)

            File.WriteAllText(path, withUnknownField)
            Assert.AreEqual(1, store.RecallAllAsync agent |> _.GetAwaiter().GetResult() |> List.length)

            File.WriteAllText(path, "[]")
            let before = File.ReadAllBytes path

            let error =
                Assert.ThrowsExactly<InvalidDataException>(fun () ->
                    store.SaveAsync agent (memEntry "beta" "v2") |> _.GetAwaiter().GetResult())

            StringAssert.Contains(error.Message, path)
            StringAssert.Contains(error.Message, "docs/migrations")
            CollectionAssert.AreEqual(before, File.ReadAllBytes path)
        finally
            Directory.Delete(dir, true)

[<TestClass>]
type MemoryToolTests() =

    let runTool (tool: Tool) input =
        match
            tool.RunAsync (AgentContext.allowAll ()) input
            |> fun task -> task.GetAwaiter().GetResult()
        with
        | Ok output -> output
        | Error failure ->
            Assert.Fail(failure.Message)
            ""

    [<TestMethod>]
    member _.RememberedFactCanBeDeliberatelySearched() =
        let store = InMemoryStore.create ()
        let owner = "session:user/one"
        let tools = MemoryTools.create MemoryToolConfig.Default store (fun () -> owner)
        let remember = tools |> List.find (fun tool -> tool.Name = "memory_remember")
        let search = tools |> List.find (fun tool -> tool.Name = "memory_search")

        runTool
            remember
            "{\"key\":\"preferred-format\",\"value\":\"Use HTML previews\",\"tags\":[\"preference\",\"documents\"]}"
        |> ignore

        let result =
            runTool
                search
                "{\"query\":\"HTML format\",\"intent\":\"Recall the user's document preference\",\"tags\":[\"preference\"]}"

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

        for index in 1..itemCount do
            (store.SaveAsync owner (memEntry (sprintf "project-%d" index) (sprintf "Project decision %d" index)))
                .GetAwaiter()
                .GetResult()

        (store.SaveAsync otherOwner (memEntry "project-private" "Other session secret")).GetAwaiter().GetResult()

        let config =
            { MemoryToolConfig.Default with
                MaxSearchResults = maxResults }

        let search =
            MemoryTools.create config store (fun () -> owner)
            |> List.find (fun tool -> tool.Name = "memory_search")

        let result =
            runTool
                search
                "{\"query\":\"project decision\",\"intent\":\"Find earlier project decisions\",\"limit\":999}"

        use document = JsonDocument.Parse result
        let entries = document.RootElement.GetProperty("entries")

        Assert.AreEqual(maxResults, entries.GetArrayLength())
        Assert.IsFalse(result.Contains("Other session secret"))

    [<TestMethod>]
    member _.ForgetRequiresPolicyAndExplicitConfirmation() =
        let store = InMemoryStore.create ()
        let owner = "session:user/forget"
        (store.SaveAsync owner (memEntry "obsolete-decision" "Use the old format")).GetAwaiter().GetResult()

        let defaultTools =
            MemoryTools.create MemoryToolConfig.Default store (fun () -> owner)

        Assert.IsFalse(defaultTools |> List.exists (fun tool -> tool.Name = "memory_forget"))

        let config =
            { MemoryToolConfig.Default with
                ForgetEnabled = true }

        let forget =
            MemoryTools.create config store (fun () -> owner)
            |> List.find (fun tool -> tool.Name = "memory_forget")

        match
            forget.RunAsync
                (AgentContext.allowAll ())
                "{\"key\":\"obsolete-decision\",\"reason\":\"Replace old decision\",\"confirmedByUser\":false}"
            |> fun task -> task.GetAwaiter().GetResult()
        with
        | Ok _ -> Assert.Fail("Unconfirmed deletion unexpectedly succeeded.")
        | Error failure -> Assert.AreEqual(ToolFailureKind.InputContract, failure.Kind)

        runTool
            forget
            "{\"key\":\"obsolete-decision\",\"reason\":\"User explicitly asked to forget it\",\"confirmedByUser\":true}"
        |> ignore

        let remaining = store.RecallAllAsync(owner).GetAwaiter().GetResult()
        Assert.IsTrue(remaining.IsEmpty)

// ---------------- ExecutionJournal ----------------

let private execRecord (tool: string) (at: DateTimeOffset) : ExecutionRecord =
    { Id = Guid.NewGuid()
      Correlation = CorrelationContext.root ()
      Owner = "session:test"
      TurnId = "turn:test"
      ToolName = tool
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
        Assert.AreEqual(r2.Correlation, history.Head.Correlation)

        let! forExecution = journal.GetByExecutionAsync r2.Correlation.ExecutionId
        Assert.AreEqual([ r2 ], forExecution)

        let! unknownExecution = journal.GetByExecutionAsync(ExecutionId.generate ())
        Assert.IsTrue(unknownExecution.IsEmpty)

        let! revertible = journal.GetRevertibleAsync()
        Assert.AreEqual(2, revertible.Length)

        do! journal.MarkRevertedAsync r2.Id
        let! afterRevert = journal.GetRevertibleAsync()
        Assert.AreEqual(1, afterRevert.Length)
        Assert.AreEqual("tool-a", afterRevert.Head.ToolName)
    }

[<TestClass>]
type ExecutionJournalTests() =

    [<TestMethod>]
    member _.ExecutionJournalPurgesByOwnerAcrossBackends() =
        let dir = tempDir ()
        let factory, databasePath = sqliteFactory ()
        let inMemory = InMemoryExecutionJournal.create ()

        let journals =
            [ (fun () -> inMemory)
              (fun () -> ExecutionJournals.file dir)
              (fun () -> ExecutionJournals.ado factory) ]

        try
            for make in journals do
                let ownerA = "session:user/a"
                let ownerB = "session:user/b"
                let cutoff = DateTimeOffset.UtcNow
                let expiredCount = Random.Shared.Next(1, 5)
                let retainedCount = Random.Shared.Next(1, 5)
                let otherCount = Random.Shared.Next(1, 5)

                let record owner timestamp =
                    { execRecord (Guid.NewGuid().ToString("N")) timestamp with
                        Owner = owner
                        Metadata = Map.empty }

                let journal = make ()

                for _ in 1..expiredCount do
                    journal.RecordAsync(record ownerA (cutoff.AddMinutes(-1.0))) |> _.Wait()

                for _ in 1..retainedCount do
                    journal.RecordAsync(record ownerA cutoff) |> _.Wait()

                for _ in 1..otherCount do
                    journal.RecordAsync(record ownerB (cutoff.AddMinutes(-1.0))) |> _.Wait()

                match journal.DeleteExpiredAsync ownerA cutoff |> _.Result with
                | Error failure -> Assert.Fail(failure.Message)
                | Ok deleted -> Assert.AreEqual(expiredCount, deleted)

                let reloaded = make ()
                let afterExpiry = reloaded.GetHistoryAsync() |> _.Result

                Assert.AreEqual(
                    retainedCount,
                    afterExpiry |> List.filter (fun entry -> entry.Owner = ownerA) |> List.length
                )

                Assert.AreEqual(
                    otherCount,
                    afterExpiry |> List.filter (fun entry -> entry.Owner = ownerB) |> List.length
                )

                match reloaded.DeleteOwnerAsync ownerA |> _.Result with
                | Error failure -> Assert.Fail(failure.Message)
                | Ok deleted -> Assert.AreEqual(retainedCount, deleted)

                let reloadedAgain = make ()
                let afterOwnerDeletion = reloadedAgain.GetHistoryAsync() |> _.Result

                Assert.AreEqual(
                    0,
                    afterOwnerDeletion
                    |> List.filter (fun entry -> entry.Owner = ownerA)
                    |> List.length
                )

                Assert.AreEqual(
                    otherCount,
                    afterOwnerDeletion
                    |> List.filter (fun entry -> entry.Owner = ownerB)
                    |> List.length
                )

                match reloadedAgain.DeleteOwnerAsync " " |> _.Result with
                | Ok _ -> Assert.Fail("Blank execution journal owner unexpectedly accepted.")
                | Error failure -> Assert.AreEqual(PlatformErrorCategory.InvalidInput, failure.Category)
        finally
            if Directory.Exists dir then
                Directory.Delete(dir, true)

            if File.Exists databasePath then
                File.Delete databasePath

    [<TestMethod>]
    member _.AdoExecutionJournal_RoundTrips() =
        let factory, _ = sqliteFactory ()
        (runJournalRoundTrip (ExecutionJournals.ado factory)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.InMemoryExecutionJournal_RoundTrips() =
        (runJournalRoundTrip (InMemoryExecutionJournal.create ())).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.FileExecutionJournal_RoundTrips() =
        let dir = tempDir ()
        (runJournalRoundTrip (ExecutionJournals.file dir)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.FileExecutionJournal_RejectsCorruptDocumentBeforeMutation() =
        let dir = tempDir ()

        try
            let path = Path.Combine(dir, "execution-journal.json")
            File.WriteAllText(path, "{invalid")
            let before = File.ReadAllBytes path
            let journal = ExecutionJournals.file dir

            let record =
                { execRecord "tool" DateTimeOffset.UtcNow with
                    Owner = agent
                    TurnId = "turn"
                    Input = "input"
                    Output = "output"
                    Metadata = Map.empty }

            let error =
                Assert.ThrowsExactly<InvalidDataException>(fun () ->
                    journal.RecordAsync record |> _.GetAwaiter().GetResult())

            StringAssert.Contains(error.Message, path)
            StringAssert.Contains(error.Message, "docs/migrations")
            CollectionAssert.AreEqual(before, File.ReadAllBytes path)
        finally
            Directory.Delete(dir, true)

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
    member _.SemanticMemoryPurgesByOwnerAcrossBackends() =
        let dir = tempDir ()
        let factory, databasePath = sqliteFactory ()
        let provider = SimpleEmbeddingProvider.create ()

        let backends =
            [ SemanticMemories.inMemory provider
              SemanticMemories.file provider dir
              SemanticMemories.ado provider factory ]

        try
            for memory in backends do
                let ownerA = "owner-a"
                let ownerB = "owner-b"
                let expiredCount = Random.Shared.Next(1, 5)
                let retainedCount = Random.Shared.Next(1, 5)
                let otherCount = Random.Shared.Next(1, 5)

                for index in 1..expiredCount do
                    memory.StoreAsync ownerA (sprintf "expired-%d" index) "expired owner A"
                    |> _.Wait()

                for index in 1..otherCount do
                    memory.StoreAsync ownerB (sprintf "other-%d" index) "owner B" |> _.Wait()

                let cutoff = DateTimeOffset.UtcNow

                for index in 1..retainedCount do
                    memory.StoreAsync ownerA (sprintf "retained-%d" index) "retained owner A"
                    |> _.Wait()

                match memory.DeleteExpiredAsync ownerA cutoff |> _.Result with
                | Error failure -> Assert.Fail(failure.Message)
                | Ok deleted -> Assert.AreEqual(expiredCount, deleted)

                Assert.AreEqual(retainedCount, memory.RetrieveAsync ownerA "owner" 100 |> _.Result |> List.length)
                Assert.AreEqual(otherCount, memory.RetrieveAsync ownerB "owner" 100 |> _.Result |> List.length)

                match memory.DeleteOwnerAsync ownerA |> _.Result with
                | Error failure -> Assert.Fail(failure.Message)
                | Ok deleted -> Assert.AreEqual(retainedCount, deleted)

                Assert.AreEqual(0, memory.RetrieveAsync ownerA "owner" 100 |> _.Result |> List.length)
                Assert.AreEqual(otherCount, memory.RetrieveAsync ownerB "owner" 100 |> _.Result |> List.length)

                match memory.DeleteOwnerAsync " " |> _.Result with
                | Ok _ -> Assert.Fail("Blank semantic memory owner unexpectedly accepted.")
                | Error failure -> Assert.AreEqual(PlatformErrorCategory.InvalidInput, failure.Category)
        finally
            if Directory.Exists dir then
                Directory.Delete(dir, true)

            if File.Exists databasePath then
                File.Delete databasePath

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

    [<TestMethod>]
    member _.FileSemanticMemory_RejectsCorruptDocumentBeforeMutation() =
        let dir = tempDir ()

        try
            let provider = SimpleEmbeddingProvider.create ()
            let memory = SemanticMemories.file provider dir
            let path = Path.Combine(dir, agent + ".json")
            File.WriteAllText(path, "{invalid")
            let before = File.ReadAllBytes path

            let error =
                Assert.ThrowsExactly<InvalidDataException>(fun () ->
                    memory.StoreAsync agent "doc1" "the quick brown fox"
                    |> _.GetAwaiter().GetResult())

            StringAssert.Contains(error.Message, path)
            StringAssert.Contains(error.Message, "docs/migrations")
            CollectionAssert.AreEqual(before, File.ReadAllBytes path)
        finally
            Directory.Delete(dir, true)

// ---------------- AuditLog ----------------

let private auditEntry permitted execId : AuditEntry =
    { Id = Guid.NewGuid()
      Timestamp = DateTimeOffset.UtcNow
      AgentId = agent
      Action = AuditAction.ToolInvocation "search"
      Input = Some "query"
      Output = Some "result"
      Permitted = permitted
      Decision = PermissionDecision.Allow
      ConstitutionViolations = [ "none" ]
      ExecutionId = execId
      Metadata = Map.ofList [ "src", "test" ] }

let private runAuditRoundTrip (log: AuditLog) =
    task {
        let exec = ExecutionId.generate ()
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
    member _.AuditLogPurgesByOwnerAcrossBackends() =
        let dir = tempDir ()
        let factory, databasePath = sqliteFactory ()
        let backends = [ AuditLogs.inMemory (); AuditLogs.file dir; AuditLogs.ado factory ]

        try
            for log in backends do
                let ownerA = "owner-a"
                let ownerB = "owner-b"
                let cutoff = DateTimeOffset.UtcNow
                let expiredCount = Random.Shared.Next(1, 5)
                let retainedCount = Random.Shared.Next(1, 5)
                let otherCount = Random.Shared.Next(1, 5)

                let entry owner timestamp =
                    { auditEntry true None with
                        Id = Guid.NewGuid()
                        AgentId = owner
                        Timestamp = timestamp }

                for _ in 1..expiredCount do
                    log.RecordAsync(entry ownerA (cutoff.AddMinutes(-1.0))) |> _.Wait()

                for _ in 1..retainedCount do
                    log.RecordAsync(entry ownerA (cutoff.AddMinutes(1.0))) |> _.Wait()

                for _ in 1..otherCount do
                    log.RecordAsync(entry ownerB (cutoff.AddMinutes(-1.0))) |> _.Wait()

                match log.DeleteExpiredAsync ownerA cutoff |> _.Result with
                | Error failure -> Assert.Fail(failure.Message)
                | Ok deleted -> Assert.AreEqual(expiredCount, deleted)

                Assert.AreEqual(retainedCount, log.QueryAsync ownerA DateTimeOffset.MinValue |> _.Result |> List.length)
                Assert.AreEqual(otherCount, log.QueryAsync ownerB DateTimeOffset.MinValue |> _.Result |> List.length)

                match log.DeleteOwnerAsync ownerA |> _.Result with
                | Error failure -> Assert.Fail(failure.Message)
                | Ok deleted -> Assert.AreEqual(retainedCount, deleted)

                Assert.AreEqual(0, log.QueryAsync ownerA DateTimeOffset.MinValue |> _.Result |> List.length)
                Assert.AreEqual(otherCount, log.QueryAsync ownerB DateTimeOffset.MinValue |> _.Result |> List.length)

                match log.DeleteOwnerAsync " " |> _.Result with
                | Ok _ -> Assert.Fail("Blank audit owner unexpectedly accepted.")
                | Error failure -> Assert.AreEqual(PlatformErrorCategory.InvalidInput, failure.Category)
        finally
            if Directory.Exists dir then
                Directory.Delete(dir, true)

            if File.Exists databasePath then
                File.Delete databasePath

    [<TestMethod>]
    member _.AdoAuditLog_RoundTrips() =
        let factory, _ = sqliteFactory ()
        (runAuditRoundTrip (AuditLogs.ado factory)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.AdoAuditLog_RejectsInvalidActionBeforeMutation() =
        let factory, databasePath = sqliteFactory ()

        try
            let log = AuditLogs.ado factory
            log.QueryAsync agent DateTimeOffset.MinValue |> _.Wait()
            let corruptId = Guid.NewGuid().ToString("D")

            Ado.executeNonQuery
                factory
                "INSERT INTO nao_audit (audit_id, audit_ts, agent_name, agent_desc, action_json, audit_input, audit_output, permitted, permission_level, violations, execution_id, metadata) VALUES (@id, @ts, @agent, '', '{\"Kind\":\"Unknown\",\"A\":null,\"B\":null}', NULL, NULL, 1, 'Allow', '[]', NULL, '{}')"
                [ "@id", box corruptId
                  "@ts", box (Time.toIso DateTimeOffset.UtcNow)
                  "@agent", box agent ]
            |> _.Wait()

            let error =
                Assert.ThrowsExactly<InvalidDataException>(fun () ->
                    log.RecordAsync(auditEntry true None) |> _.GetAwaiter().GetResult())

            StringAssert.Contains(error.Message, corruptId)
            StringAssert.Contains(error.Message, "docs/migrations")

            let rows =
                Ado.query factory "SELECT audit_id FROM nao_audit" [] (fun reader -> Ado.getString reader "audit_id")
                |> _.GetAwaiter().GetResult()

            CollectionAssert.AreEqual([| corruptId |], rows |> List.toArray)
        finally
            if File.Exists databasePath then
                File.Delete databasePath

    [<TestMethod>]
    member _.FileAuditLog_RoundTrips() =
        let dir = tempDir ()
        (runAuditRoundTrip (AuditLogs.file dir)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.FileAuditLog_RequiresVersionBeforeMutation() =
        let dir = tempDir ()

        try
            let log = AuditLogs.file dir
            let path = Path.Combine(dir, "audit-log.json")
            log.RecordAsync(auditEntry true None) |> _.GetAwaiter().GetResult()

            use document = JsonDocument.Parse(File.ReadAllText path)
            Assert.AreEqual(1, document.RootElement.GetProperty("schemaVersion").GetInt32())

            File.WriteAllText(path, "[]")
            let before = File.ReadAllBytes path

            let error =
                Assert.ThrowsExactly<InvalidDataException>(fun () ->
                    log.RecordAsync(auditEntry false None) |> _.GetAwaiter().GetResult())

            StringAssert.Contains(error.Message, path)
            StringAssert.Contains(error.Message, "docs/migrations")
            CollectionAssert.AreEqual(before, File.ReadAllBytes path)
        finally
            Directory.Delete(dir, true)
