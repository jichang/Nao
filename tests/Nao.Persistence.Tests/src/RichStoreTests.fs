module RichStoreTests

open System
open System.IO
open Microsoft.VisualStudio.TestTools.UnitTesting
open Microsoft.Data.Sqlite
open Nao.Agents
open Nao.Persistence

let private agent = "rich-agent"

let private sqliteFactory () : DbConnectionFactory =
    let path =
        Path.Combine(Path.GetTempPath(), sprintf "nao-rich-%s.db" (Guid.NewGuid().ToString("N")))

    let cs = sprintf "Data Source=%s" path
    DbConnectionFactory.ofFunc (fun () -> new SqliteConnection(cs) :> Data.Common.DbConnection)

let private tempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), sprintf "nao-rich-%s" (Guid.NewGuid().ToString("N")))

    Directory.CreateDirectory dir |> ignore
    dir

// ---------------- Episodic ----------------

let private episode owner id timestamp importance : Episode =
    { Owner = owner
      Id = id
      Action = "act"
      Observation = "observed"
      Context = "ctx"
      Success = true
      Importance = importance
      Timestamp = timestamp
      Tags = [ "x" ]
      Valence = 0.1
      LinkedEpisodes = [] }

[<TestClass>]
type EpisodicTests() =
    let exercise (make: unit -> EpisodicMemory) =
        task {
            let ownerA, ownerB = Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")
            let cutoff = DateTimeOffset.UtcNow
            let generatedExpiredCount = Random.Shared.Next(2, 7)
            let memory = make ()
            do! memory.RecordAsync(episode ownerA "shared" cutoff 0.8)
            do! memory.RecordAsync(episode ownerB "shared" cutoff 0.8)

            for index in 1..generatedExpiredCount do
                do! memory.RecordAsync(episode ownerA (sprintf "expired-%d" index) (cutoff.AddTicks(-1L)) 0.8)

            do! memory.RecordAsync(episode ownerA "exact-cutoff" cutoff 0.8)

            match! memory.DeleteExpiredAsync ownerA cutoff with
            | Error failure -> Assert.Fail failure.Message
            | Ok count -> Assert.AreEqual(generatedExpiredCount, count)

            let reloaded = make ()
            let! ownerAItems = reloaded.QueryAsync ownerA (EpisodeQuery.Recent 20)
            let! ownerBItems = reloaded.QueryAsync ownerB (EpisodeQuery.Recent 20)
            Assert.AreEqual(2, ownerAItems.Length)
            Assert.AreEqual(1, ownerBItems.Length)
            Assert.IsTrue(ownerAItems |> List.exists (fun item -> item.Id = "exact-cutoff"))

            match! reloaded.DeleteOwnerAsync ownerA with
            | Error failure -> Assert.Fail failure.Message
            | Ok count -> Assert.AreEqual(2, count)

            let afterDelete = make ()
            let! deletedOwnerItems = afterDelete.QueryAsync ownerA (EpisodeQuery.Recent 20)
            let! isolatedItems = afterDelete.QueryAsync ownerB (EpisodeQuery.Recent 20)
            Assert.AreEqual(0, deletedOwnerItems.Length)
            Assert.AreEqual(1, isolatedItems.Length)

            do! afterDelete.RecordAsync(episode ownerA "after-delete" (cutoff.AddMinutes(1.0)) 0.8)
            let finalReload = make ()
            let! restored = finalReload.QueryAsync ownerA (EpisodeQuery.Recent 20)
            Assert.AreEqual([ "after-delete" ], restored |> List.map (fun item -> item.Id))

            match! finalReload.DeleteOwnerAsync " " with
            | Ok _ -> Assert.Fail "Blank episodic-memory owners must be rejected."
            | Error failure -> Assert.AreEqual(PlatformErrorCategory.InvalidInput, failure.Category)
        }

    [<TestMethod>]
    member _.LifecycleParityAcrossBackends() =
        let inMemory = InMemoryEpisodicMemory.create None
        let dir = tempDir ()
        let factory = sqliteFactory ()

        let backends =
            [ "in-memory", fun () -> inMemory
              "file", fun () -> EpisodicMemories.file dir None
              "ado", fun () -> EpisodicMemories.ado factory None ]

        for name, make in backends do
            try
                exercise make |> _.GetAwaiter().GetResult()
            with error ->
                Assert.Fail(sprintf "%s backend failed: %s" name error.Message)

    [<TestMethod>]
    member _.File_RejectsCorruptEpisodicEventsBeforeMutation() =
        let dir = tempDir ()
        let path = Path.Combine(dir, "episodic.jsonl")
        let memory = EpisodicMemories.file dir None
        let original = episode agent "original" DateTimeOffset.UtcNow 0.8
        memory.RecordAsync original |> _.GetAwaiter().GetResult()
        File.AppendAllText(path, "{invalid" + Environment.NewLine)
        let before = File.ReadAllBytes path
        let added = episode agent "added" DateTimeOffset.UtcNow 0.8

        let error =
            Assert.ThrowsExactly<InvalidDataException>(fun () -> memory.RecordAsync added |> _.GetAwaiter().GetResult())

        StringAssert.Contains(error.Message, path)
        StringAssert.Contains(error.Message, "event 2")
        CollectionAssert.AreEqual(before, File.ReadAllBytes path)

        let retained =
            memory.QueryAsync agent (EpisodeQuery.Recent 10) |> _.GetAwaiter().GetResult()

        Assert.IsFalse(retained |> List.exists (fun item -> item.Id = added.Id))

