module PersistenceTests

open System
open System.IO
open System.Text.Json
open Microsoft.VisualStudio.TestTools.UnitTesting
open Microsoft.Data.Sqlite
open Nao.Agents
open Nao.Persistence

let private agent = "test-agent"

let private memoryScope session =
    let principal =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []

    AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") (Some(SessionId.parse session))
    |> Option.get

let private auditRetentionScope session =
    let principal =
        SecurityPrincipal.createAuthenticated
            (TenantId.parse "tenant")
            (UserId.parse "user")
            None
            []
            [ AuditRetentionAuthorization.RequiredRole ]
            Map.empty
            AuthenticationStrength.MultiFactor

    AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") (Some(SessionId.parse session))
    |> Option.get

let private memoryKey scope =
    AuthorizationScope.identity scope |> AuthorizationIdentity.storageKey

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

[<TestClass>]
type SecretRedactionPersistenceTests() =
    [<TestMethod>]
    member _.``Observability stores never retain resolved secret material``() =
        let scope = memoryScope "secret-redaction"
        let reference = SecretReference.parse "tests/canary"
        let canary = "persistent-canary-secret"
        let correlation = CorrelationContext.root ()
        let idempotencyKey = IdempotencyKey.forExecution correlation.ExecutionId
        let tracer = InMemoryTracer.create ()
        let journal = InMemoryExecutionJournal.create ()
        let audit = InMemoryAuditLog.create ()
        let traceStore = InMemoryTraceStore.create ()

        let provider =
            SecretProvider.create "test" "1" (fun _ _ _ ->
                System.Threading.Tasks.Task.FromResult(
                    SecretLookup.Available(SecretVersion.initial, SecretValue.create reference canary)
                ))

        SecretRedaction.runAsync (fun () ->
            task {
                let! _ = SecretProvider.resolveAsync scope reference System.Threading.CancellationToken.None provider

                let span = tracer.StartTrace scope correlation $"operation:{canary}"
                tracer.SetAttributes scope span (Map.ofList [ "value", canary ])
                tracer.EndSpan scope span (SpanStatus.Error TelemetryErrorCategory.Internal)

                do!
                    journal.RecordAsync
                        scope
                        { Id = Guid.NewGuid()
                          Failure = None
                          IdempotencyKey = idempotencyKey
                          Correlation = correlation
                          TurnId = "turn"
                          ToolName = "tool"
                          ReplaySafety = ToolReplaySafety.RetrySafe
                          Input = $"input:{canary}"
                          Output = $"output:{canary}"
                          ExecutedAt = DateTimeOffset.UtcNow
                          Reverted = false
                          Metadata = Map.ofList [ "detail", canary ] }

                do!
                    audit.RecordAsync
                        scope
                        (AuditLog.toolInvocation
                            "agent"
                            "tool"
                            $"input:{canary}"
                            $"output:{canary}"
                            true
                            PermissionDecision.Allow
                            (Some correlation.ExecutionId))

                let trace =
                    Verification.startTrace correlation "agent" $"trace-input:{canary}"
                    |> Verification.addStep TraceAction.Thinking canary canary 1L
                    |> Verification.complete $"trace-output:{canary}"

                do! traceStore.SaveAsync scope trace
            })
        |> _.GetAwaiter().GetResult()

        let span = tracer.GetByExecution scope correlation.ExecutionId |> List.exactlyOne
        Assert.AreEqual("operation:[REDACTED]", span.OperationName)
        Assert.AreEqual("[REDACTED]", span.Attributes.["value"])
        Assert.AreEqual(SpanStatus.Error TelemetryErrorCategory.Internal, span.Status)

        let record =
            journal.GetByExecutionAsync scope correlation.ExecutionId
            |> _.Result
            |> List.exactlyOne

        Assert.AreEqual("input:[REDACTED]", record.Input)
        Assert.AreEqual("output:[REDACTED]", record.Output)
        Assert.AreEqual("[REDACTED]", record.Metadata.["detail"])

        let entry =
            audit.QueryByExecutionAsync scope correlation.ExecutionId
            |> _.Result
            |> List.exactlyOne

        Assert.AreEqual(Some "input:[REDACTED]", entry.Input)
        Assert.AreEqual(Some "output:[REDACTED]", entry.Output)

        let storedTrace =
            traceStore.GetByExecutionAsync scope correlation.ExecutionId
            |> _.Result
            |> List.exactlyOne

        Assert.AreEqual("trace-input:[REDACTED]", storedTrace.Input)
        Assert.AreEqual(Some "trace-output:[REDACTED]", storedTrace.Output)
        Assert.AreEqual("[REDACTED]", storedTrace.Steps |> List.exactlyOne |> _.Input)

    [<TestMethod>]
    member _.``Serialized observability streams contain no canary secret``() =
        let root = tempDir ()

        try
            let scope = memoryScope "serialized-secret-redaction"
            let reference = SecretReference.parse "tests/serialized-canary"
            let canary = "serialized-canary-" + Guid.NewGuid().ToString("N")
            let correlation = CorrelationContext.root ()
            let idempotencyKey = IdempotencyKey.forExecution correlation.ExecutionId
            let tracer = Tracers.file root
            let journal = ExecutionJournals.file root
            let audit = AuditLogs.file root
            let traceStore = TraceStores.file root

            let provider =
                SecretProvider.create "test" "1" (fun _ _ _ ->
                    System.Threading.Tasks.Task.FromResult(
                        SecretLookup.Available(SecretVersion.initial, SecretValue.create reference canary)
                    ))

            SecretRedaction.runAsync (fun () ->
                task {
                    let! _ =
                        SecretProvider.resolveAsync scope reference System.Threading.CancellationToken.None provider

                    let span = tracer.StartTrace scope correlation $"operation:{canary}"
                    tracer.SetAttributes scope span (Map.ofList [ canary, canary ])
                    tracer.EndSpan scope span (SpanStatus.Error TelemetryErrorCategory.Internal)

                    do!
                        journal.RecordAsync
                            scope
                            { Id = Guid.NewGuid()
                              Failure = None
                              IdempotencyKey = idempotencyKey
                              Correlation = correlation
                              TurnId = "turn"
                              ToolName = $"tool:{canary}"
                              ReplaySafety = ToolReplaySafety.RetrySafe
                              Input = canary
                              Output = canary
                              ExecutedAt = DateTimeOffset.UtcNow
                              Reverted = false
                              Metadata = Map.ofList [ canary, canary ] }

                    do!
                        audit.RecordAsync
                            scope
                            (AuditLog.toolInvocation
                                "agent"
                                $"tool:{canary}"
                                canary
                                canary
                                true
                                PermissionDecision.Allow
                                (Some correlation.ExecutionId))

                    let trace =
                        Verification.startTrace correlation "agent" canary
                        |> Verification.addStep (TraceAction.ToolInvocation canary) canary canary 1L
                        |> Verification.complete canary

                    do! traceStore.SaveAsync scope trace
                })
            |> _.GetAwaiter().GetResult()

            let serialized =
                Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                |> Array.map File.ReadAllText
                |> String.concat "\n"

            Assert.IsTrue(serialized.Contains "[REDACTED]")
            Assert.IsFalse(serialized.Contains canary)
        finally
            Directory.Delete(root, true)

