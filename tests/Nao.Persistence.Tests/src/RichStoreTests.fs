module RichStoreTests

open System
open System.IO
open Microsoft.VisualStudio.TestTools.UnitTesting
open Microsoft.Data.Sqlite
open Nao.Agents
open Nao.Agents
open Nao.Persistence

let private agent = "rich-agent"

let private sqliteFactory () : IDbConnectionFactory =
    let path = Path.Combine(Path.GetTempPath(), sprintf "nao-rich-%s.db" (Guid.NewGuid().ToString("N")))
    let cs = sprintf "Data Source=%s" path
    DbConnectionFactory.ofFunc (fun () -> new SqliteConnection(cs) :> Data.Common.DbConnection)

let private tempDir () =
    let dir = Path.Combine(Path.GetTempPath(), sprintf "nao-rich-%s" (Guid.NewGuid().ToString("N")))
    Directory.CreateDirectory dir |> ignore
    dir

// ---------------- Episodic ----------------

let private episode id action =
    { Id = id
      Action = action
      Observation = "observed"
      Context = "ctx"
      Success = true
      Importance = 0.8
      Timestamp = DateTimeOffset.UtcNow
      Tags = [ "x" ]
      Valence = 0.1
      LinkedEpisodes = [] }

[<TestClass>]
type EpisodicTests() =
    let exercise (make: unit -> IEpisodicMemory) =
        task {
            let first = make ()
            do! first.RecordAsync(episode "e1" "act-one")
            do! first.RecordAsync(episode "e2" "act-two")
            let reloaded = make ()
            let! recent = reloaded.QueryAsync(EpisodeQuery.Recent 10)
            Assert.AreEqual(2, recent.Length)
        }

    [<TestMethod>]
    member _.Ado_Persists() =
        let factory = sqliteFactory ()
        (exercise (fun () -> EpisodicMemories.ado factory None)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.File_Persists() =
        let dir = tempDir ()
        (exercise (fun () -> EpisodicMemories.file dir None)).GetAwaiter().GetResult()

// ---------------- Graph ----------------

let private node id =
    { Id = id
      EntityType = "thing"
      Properties = Map.ofList [ "color", "red" ]
      CreatedAt = DateTimeOffset.UtcNow
      LastAccessed = DateTimeOffset.UtcNow
      AccessCount = 0 }

let private relation s p o =
    { Subject = s
      Predicate = p
      Object = o
      Confidence = 1.0
      Source = Some "test"
      Timestamp = DateTimeOffset.UtcNow
      Metadata = Map.empty }

[<TestClass>]
type GraphTests() =
    let exercise (make: unit -> IGraphMemory) =
        task {
            let first = make ()
            do! first.UpsertNodeAsync(node "a")
            do! first.UpsertNodeAsync(node "b")
            do! first.AddRelationAsync(relation "a" "knows" "b")
            let reloaded = make ()
            let! result = reloaded.QueryAsync(GraphQuery.ByEntity "a")
            Assert.AreEqual(1, result.Relations.Length)
            Assert.AreEqual("knows", result.Relations.Head.Predicate)
        }

    [<TestMethod>]
    member _.Ado_Persists() =
        let factory = sqliteFactory ()
        (exercise (fun () -> GraphMemories.ado factory None)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.File_Persists() =
        let dir = tempDir ()
        (exercise (fun () -> GraphMemories.file dir None)).GetAwaiter().GetResult()

// ---------------- Tiered ----------------

let private tieredEntry key =
    { Key = key
      Value = "v"
      Tier = MemoryTier.LongTerm
      Timestamp = DateTimeOffset.UtcNow
      AccessCount = 0
      Relevance = 0.5
      Tags = [] }

[<TestClass>]
type TieredTests() =
    let exercise (make: unit -> ITieredMemory) =
        task {
            let first = make ()
            do! first.StoreAsync(tieredEntry "k1")
            do! first.StoreAsync(tieredEntry "k2")
            let reloaded = make ()
            let! items = reloaded.RetrieveFromTierAsync MemoryTier.LongTerm 10
            Assert.AreEqual(2, items.Length)
        }

    [<TestMethod>]
    member _.Ado_Persists() =
        let factory = sqliteFactory ()
        (exercise (fun () -> TieredMemories.ado factory TieredMemoryConfig.Default None))
            .GetAwaiter()
            .GetResult()

    [<TestMethod>]
    member _.File_Persists() =
        let dir = tempDir ()
        (exercise (fun () -> TieredMemories.file dir TieredMemoryConfig.Default None))
            .GetAwaiter()
            .GetResult()

// ---------------- Working memory ----------------

let private wmItem key =
    { Key = key
      Content = "content"
      Attention = 0.9
      Source = "test"
      AddedAt = DateTimeOffset.UtcNow
      ExpiresAt = None
      Pinned = true }

[<TestClass>]
type WorkingMemoryTests() =
    let exercise (make: unit -> IWorkingMemory) =
        task {
            let first = make ()
            do! first.SetAsync(wmItem "w1")
            do! first.SetAsync(wmItem "w2")
            let reloaded = make ()
            let! all = reloaded.GetAllAsync()
            Assert.AreEqual(2, all.Length)
        }

    [<TestMethod>]
    member _.Ado_Persists() =
        let factory = sqliteFactory ()
        (exercise (fun () -> WorkingMemories.ado factory WorkingMemoryConfig.Default))
            .GetAwaiter()
            .GetResult()

    [<TestMethod>]
    member _.File_Persists() =
        let dir = tempDir ()
        (exercise (fun () -> WorkingMemories.file dir WorkingMemoryConfig.Default))
            .GetAwaiter()
            .GetResult()

// ---------------- Metrics ----------------

[<TestClass>]
type MetricsTests() =
    let exercise (make: unit -> IMetricsCollector) =
        let first = make ()
        first.RecordLlmCall 10 20 100L
        first.RecordLlmCall 5 5 50L
        first.RecordToolCall "t" 30L true
        let reloaded = make ()
        let metrics = reloaded.GetMetrics()
        Assert.AreEqual(2, metrics.TotalLlmCalls)
        Assert.AreEqual(15, metrics.TotalInputTokens)
        Assert.AreEqual(25, metrics.TotalOutputTokens)
        Assert.AreEqual(1, metrics.TotalToolCalls)

    [<TestMethod>]
    member _.Ado_Persists() =
        let factory = sqliteFactory ()
        exercise (fun () -> MetricsCollectors.ado factory)

    [<TestMethod>]
    member _.File_Persists() =
        let dir = tempDir ()
        exercise (fun () -> MetricsCollectors.file dir)

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
    let exercise (make: unit -> ITraceStore) =
        task {
            let first = make ()
            do! first.SaveAsync(executionTrace ())
            do! first.SaveAsync(executionTrace ())
            let reloaded = make ()
            let! traces = reloaded.GetTracesAsync agent 10
            Assert.AreEqual(2, traces.Length)
        }

    [<TestMethod>]
    member _.Ado_Persists() =
        let factory = sqliteFactory ()
        (exercise (fun () -> TraceStores.ado factory)).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.File_Persists() =
        let dir = tempDir ()
        (exercise (fun () -> TraceStores.file dir)).GetAwaiter().GetResult()

// ---------------- Tracer ----------------

[<TestClass>]
type TracerTests() =
    let exercise (make: unit -> ITracer) =
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