// ---------------- Graph ----------------

let private node owner id createdAt : GraphNode =
    { Owner = owner
      Id = id
      EntityType = "thing"
      Properties = Map.ofList [ "color", "red" ]
      CreatedAt = createdAt
      LastAccessed = createdAt
      AccessCount = 0 }

let private relation owner subject predicate object' timestamp : GraphRelation =
    { Owner = owner
      Subject = subject
      Predicate = predicate
      Object = object'
      Confidence = 1.0
      Source = Some "test"
      Timestamp = timestamp
      Metadata = Map.empty }

[<TestClass>]
type GraphTests() =
    let exercise (make: unit -> GraphMemory) =
        task {
            let ownerA, ownerB = Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")
            let cutoff = DateTimeOffset.UtcNow
            let generatedExpiredCount = Random.Shared.Next(2, 6)
            let graph = make ()

            for owner in [ ownerA; ownerB ] do
                do! graph.UpsertNodeAsync(node owner "shared" cutoff)
                do! graph.UpsertNodeAsync(node owner "target" cutoff)
                do! graph.AddRelationAsync(relation owner "shared" "exact" "target" cutoff)

            do! graph.UpsertNodeAsync(node ownerA "exact-cutoff" cutoff)

            for index in 1..generatedExpiredCount do
                let nodeId = sprintf "expired-%d" index
                do! graph.UpsertNodeAsync(node ownerA nodeId (cutoff.AddTicks(-1L)))
                do! graph.AddRelationAsync(relation ownerA nodeId "cascade" "target" cutoff)

            do! graph.AddRelationAsync(relation ownerA "shared" "expired" "target" (cutoff.AddTicks(-1L)))

            match! graph.DeleteExpiredAsync ownerA cutoff with
            | Error failure -> Assert.Fail failure.Message
            | Ok count -> Assert.AreEqual(generatedExpiredCount * 2 + 1, count)

            let reloaded = make ()
            let! ownerAResult = reloaded.QueryAsync ownerA (GraphQuery.ByEntity "target")
            let! ownerBResult = reloaded.QueryAsync ownerB (GraphQuery.ByEntity "target")
            Assert.AreEqual(1, ownerAResult.Relations.Length)
            Assert.AreEqual(1, ownerBResult.Relations.Length)
            let! exactNode = reloaded.GetByTypeAsync ownerA "thing"
            Assert.IsTrue(exactNode |> List.exists (fun item -> item.Id = "exact-cutoff"))

            do! reloaded.RemoveRelationAsync ownerA "shared" "exact" "target"
            let afterRelationDelete = make ()
            let! withoutRelation = afterRelationDelete.QueryAsync ownerA (GraphQuery.ByEntity "target")
            Assert.AreEqual(0, withoutRelation.Relations.Length)
            let! isolatedRelation = afterRelationDelete.QueryAsync ownerB (GraphQuery.ByEntity "target")
            Assert.AreEqual(1, isolatedRelation.Relations.Length)

            do! afterRelationDelete.AddRelationAsync(relation ownerA "shared" "restored" "target" cutoff)
            do! afterRelationDelete.RemoveNodeAsync ownerA "target"
            let afterNodeDelete = make ()
            let! withoutNode = afterNodeDelete.QueryAsync ownerA (GraphQuery.ByEntity "target")
            Assert.AreEqual(0, withoutNode.Nodes.Length)
            Assert.AreEqual(0, withoutNode.Relations.Length)

            match! afterNodeDelete.DeleteOwnerAsync ownerA with
            | Error failure -> Assert.Fail failure.Message
            | Ok count -> Assert.AreEqual(2, count)

            let afterOwnerDelete = make ()
            let! deletedOwner = afterOwnerDelete.GetByTypeAsync ownerA "thing"
            let! retainedOwner = afterOwnerDelete.QueryAsync ownerB (GraphQuery.ByEntity "target")
            Assert.AreEqual(0, deletedOwner.Length)
            Assert.AreEqual(1, retainedOwner.Relations.Length)

            do! afterOwnerDelete.UpsertNodeAsync(node ownerA "after-delete" (cutoff.AddMinutes(1.0)))
            let finalReload = make ()
            let! restored = finalReload.GetByTypeAsync ownerA "thing"
            Assert.AreEqual([ "after-delete" ], restored |> List.map (fun item -> item.Id))

            match! finalReload.DeleteOwnerAsync " " with
            | Ok _ -> Assert.Fail "Blank graph-memory owners must be rejected."
            | Error failure -> Assert.AreEqual(PlatformErrorCategory.InvalidInput, failure.Category)
        }

    [<TestMethod>]
    member _.LifecycleParityAcrossBackends() =
        let inMemory = InMemoryGraphMemory.create None
        let dir = tempDir ()
        let factory = sqliteFactory ()

        let backends =
            [ "in-memory", fun () -> inMemory
              "file", fun () -> GraphMemories.file dir None
              "ado", fun () -> GraphMemories.ado factory None ]

        for name, make in backends do
            try
                exercise make |> _.GetAwaiter().GetResult()
            with error ->
                Assert.Fail(sprintf "%s backend failed: %s" name error.Message)

    [<TestMethod>]
    member _.File_RejectsCorruptGraphEventsBeforeMutation() =
        let dir = tempDir ()
        let path = Path.Combine(dir, "graph.jsonl")
        let graph = GraphMemories.file dir None

        graph.UpsertNodeAsync(node agent "original" DateTimeOffset.UtcNow)
        |> _.GetAwaiter().GetResult()

        File.AppendAllText(path, "{invalid" + Environment.NewLine)
        let before = File.ReadAllBytes path

        let error =
            Assert.ThrowsExactly<InvalidDataException>(fun () ->
                graph.UpsertNodeAsync(node agent "added" DateTimeOffset.UtcNow)
                |> _.GetAwaiter().GetResult())

        StringAssert.Contains(error.Message, path)
        StringAssert.Contains(error.Message, "event 2")
        CollectionAssert.AreEqual(before, File.ReadAllBytes path)

        let retained = graph.GetByTypeAsync agent "thing" |> _.GetAwaiter().GetResult()
        Assert.IsFalse(retained |> List.exists (fun item -> item.Id = "added"))