// ---------------- MemoryStore ----------------

let private runMemoryStoreRoundTrip (store: MemoryStore) =
    let agent = memoryScope agent

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
    let agent = memoryScope agent

    [<TestMethod>]
    member _.MemoryStorePurgesByOwnerAcrossBackends() =
        let dir = tempDir ()
        let factory, databasePath = sqliteFactory ()

        let stores =
            [ InMemoryStore.create (); MemoryStores.file dir; MemoryStores.ado factory ]

        try
            for store in stores do
                let ownerA = memoryScope "owner-a"
                let ownerB = memoryScope "owner-b"
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

                Assert.AreEqual(Ok 0, store.DeleteOwnerAsync ownerA |> _.Result)
        finally
            if Directory.Exists dir then
                Directory.Delete(dir, true)

            if File.Exists databasePath then
                File.Delete databasePath

    [<TestMethod>]
    member _.AdoMemoryStore_RoundTrips() =
        let factory, path = sqliteFactory ()

        try
            (runMemoryStoreRoundTrip (MemoryStores.ado factory)).GetAwaiter().GetResult()
        finally
            File.Delete path

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
                Ado.query factory "SELECT owner_key FROM nao_memory" [] (fun reader -> Ado.getString reader "owner_key")
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
                "INSERT INTO nao_memory (owner_key, mem_key, mem_value, mem_ts, mem_tags) VALUES (@owner, 'corrupt-key', 'value', 'not-a-time', '[]')"
                [ "@owner", box (memoryKey agent) ]
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

        try
            (runMemoryStoreRoundTrip (MemoryStores.file dir)).GetAwaiter().GetResult()
        finally
            Directory.Delete(dir, true)

    [<TestMethod>]
    member _.FileMemoryStore_RequiresVersionBeforeMutation() =
        let dir = tempDir ()

        try
            let store = MemoryStores.file dir
            let path = Path.Combine(dir, memoryKey agent + ".json")
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

    let context owner =
        { AgentContext.unrestrictedForTests () with
            Authorization = Some owner }

    let runTool owner (tool: Tool) input =
        match tool.RunAsync (context owner) input |> fun task -> task.GetAwaiter().GetResult() with
        | Ok output -> output
        | Error failure ->
            Assert.Fail(failure.Message)
            ""

    [<TestMethod>]
    member _.RememberedFactCanBeDeliberatelySearched() =
        let store = InMemoryStore.create ()
        let owner = memoryScope "one"
        let tools = MemoryTools.create MemoryToolConfig.Default store None
        let remember = tools |> List.find (fun tool -> tool.Name = "memory_remember")
        let search = tools |> List.find (fun tool -> tool.Name = "memory_search")

        runTool
            owner
            remember
            "{\"key\":\"preferred-format\",\"value\":\"Use HTML previews\",\"tags\":[\"preference\",\"documents\"]}"
        |> ignore

        let result =
            runTool
                owner
                search
                "{\"query\":\"HTML format\",\"intent\":\"Recall the user's document preference\",\"source\":\"memory\",\"memoryTags\":[\"preference\"]}"

        use document = JsonDocument.Parse result

        StringAssert.Contains(result, "preferred-format")
        StringAssert.Contains(result, "Use HTML previews")
        Assert.AreEqual("Recall the user's document preference", document.RootElement.GetProperty("intent").GetString())

    [<TestMethod>]
    member _.SearchIsBoundedAndOwnerScoped() =
        let store = InMemoryStore.create ()
        let owner = memoryScope "active"
        let otherOwner = memoryScope "other"
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
            MemoryTools.create config store None
            |> List.find (fun tool -> tool.Name = "memory_search")

        let result =
            runTool
                owner
                search
                "{\"query\":\"project decision\",\"intent\":\"Find earlier project decisions\",\"source\":\"memory\",\"limit\":999}"

        use document = JsonDocument.Parse result
        let entries = document.RootElement.GetProperty("memories")

        Assert.AreEqual(maxResults, entries.GetArrayLength())
        Assert.IsFalse(result.Contains("Other session secret"))

    [<TestMethod>]
    member _.ForgetRequiresPolicyAndExplicitConfirmation() =
        let store = InMemoryStore.create ()
        let owner = memoryScope "forget"
        (store.SaveAsync owner (memEntry "obsolete-decision" "Use the old format")).GetAwaiter().GetResult()

        let defaultTools = MemoryTools.create MemoryToolConfig.Default store None

        Assert.IsFalse(defaultTools |> List.exists (fun tool -> tool.Name = "memory_forget"))

        let config =
            { MemoryToolConfig.Default with
                ForgetEnabled = true }

        let forget =
            MemoryTools.create config store None
            |> List.find (fun tool -> tool.Name = "memory_forget")

        match
            forget.RunAsync
                (context owner)
                "{\"key\":\"obsolete-decision\",\"reason\":\"Replace old decision\",\"confirmedByUser\":false}"
            |> fun task -> task.GetAwaiter().GetResult()
        with
        | Ok _ -> Assert.Fail("Unconfirmed deletion unexpectedly succeeded.")
        | Error failure -> Assert.AreEqual(ToolFailureKind.InputContract, failure.Kind)

        runTool
            owner
            forget
            "{\"key\":\"obsolete-decision\",\"reason\":\"User explicitly asked to forget it\",\"confirmedByUser\":true}"
        |> ignore

        let remaining = store.RecallAllAsync(owner).GetAwaiter().GetResult()
        Assert.IsTrue(remaining.IsEmpty)