// ---------------- Tiered ----------------

let private tieredEntry owner key tier timestamp : TieredMemoryEntry =
    { Owner = owner
      Key = key
      Value = "v"
      Tier = tier
      Timestamp = timestamp
      AccessCount = 0
      Relevance = 0.5
      Tags = [] }

[<TestClass>]
type TieredTests() =
    let exercise (make: unit -> TieredMemory) =
        task {
            let ownerA, ownerB = Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")
            let cutoff = DateTimeOffset.UtcNow
            let generatedExpiredCount = Random.Shared.Next(2, 6)
            let memory = make ()

            for owner in [ ownerA; ownerB ] do
                do! memory.StoreAsync(tieredEntry owner "shared" MemoryTier.LongTerm cutoff)

                for key in [ "capacity-a"; "capacity-b"; "capacity-c" ] do
                    do! memory.StoreAsync(tieredEntry owner key MemoryTier.ShortTerm cutoff)

            do! memory.StoreAsync(tieredEntry ownerA "accessed" MemoryTier.MidTerm cutoff)
            do! memory.RecordAccessAsync ownerA [ "accessed" ] cutoff

            for index in 1..generatedExpiredCount do
                do!
                    memory.StoreAsync(
                        tieredEntry ownerA (sprintf "expired-%d" index) MemoryTier.MidTerm (cutoff.AddTicks(-1L))
                    )

            do! memory.StoreAsync(tieredEntry ownerA "exact-cutoff" MemoryTier.MidTerm cutoff)

            do!
                memory.StoreAsync(
                    tieredEntry ownerA "ttl-expired" MemoryTier.MidTerm (cutoff.AddHours(-1.0).AddTicks(-1L))
                )

            do! memory.StoreAsync(tieredEntry ownerA "ttl-boundary" MemoryTier.MidTerm (cutoff.AddHours(-1.0)))

            let! evicted = memory.EvictAsync ownerA cutoff
            Assert.AreEqual(1, evicted)

            let reloaded = make ()
            let! ownerAShort = reloaded.RetrieveFromTierAsync ownerA MemoryTier.ShortTerm 20
            let! ownerBShort = reloaded.RetrieveFromTierAsync ownerB MemoryTier.ShortTerm 20

            Assert.AreEqual(
                [ "capacity-b"; "capacity-c" ],
                ownerAShort |> List.map (fun entry -> entry.Key) |> List.sort
            )

            Assert.AreEqual(
                [ "capacity-b"; "capacity-c" ],
                ownerBShort |> List.map (fun entry -> entry.Key) |> List.sort
            )

            let! promoted = reloaded.RetrieveFromTierAsync ownerA MemoryTier.LongTerm 20

            Assert.IsTrue(
                promoted
                |> List.exists (fun entry -> entry.Key = "accessed" && entry.AccessCount = 1)
            )

            let! retainedAtTtlBoundary = reloaded.RetrieveFromTierAsync ownerA MemoryTier.MidTerm 20
            Assert.IsTrue(retainedAtTtlBoundary |> List.exists (fun entry -> entry.Key = "ttl-boundary"))

            match! reloaded.DeleteExpiredAsync ownerA cutoff with
            | Error failure -> Assert.Fail failure.Message
            | Ok count -> Assert.AreEqual(generatedExpiredCount + 1, count)

            let afterCutoff = make ()
            let! exact = afterCutoff.RetrieveFromTierAsync ownerA MemoryTier.MidTerm 20
            Assert.IsTrue(exact |> List.exists (fun entry -> entry.Key = "exact-cutoff"))
            Assert.IsFalse(exact |> List.exists (fun entry -> entry.Key = "ttl-boundary"))
            let! isolated = afterCutoff.RetrieveAsync ownerB "v" 20
            Assert.AreEqual(3, isolated.Length)

            match! afterCutoff.DeleteOwnerAsync ownerA with
            | Error failure -> Assert.Fail failure.Message
            | Ok count -> Assert.AreEqual(5, count)

            let afterOwnerDelete = make ()
            let! deleted = afterOwnerDelete.RetrieveAsync ownerA "v" 20
            let! retained = afterOwnerDelete.RetrieveAsync ownerB "v" 20
            Assert.AreEqual(0, deleted.Length)
            Assert.AreEqual(3, retained.Length)

            do!
                afterOwnerDelete.StoreAsync(
                    tieredEntry ownerA "after-delete" MemoryTier.LongTerm (cutoff.AddMinutes(1.0))
                )

            let finalReload = make ()
            let! restored = finalReload.RetrieveAsync ownerA "v" 20
            Assert.AreEqual([ "after-delete" ], restored |> List.map (fun entry -> entry.Key))

            match! finalReload.DeleteOwnerAsync " " with
            | Ok _ -> Assert.Fail "Blank tiered-memory owners must be rejected."
            | Error failure -> Assert.AreEqual(PlatformErrorCategory.InvalidInput, failure.Category)
        }

    [<TestMethod>]
    member _.LifecycleParityAcrossBackends() =
        let config =
            { TieredMemoryConfig.Default with
                ShortTermCapacity = 2
                MidTermCapacity = 20
                PromotionPolicy = MemoryPromotionPolicy.AccessThreshold 1
                MidTermTtl = Some(TimeSpan.FromHours 1.0) }

        let inMemory = InMemoryTieredMemory.create config None
        let dir = tempDir ()
        let factory = sqliteFactory ()

        let backends =
            [ "in-memory", fun () -> inMemory
              "file", fun () -> TieredMemories.file dir config None
              "ado", fun () -> TieredMemories.ado factory config None ]

        for name, make in backends do
            try
                exercise make |> _.GetAwaiter().GetResult()
            with error ->
                Assert.Fail(sprintf "%s backend failed: %s" name error.Message)

    [<TestMethod>]
    member _.File_RejectsCorruptTieredEventsBeforeMutation() =
        let dir = tempDir ()
        let path = Path.Combine(dir, "tiered.jsonl")
        let memory = TieredMemories.file dir TieredMemoryConfig.Default None

        memory.StoreAsync(tieredEntry agent "original" MemoryTier.LongTerm DateTimeOffset.UtcNow)
        |> _.GetAwaiter().GetResult()

        File.AppendAllText(path, "{invalid" + Environment.NewLine)
        let before = File.ReadAllBytes path

        let error =
            Assert.ThrowsExactly<InvalidDataException>(fun () ->
                memory.StoreAsync(tieredEntry agent "added" MemoryTier.LongTerm DateTimeOffset.UtcNow)
                |> _.GetAwaiter().GetResult())

        StringAssert.Contains(error.Message, path)
        StringAssert.Contains(error.Message, "event 2")
        CollectionAssert.AreEqual(before, File.ReadAllBytes path)

        let retained = memory.RetrieveAsync agent "v" 10 |> _.GetAwaiter().GetResult()
        Assert.IsFalse(retained |> List.exists (fun item -> item.Key = "added"))