// ---------------- ExecutionJournal ----------------

let private execRecord (tool: string) (at: DateTimeOffset) : ExecutionRecord =
    let correlation = CorrelationContext.root ()

    { Id = Guid.NewGuid()
      IdempotencyKey =
        IdempotencyKey.forExecution correlation.ExecutionId
        |> IdempotencyKey.operation "tool" 1
      Correlation = correlation
      TurnId = "turn:test"
      ToolName = tool
      ReplaySafety = ToolReplaySafety.ExternallySideEffecting
      Input = "in"
      Output = "out"
      Failure = None
      ExecutedAt = at
      Reverted = false
      Metadata = Map.ofList [ "m", "1" ] }

let private checkpointRecord (correlation: CorrelationContext) turnId recordedAt : HarnessCheckpoint =
    { Id = Guid.NewGuid()
      IdempotencyKey = IdempotencyKey.forExecution correlation.ExecutionId
      Correlation = correlation
      TurnId = turnId
      AgentId = "checkpoint-agent"
      Phase = HarnessCheckpointPhase.Succeeded
      RecordedAt = recordedAt }

let private runJournalRoundTrip scope (journal: ExecutionJournal) =
    task {
        let t0 = DateTimeOffset.UtcNow
        let r1 = execRecord "tool-a" (t0.AddSeconds 1.0)
        let r2 = execRecord "tool-b" (t0.AddSeconds 2.0)

        let failure =
            { Kind = ToolFailureKind.Execution
              Message = "failed outcome"
              Retryable = false }

        let failed =
            { execRecord "tool-failed" (t0.AddSeconds 3.0) with
                Output = ""
                Failure = Some failure }

        do! journal.RecordAsync scope r1
        do! journal.RecordAsync scope r2
        do! journal.RecordAsync scope failed

        let! history = journal.GetHistoryAsync scope
        Assert.AreEqual(3, history.Length)
        // Most recent first
        Assert.AreEqual("tool-failed", history.Head.ToolName)
        Assert.AreEqual("1", history.Head.Metadata.["m"])
        Assert.AreEqual(failed.Correlation, history.Head.Correlation)
        Assert.AreEqual(Some failure, history.Head.Failure)

        let! forExecution = journal.GetByExecutionAsync scope r2.Correlation.ExecutionId
        Assert.AreEqual([ r2 ], forExecution)

        let! unknownExecution = journal.GetByExecutionAsync scope (ExecutionId.generate ())
        Assert.IsTrue(unknownExecution.IsEmpty)

        let checkpoint = checkpointRecord r2.Correlation r2.TurnId (t0.AddSeconds 3.0)

        do! journal.Checkpoints.Save scope checkpoint
        let! checkpoints = journal.Checkpoints.GetByExecution scope checkpoint.Correlation.ExecutionId
        Assert.AreEqual([ checkpoint ], checkpoints)

        let! logicalCheckpoints = journal.Checkpoints.GetByIdempotencyKey scope checkpoint.IdempotencyKey
        Assert.AreEqual([ checkpoint ], logicalCheckpoints)

        let transition: ToolSideEffectTransition =
            { Id = Guid.NewGuid()
              IdempotencyKey = r2.IdempotencyKey
              Correlation = r2.Correlation
              TurnId = r2.TurnId
              ToolName = r2.ToolName
              Input = r2.Input
              Phase = ToolSideEffectPhase.IntentRecorded
              Detail = None
              RecordedAt = t0.AddSeconds 4.0 }

        do! journal.SideEffects.Save scope transition

        let compensation =
            { transition with
                Id = Guid.NewGuid()
                Phase = ToolSideEffectPhase.CompensationSucceeded
                RecordedAt = t0.AddSeconds 5.0 }

        do! journal.SideEffects.Save scope compensation
        let! transitions = journal.SideEffects.GetByIdempotencyKey scope transition.IdempotencyKey
        Assert.AreEqual([ transition; compensation ], transitions)

        let! executionTransitions = journal.SideEffects.GetByExecution scope transition.Correlation.ExecutionId
        Assert.AreEqual([ transition; compensation ], executionTransitions)

        let quarantine =
            { transition with
                Id = Guid.NewGuid()
                IdempotencyKey = r1.IdempotencyKey
                Correlation = r1.Correlation
                ToolName = r1.ToolName
                Phase = ToolSideEffectPhase.Quarantined
                RecordedAt = t0.AddSeconds 6.0 }

        do! journal.SideEffects.Save scope quarantine

        let! revertible = journal.GetRevertibleAsync scope
        Assert.AreEqual([ r2 ], revertible)

        do! journal.MarkRevertedAsync scope r2.Id
        let! afterRevert = journal.GetRevertibleAsync scope
        Assert.IsTrue(afterRevert.IsEmpty)

        match! journal.DeleteScopeAsync scope with
        | Error failure -> Assert.Fail(failure.Message)
        | Ok deleted -> Assert.AreEqual(7, deleted)

        let! deletedTransitions = journal.SideEffects.GetByIdempotencyKey scope transition.IdempotencyKey
        Assert.IsTrue(deletedTransitions.IsEmpty)
    }