// ---------------- Working memory ----------------

let private wmItem owner key addedAt expiresAt pinned : WorkingMemoryItem =
    { ExecutionId = owner
      Key = key
      Content = "content"
      Attention = 0.9
      Source = "test"
      AddedAt = addedAt
      ExpiresAt = expiresAt
      Pinned = pinned }

[<TestClass>]
type WorkingMemoryTests() =
    let exercise (make: unit -> WorkingMemory) =
        task {
            let ownerA, ownerB = Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N")
            let cutoff = DateTimeOffset.UtcNow
            let generatedExpiredCount = Random.Shared.Next(2, 6)
            let memory = make ()
            do! memory.SetAsync(wmItem ownerA "shared" cutoff None false)
            do! memory.SetAsync(wmItem ownerB "shared" cutoff None false)

            for index in 1..generatedExpiredCount do
                do!
                    memory.SetAsync(
                        wmItem ownerA (sprintf "expired-%d" index) cutoff (Some(cutoff.AddTicks(-1L))) false
                    )

            do! memory.SetAsync(wmItem ownerA "exact-cutoff" cutoff (Some cutoff) false)
            do! memory.SetAsync(wmItem ownerA "pinned" cutoff (Some(cutoff.AddTicks(-1L))) true)

            match! memory.DeleteExpiredAsync ownerA cutoff with
            | Error failure -> Assert.Fail failure.Message
            | Ok count -> Assert.AreEqual(generatedExpiredCount, count)

            let reloaded = make ()
            let! ownerAItems = reloaded.GetAllAsync ownerA
            let! ownerBItems = reloaded.GetAllAsync ownerB
            Assert.AreEqual(3, ownerAItems.Length)
            Assert.AreEqual(1, ownerBItems.Length)
            Assert.IsTrue(ownerAItems |> List.exists (fun item -> item.Key = "exact-cutoff"))
            Assert.IsTrue(ownerAItems |> List.exists (fun item -> item.Key = "pinned"))

            match! reloaded.DeleteOwnerAsync ownerA with
            | Error failure -> Assert.Fail failure.Message
            | Ok count -> Assert.AreEqual(3, count)

            let afterDelete = make ()
            let! deletedOwnerItems = afterDelete.GetAllAsync ownerA
            let! isolatedItems = afterDelete.GetAllAsync ownerB
            Assert.AreEqual(0, deletedOwnerItems.Length)
            Assert.AreEqual(1, isolatedItems.Length)

            let addedAt = cutoff.AddHours(1.0)
            do! afterDelete.SetAsync(wmItem ownerA "after-delete" addedAt None false)
            let finalReload = make ()
            let! restored = finalReload.GetAsync ownerA "after-delete"
            Assert.IsTrue(restored.IsSome)
            Assert.AreEqual(Some(addedAt + WorkingMemoryConfig.Default.DefaultTtl), restored.Value.ExpiresAt)

            match! finalReload.DeleteOwnerAsync " " with
            | Ok _ -> Assert.Fail "Blank execution owners must be rejected."
            | Error failure -> Assert.AreEqual(PlatformErrorCategory.InvalidInput, failure.Category)
        }

    [<TestMethod>]
    member _.LifecycleParityAcrossBackends() =
        let inMemory = InMemoryWorkingMemory.create WorkingMemoryConfig.Default
        let dir = tempDir ()
        let factory = sqliteFactory ()

        let backends =
            [ "in-memory", fun () -> inMemory
              "file", fun () -> WorkingMemories.file dir WorkingMemoryConfig.Default
              "ado", fun () -> WorkingMemories.ado factory WorkingMemoryConfig.Default ]

        for name, make in backends do
            try
                exercise make |> _.GetAwaiter().GetResult()
            with ex ->
                Assert.Fail(sprintf "%s backend failed: %s" name ex.Message)

    [<TestMethod>]
    member _.File_RejectsCorruptWorkingEventsBeforeMutation() =
        let dir = tempDir ()
        let path = Path.Combine(dir, "working.jsonl")
        let memory = WorkingMemories.file dir WorkingMemoryConfig.Default
        let original = wmItem agent "original" DateTimeOffset.UtcNow None false
        memory.SetAsync original |> _.GetAwaiter().GetResult()
        File.AppendAllText(path, "{invalid" + Environment.NewLine)
        let before = File.ReadAllBytes path
        let added = wmItem agent "added" DateTimeOffset.UtcNow None false

        let error =
            Assert.ThrowsExactly<InvalidDataException>(fun () -> memory.SetAsync added |> _.GetAwaiter().GetResult())

        StringAssert.Contains(error.Message, path)
        StringAssert.Contains(error.Message, "event 2")
        CollectionAssert.AreEqual(before, File.ReadAllBytes path)
        Assert.IsTrue((memory.GetAsync agent "added" |> _.GetAwaiter().GetResult()).IsNone)

// ---------------- Metrics ----------------

[<TestClass>]
type MetricsTests() =
    let exercise (make: unit -> MetricsCollector) =
        task {
            let ownerA, ownerB = "metrics/a", "metrics/b"
            let cutoff = DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)
            let llmCount = Random.Shared.Next(2, 7)
            let first = make ()

            for index in 1..llmCount do
                first.Record(
                    MetricRecord.llmCall
                        ownerA
                        (cutoff.AddMinutes(float index))
                        (index * 10)
                        (index * 5)
                        (int64 (index * 20))
                )

            first.Record(
                MetricRecord.custom
                    ownerA
                    (cutoff.AddTicks(-1L))
                    { Name = "old"
                      Value = 1.0
                      Labels = Map.empty }
            )

            first.Record(
                MetricRecord.custom
                    ownerA
                    cutoff
                    { Name = "boundary"
                      Value = 2.0
                      Labels = Map.empty }
            )

            first.Record(MetricRecord.toolCall ownerB cutoff "search" 30L true)

            let reloaded = make ()
            let metricsA = reloaded.GetMetrics ownerA
            let expectedInput = [ 1..llmCount ] |> List.sumBy (fun index -> index * 10)
            let expectedOutput = [ 1..llmCount ] |> List.sumBy (fun index -> index * 5)
            Assert.AreEqual(llmCount, metricsA.TotalLlmCalls)
            Assert.AreEqual(expectedInput, metricsA.TotalInputTokens)
            Assert.AreEqual(expectedOutput, metricsA.TotalOutputTokens)
            Assert.AreEqual(0, metricsA.TotalToolCalls)
            Assert.AreEqual(1, (reloaded.GetMetrics ownerB).TotalToolCalls)

            match! reloaded.DeleteExpiredAsync ownerA cutoff with
            | Error failure -> Assert.Fail failure.Message
            | Ok count -> Assert.AreEqual(1, count)

            let afterExpiry = make ()
            Assert.AreEqual(TimeSpan.FromMinutes(float llmCount), (afterExpiry.GetMetrics ownerA).TotalDuration)

            match! afterExpiry.DeleteOwnerAsync ownerB with
            | Error failure -> Assert.Fail failure.Message
            | Ok count -> Assert.AreEqual(1, count)

            match! afterExpiry.DeleteOwnerAsync " " with
            | Ok _ -> Assert.Fail "Blank metric owners must be rejected."
            | Error failure -> Assert.AreEqual(PlatformErrorCategory.InvalidInput, failure.Category)

            match! afterExpiry.DeleteOwnerAsync ownerA with
            | Error failure -> Assert.Fail failure.Message
            | Ok count -> Assert.AreEqual(llmCount + 1, count)

            let afterDeletion = make ()
            Assert.AreEqual(0, (afterDeletion.GetMetrics ownerA).TotalLlmCalls)
            Assert.AreEqual(0, (afterDeletion.GetMetrics ownerB).TotalToolCalls)

            afterDeletion.Record(MetricRecord.llmCall ownerA (cutoff.AddDays 1) 7 3 11L)
            Assert.AreEqual(1, ((make ()).GetMetrics ownerA).TotalLlmCalls)
        }

    [<TestMethod>]
    member _.LifecycleParityAcrossBackends() =
        let inMemory = InMemoryMetricsCollector.create ()
        let dir = tempDir ()
        let factory = sqliteFactory ()

        let backends =
            [ "in-memory", fun () -> inMemory
              "file", fun () -> MetricsCollectors.file dir
              "ado", fun () -> MetricsCollectors.ado factory ]

        for name, make in backends do
            try
                exercise make |> _.GetAwaiter().GetResult()
            with ex ->
                Assert.Fail(sprintf "%s backend failed: %s" name ex.Message)

    [<TestMethod>]
    member _.File_RejectsCorruptEventsBeforeMutation() =
        let dir = tempDir ()
        let path = Path.Combine(dir, "metrics.jsonl")
        let collector = MetricsCollectors.file dir
        let original = MetricRecord.llmCall agent DateTimeOffset.UtcNow 10 5 20L
        collector.Record original
        File.AppendAllText(path, "{invalid" + Environment.NewLine)
        let before = File.ReadAllBytes path

        let error =
            Assert.ThrowsExactly<InvalidDataException>(fun () ->
                collector.Record(MetricRecord.llmCall agent DateTimeOffset.UtcNow 20 10 30L))

        StringAssert.Contains(error.Message, path)
        StringAssert.Contains(error.Message, "event 2")
        StringAssert.Contains(error.Message, "docs/migrations")
        CollectionAssert.AreEqual(before, File.ReadAllBytes path)
        Assert.AreEqual(1, collector.GetMetrics(agent).TotalLlmCalls)