[<TestClass>]
type ExecutionJournalTests() =

    [<TestMethod>]
    member _.ExecutionJournalPurgesByScopeAcrossBackends() =
        let dir = tempDir ()
        let factory, databasePath = sqliteFactory ()
        let inMemory = InMemoryExecutionJournal.create ()

        let journals =
            [ (fun () -> inMemory)
              (fun () -> ExecutionJournals.file dir)
              (fun () -> ExecutionJournals.ado factory) ]

        try
            for make in journals do
                let ownerA = memoryScope "journal-a"
                let ownerB = memoryScope "journal-b"
                let cutoff = DateTimeOffset.UtcNow
                let expiredCount = Random.Shared.Next(1, 5)
                let retainedCount = Random.Shared.Next(1, 5)
                let otherCount = Random.Shared.Next(1, 5)

                let record timestamp =
                    { execRecord (Guid.NewGuid().ToString("N")) timestamp with
                        Metadata = Map.empty }

                let journal = make ()

                for _ in 1..expiredCount do
                    journal.RecordAsync ownerA (record (cutoff.AddMinutes(-1.0))) |> _.Wait()

                for _ in 1..retainedCount do
                    journal.RecordAsync ownerA (record cutoff) |> _.Wait()

                for _ in 1..otherCount do
                    journal.RecordAsync ownerB (record (cutoff.AddMinutes(-1.0))) |> _.Wait()

                match journal.DeleteExpiredAsync ownerA cutoff |> _.Result with
                | Error failure -> Assert.Fail(failure.Message)
                | Ok deleted -> Assert.AreEqual(expiredCount, deleted)

                let reloaded = make ()
                Assert.AreEqual(retainedCount, reloaded.GetHistoryAsync ownerA |> _.Result |> List.length)
                Assert.AreEqual(otherCount, reloaded.GetHistoryAsync ownerB |> _.Result |> List.length)

                match reloaded.DeleteScopeAsync ownerA |> _.Result with
                | Error failure -> Assert.Fail(failure.Message)
                | Ok deleted -> Assert.AreEqual(retainedCount, deleted)

                let reloadedAgain = make ()
                Assert.AreEqual(0, reloadedAgain.GetHistoryAsync ownerA |> _.Result |> List.length)
                Assert.AreEqual(otherCount, reloadedAgain.GetHistoryAsync ownerB |> _.Result |> List.length)
        finally
            if Directory.Exists dir then
                Directory.Delete(dir, true)

            if File.Exists databasePath then
                File.Delete databasePath

    [<TestMethod>]
    member _.AdoExecutionJournal_RoundTrips() =
        let factory, _ = sqliteFactory ()
        (runJournalRoundTrip (memoryScope "journal-ado") (ExecutionJournals.ado factory)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.InMemoryExecutionJournal_RoundTrips() =
        (runJournalRoundTrip (memoryScope "journal-memory") (InMemoryExecutionJournal.create ()))
            .GetAwaiter()
            .GetResult()

    [<TestMethod>]
    member _.FileExecutionJournal_RoundTrips() =
        let dir = tempDir ()
        (runJournalRoundTrip (memoryScope "journal-file") (ExecutionJournals.file dir)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.FileExecutionJournal_RejectsCorruptDocumentBeforeMutation() =
        let dir = tempDir ()

        try
            let scope = memoryScope "journal-corrupt"
            let path = Path.Combine(dir, "execution-journal", memoryKey scope + ".jsonl")
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, "{invalid" + Environment.NewLine)
            let before = File.ReadAllBytes path
            let journal = ExecutionJournals.file dir

            let record =
                { execRecord "tool" DateTimeOffset.UtcNow with
                    TurnId = "turn"
                    Input = "input"
                    Output = "output"
                    Metadata = Map.empty }

            let error =
                Assert.ThrowsExactly<InvalidDataException>(fun () ->
                    journal.RecordAsync scope record |> _.GetAwaiter().GetResult())

            StringAssert.Contains(error.Message, "execution-journal/" + memoryKey scope)
            StringAssert.Contains(error.Message, "docs/migrations")
            CollectionAssert.AreEqual(before, File.ReadAllBytes path)
        finally
            Directory.Delete(dir, true)

// ---------------- SemanticMemory ----------------

let private runSemanticRoundTrip (memory: SemanticMemory) =
    let agent = memoryScope agent

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
    let agent = memoryScope agent

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
                let ownerA = memoryScope "semantic-owner-a"
                let ownerB = memoryScope "semantic-owner-b"
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

                Assert.AreEqual(Ok 0, memory.DeleteOwnerAsync ownerA |> _.Result)
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
            let path = Path.Combine(dir, memoryKey agent + ".json")
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
      PermissionEvidence = None
      PolicyEvidence = None
      ConstitutionEvidence = None
      ExecutionId = execId
      Metadata = Map.ofList [ "src", "test" ] }