// ---------------- Trace store ----------------

let private executionTrace () =
    { Id = Guid.NewGuid()
      AgentId = agent
      Input = "in"
      Output = Some "out"
      Steps = []
      StartedAt = DateTimeOffset.UtcNow
      CompletedAt = Some DateTimeOffset.UtcNow
      Success = true
      Metadata = Map.empty }

[<TestClass>]
type TraceStoreTests() =
    let exercise (make: unit -> TraceStore) =
        task {
            let first = make ()
            do! first.SaveAsync(executionTrace ())
            do! first.SaveAsync(executionTrace ())
            let reloaded = make ()
            let! traces = reloaded.GetTracesAsync agent 10
            Assert.AreEqual(2, traces.Length)
        }

    [<TestMethod>]
    member _.TraceStorePurgesByOwnerAcrossBackends() =
        let dir = tempDir ()
        let factory = sqliteFactory ()
        let inMemory = InMemoryTraceStore.create ()

        let stores =
            [ (fun () -> inMemory)
              (fun () -> TraceStores.file dir)
              (fun () -> TraceStores.ado factory) ]

        for make in stores do
            let ownerA = "owner-a"
            let ownerB = "owner-b"
            let cutoff = DateTimeOffset.UtcNow
            let expiredCount = Random.Shared.Next(1, 5)
            let retainedCount = Random.Shared.Next(1, 5)
            let otherCount = Random.Shared.Next(1, 5)

            let trace owner timestamp =
                { executionTrace () with
                    AgentId = owner
                    StartedAt = timestamp }

            let store = make ()

            for _ in 1..expiredCount do
                store.SaveAsync(trace ownerA (cutoff.AddMinutes(-1.0))) |> _.Wait()

            for _ in 1..retainedCount do
                store.SaveAsync(trace ownerA cutoff) |> _.Wait()

            for _ in 1..otherCount do
                store.SaveAsync(trace ownerB (cutoff.AddMinutes(-1.0))) |> _.Wait()

            match store.DeleteExpiredAsync ownerA cutoff |> _.Result with
            | Error failure -> Assert.Fail(failure.Message)
            | Ok deleted -> Assert.AreEqual(expiredCount, deleted)

            let reloaded = make ()
            Assert.AreEqual(retainedCount, reloaded.GetTracesAsync ownerA 100 |> _.Result |> List.length)
            Assert.AreEqual(otherCount, reloaded.GetTracesAsync ownerB 100 |> _.Result |> List.length)

            match reloaded.DeleteOwnerAsync ownerA |> _.Result with
            | Error failure -> Assert.Fail(failure.Message)
            | Ok deleted -> Assert.AreEqual(retainedCount, deleted)

            let reloadedAgain = make ()
            Assert.AreEqual(0, reloadedAgain.GetTracesAsync ownerA 100 |> _.Result |> List.length)
            Assert.AreEqual(otherCount, reloadedAgain.GetTracesAsync ownerB 100 |> _.Result |> List.length)

            match reloadedAgain.DeleteOwnerAsync " " |> _.Result with
            | Ok _ -> Assert.Fail("Blank trace owner unexpectedly accepted.")
            | Error failure -> Assert.AreEqual(PlatformErrorCategory.InvalidInput, failure.Category)

    [<TestMethod>]
    member _.Ado_Persists() =
        let factory = sqliteFactory ()
        (exercise (fun () -> TraceStores.ado factory)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.Ado_RejectsUnversionedEventTableBeforeMutation() =
        let path =
            Path.Combine(Path.GetTempPath(), sprintf "nao-rich-%s.db" (Guid.NewGuid().ToString("N")))

        let factory =
            DbConnectionFactory.ofFunc (fun () ->
                new SqliteConnection(sprintf "Data Source=%s" path) :> Data.Common.DbConnection)

        try
            Ado.executeNonQuery
                factory
                "CREATE TABLE nao_events (stream TEXT NOT NULL, ord INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY (stream, ord))"
                []
            |> _.Wait()

            Ado.executeNonQuery
                factory
                "INSERT INTO nao_events (stream, ord, payload) VALUES ('trace-store', 0, '{}')"
                []
            |> _.Wait()

            let error =
                Assert.ThrowsExactly<InvalidDataException>(fun () -> TraceStores.ado factory |> ignore)

            StringAssert.Contains(error.Message, "unversioned 'nao_events'")
            StringAssert.Contains(error.Message, "docs/migrations")

            let rows =
                Ado.query factory "SELECT payload FROM nao_events" [] (fun reader -> Ado.getString reader "payload")
                |> _.GetAwaiter().GetResult()

            CollectionAssert.AreEqual([| "{}" |], rows |> List.toArray)

            let markerTables =
                Ado.query
                    factory
                    "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'nao_schema_versions'"
                    []
                    (fun reader -> Ado.getString reader "name")
                |> _.GetAwaiter().GetResult()

            Assert.AreEqual(0, markerTables.Length)
        finally
            if File.Exists path then
                File.Delete path

    [<TestMethod>]
    member _.File_Persists() =
        let dir = tempDir ()
        (exercise (fun () -> TraceStores.file dir)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.File_RejectsUnversionedEventsBeforeMutation() =
        let dir = tempDir ()
        let path = Path.Combine(dir, "trace-store.jsonl")
        let store = TraceStores.file dir
        let original = executionTrace ()
        store.SaveAsync original |> _.GetAwaiter().GetResult()

        let persisted = File.ReadAllText path
        StringAssert.Contains(persisted, "\"schemaVersion\":1")

        File.WriteAllText(path, FSharpJson.serialize (TraceStoreEvent.Save original) + Environment.NewLine)
        let before = File.ReadAllBytes path
        let added = executionTrace ()

        let error =
            Assert.ThrowsExactly<InvalidDataException>(fun () -> store.SaveAsync added |> _.GetAwaiter().GetResult())

        StringAssert.Contains(error.Message, path)
        StringAssert.Contains(error.Message, "event 1")
        StringAssert.Contains(error.Message, "docs/migrations")
        CollectionAssert.AreEqual(before, File.ReadAllBytes path)

        let traces = store.GetTracesAsync agent 10 |> _.GetAwaiter().GetResult()
        Assert.IsFalse(traces |> List.exists (fun trace -> trace.Id = added.Id))

// ---------------- Tracer ----------------

[<TestClass>]
type TracerTests() =
    let exercise (make: unit -> Tracer) =
        let first = make ()
        let root = first.StartTrace "op"
        let child = first.StartSpan root "child"
        first.EndSpan child SpanStatus.Ok
        let traceId = root.TraceId
        let reloaded = make ()
        let spans = reloaded.GetTrace traceId
        Assert.AreEqual(2, spans.Length)

    [<TestMethod>]
    member _.Ado_Persists() =
        let factory = sqliteFactory ()
        exercise (fun () -> Tracers.ado factory)

    [<TestMethod>]
    member _.File_Persists() =
        let dir = tempDir ()
        exercise (fun () -> Tracers.file dir)

    [<TestMethod>]
    member _.File_RejectsCorruptSpansBeforeMutation() =
        let dir = tempDir ()
        let path = Path.Combine(dir, "tracer.jsonl")
        let tracer = Tracers.file dir
        let root = tracer.StartTrace "root"

        StringAssert.Contains(File.ReadAllText(path), "\"schemaVersion\":1")
        File.AppendAllText(path, "{invalid" + Environment.NewLine)
        let before = File.ReadAllBytes path

        let error =
            Assert.ThrowsExactly<InvalidDataException>(fun () -> tracer.StartSpan root "child" |> ignore)

        StringAssert.Contains(error.Message, path)
        StringAssert.Contains(error.Message, "span 2")
        StringAssert.Contains(error.Message, "docs/migrations")
        CollectionAssert.AreEqual(before, File.ReadAllBytes path)
        Assert.AreEqual(1, tracer.GetTrace(root.TraceId).Length)