let private runAuditRoundTrip (scope: AuthorizationScope) (log: AuditLog) =
    task {
        let exec = ExecutionId.generate ()
        let since = DateTimeOffset.UtcNow.AddMinutes -1.0
        do! log.RecordAsync scope (auditEntry true (Some exec))
        do! log.RecordAsync scope (auditEntry false (Some exec))

        let! entries = log.QueryAsync scope agent since
        Assert.AreEqual(2, entries.Length)

        match entries.Head.Action with
        | AuditAction.ToolInvocation t -> Assert.AreEqual("search", t)
        | other -> Assert.Fail(sprintf "Unexpected action: %A" other)

        let! byExec = log.QueryByExecutionAsync scope exec
        Assert.AreEqual(2, byExec.Length)

        let! denied = log.GetDeniedCountAsync scope agent since
        Assert.AreEqual(1, denied)
    }

[<TestClass>]
type AuditLogTests() =

    [<TestMethod>]
    member _.AuditRetentionRequiresAdministrativeRoleAndMultiFactorAuthentication() =
        let ordinary = memoryScope "audit-ordinary"

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            AuditRetentionAuthorization.authorize ordinary |> ignore)
        |> ignore

        let singleFactorPrincipal =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse "tenant")
                (UserId.parse "user")
                None
                []
                [ AuditRetentionAuthorization.RequiredRole ]
                Map.empty
                AuthenticationStrength.SingleFactor

        let singleFactor =
            AuthorizationScope.tryCreate
                singleFactorPrincipal
                None
                (WorkspaceId.parse "workspace")
                (Some(SessionId.parse "audit-single-factor"))
            |> Option.get

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            AuditRetentionAuthorization.authorize singleFactor |> ignore)
        |> ignore

    [<TestMethod>]
    member _.AuditLogPurgesByScopeAcrossBackends() =
        let dir = tempDir ()
        let factory, databasePath = sqliteFactory ()
        let backends = [ AuditLogs.inMemory (); AuditLogs.file dir; AuditLogs.ado factory ]
        let ownerA = auditRetentionScope "audit-a"
        let ownerB = memoryScope "audit-b"

        try
            for log in backends do
                let cutoff = DateTimeOffset.UtcNow
                let expiredCount = Random.Shared.Next(1, 5)
                let retainedCount = Random.Shared.Next(1, 5)
                let otherCount = Random.Shared.Next(1, 5)

                let entry actor timestamp =
                    { auditEntry true None with
                        Id = Guid.NewGuid()
                        AgentId = actor
                        Timestamp = timestamp }

                for _ in 1..expiredCount do
                    log.RecordAsync ownerA (entry agent (cutoff.AddMinutes(-1.0))) |> _.Wait()

                for _ in 1..retainedCount do
                    log.RecordAsync ownerA (entry agent (cutoff.AddMinutes(1.0))) |> _.Wait()

                for _ in 1..otherCount do
                    log.RecordAsync ownerB (entry agent (cutoff.AddMinutes(-1.0))) |> _.Wait()

                let retention = AuditRetentionAuthorization.authorize ownerA

                match log.DeleteExpiredAsync retention cutoff |> _.Result with
                | Error failure -> Assert.Fail(failure.Message)
                | Ok deleted -> Assert.AreEqual(expiredCount, deleted)

                Assert.AreEqual(
                    retainedCount,
                    log.QueryAsync ownerA agent DateTimeOffset.MinValue |> _.Result |> List.length
                )

                Assert.AreEqual(
                    otherCount,
                    log.QueryAsync ownerB agent DateTimeOffset.MinValue |> _.Result |> List.length
                )

                match log.DeleteScopeAsync ownerA |> _.Result with
                | Error failure -> Assert.Fail(failure.Message)
                | Ok deleted -> Assert.AreEqual(retainedCount, deleted)

                Assert.AreEqual(0, log.QueryAsync ownerA agent DateTimeOffset.MinValue |> _.Result |> List.length)

                Assert.AreEqual(
                    otherCount,
                    log.QueryAsync ownerB agent DateTimeOffset.MinValue |> _.Result |> List.length
                )
        finally
            if Directory.Exists dir then
                Directory.Delete(dir, true)

            if File.Exists databasePath then
                File.Delete databasePath

    [<TestMethod>]
    member _.AdoAuditLog_RoundTrips() =
        let factory, _ = sqliteFactory ()
        (runAuditRoundTrip (memoryScope "ado-audit") (AuditLogs.ado factory)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.FileAuditLog_RoundTrips() =
        let dir = tempDir ()
        (runAuditRoundTrip (memoryScope "file-audit") (AuditLogs.file dir)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.FileAuditLog_RequiresVersionBeforeMutation() =
        let dir = tempDir ()

        try
            let log = AuditLogs.file dir
            let scope = memoryScope "version"
            let path = Path.Combine(dir, "audit", memoryKey scope + ".jsonl")
            log.RecordAsync scope (auditEntry true None) |> _.GetAwaiter().GetResult()

            use document = JsonDocument.Parse(File.ReadAllText(path).Trim())
            Assert.AreEqual(1, document.RootElement.GetProperty("Version").GetInt32())

            File.WriteAllText(path, """{"Version":99,"Event":{"Case":"DeleteScope"}}""" + Environment.NewLine)
            let before = File.ReadAllBytes path

            let error =
                Assert.ThrowsExactly<InvalidDataException>(fun () ->
                    log.RecordAsync scope (auditEntry false None) |> _.GetAwaiter().GetResult())

            StringAssert.Contains(error.Message, "audit/" + memoryKey scope)
            StringAssert.Contains(error.Message, "docs/migrations")
            CollectionAssert.AreEqual(before, File.ReadAllBytes path)
        finally
            Directory.Delete(dir, true)
