module FeedbackTests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Microsoft.Data.Sqlite
open Nao.Agents
open Nao.Agents
open Nao.Persistence
open Nao.Agents
open Nao.Feedback.Tests

let private tempDir () =
    let d =
        Path.Combine(Path.GetTempPath(), "nao-feedback-tests", Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory d |> ignore
    d

let private sqliteFactory () : DbConnectionFactory =
    let path =
        Path.Combine(Path.GetTempPath(), sprintf "nao-feedback-%s.db" (Guid.NewGuid().ToString("N")))

    let cs = sprintf "Data Source=%s" path
    DbConnectionFactory.ofFunc (fun () -> new SqliteConnection(cs) :> System.Data.Common.DbConnection)

[<TestClass>]
type TurnRecorderTests() =

    [<TestMethod>]
    member _.``Pairs tool invocations with their results in order``() =
        let recorder = TurnRecorder.create ("t1", "s1", "u1", "ws", "agent", "hello")
        let consumer = recorder.Consumer

        let scope =
            EventScope.Create("u1", "s1", "", "ws", "t1", "u1/s1", CorrelationContext.root ())

        let send signal =
            EventConsumer.handleAsync (NaoEvent.TurnProgress(scope, signal)) consumer
            |> _.Wait()

        send (ToolInvoked("search", "query"))
        send (ToolCompleted("search", "results"))
        send (SubAgentInvoked("helper", "subtask"))
        send (SubAgentCompleted("helper", "done"))
        send (AnswerProduced("final answer"))

        let snap = TurnRecorder.snapshot recorder
        Assert.AreEqual(1, snap.ToolCalls.Length)
        Assert.AreEqual("search", snap.ToolCalls.[0].Name)
        Assert.AreEqual("query", snap.ToolCalls.[0].Input)
        Assert.AreEqual("results", snap.ToolCalls.[0].Output)
        Assert.AreEqual(1, snap.SubAgentCalls.Length)
        Assert.AreEqual("helper", snap.SubAgentCalls.[0].Name)
        Assert.AreEqual("subtask", snap.SubAgentCalls.[0].Input)
        Assert.AreEqual("final answer", snap.Output)

    [<TestMethod>]
    member _.``Records tool calls from progress events``() =
        let recorder = TurnRecorder.create ("t1", "s1", "u1", "ws", "agent", "hi")
        let consumer = recorder.Consumer

        let scope =
            EventScope.Create("u1", "s1", "", "ws", "t1", "u1/s1", CorrelationContext.root ())

        let send signal =
            EventConsumer.handleAsync (NaoEvent.TurnProgress(scope, signal)) consumer
            |> _.Wait()

        send (ToolInvoked("search", "q"))
        send (ToolCompleted("search", "r"))
        let snap = TurnRecorder.snapshot recorder
        Assert.AreEqual("search", snap.ToolCalls.[0].Name)

[<TestClass>]
type FileStoreTests() =

    [<TestMethod>]
    member _.``Turn store round-trips via JSONL``() =
        (task {
            let dir = tempDir ()
            let store = FileTurnStore.create dir

            let turn =
                { TurnRecord.Empty with
                    TurnId = "t1"
                    SessionId = "s1"
                    ToolCalls =
                        [ { Name = "search"
                            Input = "q"
                            Output = "r" } ] }

            do! store.SaveAsync turn
            do! store.SaveAsync { turn with Output = "latest" }

            use envelope =
                JsonDocument.Parse(File.ReadLines(Path.Combine(dir, "turns.jsonl")) |> Seq.head)

            Assert.AreEqual(1, envelope.RootElement.GetProperty("schemaVersion").GetInt32())
            Assert.AreEqual("turn.upsert", envelope.RootElement.GetProperty("kind").GetString())
            let! loaded = store.GetAsync "t1"
            Assert.IsTrue(loaded.IsSome)
            Assert.AreEqual("latest", loaded.Value.Output)
            Assert.AreEqual(1, loaded.Value.ToolCalls.Length)
            Assert.AreEqual("search", loaded.Value.ToolCalls.[0].Name)
            let! forSession = store.GetForSessionAsync "s1"
            Assert.AreEqual(1, forSession.Length, "The latest TurnId is the authoritative logical record")
        })
            .GetAwaiter()
            .GetResult()

    [<TestMethod>]
    member _.``Turn store rejects unsupported schema versions``() =
        let dir = tempDir ()

        let invalid: TurnStoreEnvelope =
            { SchemaVersion = 2
              Kind = "turn.upsert"
              Record = Some TurnRecord.Empty
              SessionId = None
              Before = None }

        File.WriteAllText(Path.Combine(dir, "turns.jsonl"), FeedbackJson.serialize invalid + Environment.NewLine)

        try
            FileTurnStore.create(dir).GetAsync("turn").Wait()
            Assert.Fail("Unsupported turn-store schema unexpectedly accepted.")
        with
        | :? InvalidDataException -> ()
        | :? AggregateException as error -> Assert.IsTrue(error.InnerException :? InvalidDataException)

    [<TestMethod>]
    member _.``Turn store rejects corrupt history before save``() =
        let dir = tempDir ()
        let path = Path.Combine(dir, "turns.jsonl")
        File.WriteAllText(path, "{invalid" + Environment.NewLine)
        let before = File.ReadAllBytes path
        let store = FileTurnStore.create dir

        let error =
            Assert.ThrowsExactly<InvalidDataException>(fun () ->
                store.SaveAsync TurnRecord.Empty |> _.GetAwaiter().GetResult())

        StringAssert.Contains(error.Message, path)
        StringAssert.Contains(error.Message, "line 1")
        StringAssert.Contains(error.Message, "docs/migrations")
        CollectionAssert.AreEqual(before, File.ReadAllBytes path)

[<TestClass>]
type TurnStoreLifecycleTests() =

    [<TestMethod>]
    member _.``Purges turns by session across backends``() =
        let dir = tempDir ()
        let factory = sqliteFactory ()
        let inMemory = InMemoryTurnStore.create ()

        let stores =
            [ (fun () -> inMemory)
              (fun () -> FileTurnStore.create dir)
              (fun () -> AdoTurnStore.create factory) ]

        for make in stores do
            let sessionA = "session-a"
            let sessionB = "session-b"
            let cutoff = DateTimeOffset.UtcNow
            let expiredCount = Random.Shared.Next(1, 5)
            let retainedCount = Random.Shared.Next(1, 5)
            let otherCount = Random.Shared.Next(1, 5)

            let turn sessionId timestamp =
                { TurnRecord.Empty with
                    TurnId = Guid.NewGuid().ToString("N")
                    SessionId = sessionId
                    CreatedAt = timestamp }

            let store = make ()

            for _ in 1..expiredCount do
                store.SaveAsync(turn sessionA (cutoff.AddMinutes(-1.0))) |> _.Wait()

            for _ in 1..retainedCount do
                store.SaveAsync(turn sessionA cutoff) |> _.Wait()

            for _ in 1..otherCount do
                store.SaveAsync(turn sessionB (cutoff.AddMinutes(-1.0))) |> _.Wait()

            match store.DeleteExpiredAsync sessionA cutoff |> _.Result with
            | Error failure -> Assert.Fail(failure.Message)
            | Ok deleted -> Assert.AreEqual(expiredCount, deleted)

            let reloaded = make ()
            Assert.AreEqual(retainedCount, reloaded.GetForSessionAsync sessionA |> _.Result |> List.length)
            Assert.AreEqual(otherCount, reloaded.GetForSessionAsync sessionB |> _.Result |> List.length)

            match reloaded.DeleteSessionAsync sessionA |> _.Result with
            | Error failure -> Assert.Fail(failure.Message)
            | Ok deleted -> Assert.AreEqual(retainedCount, deleted)

            let reloadedAgain = make ()
            Assert.AreEqual(0, reloadedAgain.GetForSessionAsync sessionA |> _.Result |> List.length)
            Assert.AreEqual(otherCount, reloadedAgain.GetForSessionAsync sessionB |> _.Result |> List.length)

            match reloadedAgain.DeleteSessionAsync " " |> _.Result with
            | Ok _ -> Assert.Fail("Blank turn session unexpectedly accepted.")
            | Error failure -> Assert.AreEqual(PlatformErrorCategory.InvalidInput, failure.Category)

/// Same coverage as FileStoreTests but against the ADO.NET (SQLite) backend, proving
/// the feedback stores have full parity across InMemory / File / Database modes.
[<TestClass>]
type DatabaseStoreTests() =

    [<TestMethod>]
    member _.``Turn store round-trips via ADO.NET``() =
        (task {
            let factory = sqliteFactory ()
            let store = AdoTurnStore.create factory

            let turn =
                { TurnRecord.Empty with
                    TurnId = "t1"
                    SessionId = "s1"
                    ToolCalls =
                        [ { Name = "search"
                            Input = "q"
                            Output = "r" } ] }

            do! store.SaveAsync turn
            do! store.SaveAsync { turn with Output = "latest" }
            let! loaded = store.GetAsync "t1"
            Assert.IsTrue(loaded.IsSome)
            Assert.AreEqual("latest", loaded.Value.Output)
            Assert.AreEqual(1, loaded.Value.ToolCalls.Length)
            Assert.AreEqual("search", loaded.Value.ToolCalls.[0].Name)
            let! forSession = store.GetForSessionAsync "s1"
            Assert.AreEqual(1, forSession.Length)
        })
            .GetAwaiter()
            .GetResult()

[<TestClass>]
type FeedbackStoreLifecycleTests() =

    [<TestMethod>]
    member _.``Feedback store rejects corrupt history before save``() =
        let dir = tempDir ()
        let path = Path.Combine(dir, "feedback.jsonl")
        File.WriteAllText(path, "{invalid" + Environment.NewLine)
        let before = File.ReadAllBytes path
        let store = FileFeedbackStore.create dir

        let feedback =
            { Id = Guid.NewGuid()
              TurnId = "turn"
              SessionId = "session"
              UserId = "user"
              Sentiment = FeedbackSentiment.Neutral
              Comment = None
              CreatedAt = DateTimeOffset.UtcNow
              Metadata = Map.empty }

        let error =
            Assert.ThrowsExactly<InvalidDataException>(fun () -> store.SaveAsync feedback |> _.GetAwaiter().GetResult())

        StringAssert.Contains(error.Message, path)
        StringAssert.Contains(error.Message, "line 1")
        CollectionAssert.AreEqual(before, File.ReadAllBytes path)

    [<TestMethod>]
    member _.``ADO feedback store rejects corrupt row before save``() =
        let factory = sqliteFactory ()
        let store = AdoFeedbackStore.create factory
        store.GetAllAsync().Wait()

        Ado.executeNonQuery
            factory
            "INSERT INTO nao_feedback_entries (item_id, payload) VALUES ('corrupt-feedback', '{invalid')"
            []
        |> _.Wait()

        let feedback =
            { Id = Guid.NewGuid()
              TurnId = "turn"
              SessionId = "session"
              UserId = "user"
              Sentiment = FeedbackSentiment.Neutral
              Comment = None
              CreatedAt = DateTimeOffset.UtcNow
              Metadata = Map.empty }

        let error =
            Assert.ThrowsExactly<InvalidDataException>(fun () -> store.SaveAsync feedback |> _.GetAwaiter().GetResult())

        StringAssert.Contains(error.Message, "corrupt-feedback")
        StringAssert.Contains(error.Message, "docs/migrations")

        let rows =
            Ado.query factory "SELECT item_id FROM nao_feedback_entries" [] (fun reader ->
                Ado.getString reader "item_id")
            |> _.GetAwaiter().GetResult()

        CollectionAssert.AreEqual([| "corrupt-feedback" |], rows |> List.toArray)

    [<TestMethod>]
    member _.``Purges feedback by user across backends``() =
        let dir = tempDir ()
        let factory = sqliteFactory ()
        let inMemory = InMemoryFeedbackStore.create ()

        let stores =
            [ (fun () -> inMemory)
              (fun () -> FileFeedbackStore.create dir)
              (fun () -> AdoFeedbackStore.create factory) ]

        for make in stores do
            let ownerA = "user-a"
            let ownerB = "user-b"
            let cutoff = DateTimeOffset.UtcNow
            let expiredCount = Random.Shared.Next(1, 5)
            let retainedCount = Random.Shared.Next(1, 5)
            let otherCount = Random.Shared.Next(1, 5)

            let feedback owner sessionId timestamp =
                { Id = Guid.NewGuid()
                  TurnId = Guid.NewGuid().ToString("N")
                  SessionId = sessionId
                  UserId = owner
                  Sentiment = FeedbackSentiment.Neutral
                  Comment = None
                  CreatedAt = timestamp
                  Metadata = Map.empty }

            let store = make ()

            let retained =
                [ for index in 1..retainedCount -> feedback ownerA (sprintf "session-%d" index) cutoff ]

            for _ in 1..expiredCount do
                store.SaveAsync(feedback ownerA "earlier-session" (cutoff.AddMinutes(-1.0)))
                |> _.Wait()

            for entry in retained do
                store.SaveAsync entry |> _.Wait()

            store.SaveAsync
                { retained.Head with
                    Comment = Some "latest" }
            |> _.Wait()

            for _ in 1..otherCount do
                store.SaveAsync(feedback ownerB "other-session" (cutoff.AddMinutes(-1.0)))
                |> _.Wait()

            Assert.AreEqual(expiredCount + retainedCount + otherCount, store.GetAllAsync() |> _.Result |> List.length)

            match store.DeleteExpiredAsync ownerA cutoff |> _.Result with
            | Error failure -> Assert.Fail(failure.Message)
            | Ok deleted -> Assert.AreEqual(expiredCount, deleted)

            let reloaded = make ()
            let afterExpiry = reloaded.GetAllAsync() |> _.Result

            Assert.AreEqual(
                retainedCount,
                afterExpiry |> List.filter (fun entry -> entry.UserId = ownerA) |> List.length
            )

            Assert.AreEqual(otherCount, afterExpiry |> List.filter (fun entry -> entry.UserId = ownerB) |> List.length)

            match reloaded.DeleteOwnerAsync ownerA |> _.Result with
            | Error failure -> Assert.Fail(failure.Message)
            | Ok deleted -> Assert.AreEqual(retainedCount, deleted)

            let reloadedAgain = make ()
            let afterOwnerDeletion = reloadedAgain.GetAllAsync() |> _.Result

            Assert.AreEqual(
                0,
                afterOwnerDeletion
                |> List.filter (fun entry -> entry.UserId = ownerA)
                |> List.length
            )

            Assert.AreEqual(
                otherCount,
                afterOwnerDeletion
                |> List.filter (fun entry -> entry.UserId = ownerB)
                |> List.length
            )

            match reloadedAgain.DeleteOwnerAsync " " |> _.Result with
            | Ok _ -> Assert.Fail("Blank feedback owner unexpectedly accepted.")
            | Error failure -> Assert.AreEqual(PlatformErrorCategory.InvalidInput, failure.Category)

[<TestClass>]
type FeedbackServiceTests() =

    [<TestMethod>]
    member _.``Submitting feedback stores it for the turn``() =
        (task {
            let svc = inMemory ()

            let turn =
                { TurnRecord.Empty with
                    TurnId = "t1"
                    SessionId = "s1"
                    ToolCalls =
                        [ { Name = "search"
                            Input = "q"
                            Output = "r" } ] }

            do! svc.RecordTurnAsync turn

            let feedback =
                { Id = Guid.NewGuid()
                  TurnId = "t1"
                  SessionId = "s1"
                  UserId = "u1"
                  Sentiment = FeedbackSentiment.Negative
                  Comment = Some "be concise"
                  CreatedAt = DateTimeOffset.UtcNow
                  Metadata = Map.empty }

            do! svc.SubmitFeedbackAsync feedback
        })
            .GetAwaiter()
            .GetResult()

    [<TestMethod>]
    member _.``SubmitFeedback records feedback even for an unknown turn``() =
        (task {
            let svc = inMemory ()

            let feedback =
                { Id = Guid.NewGuid()
                  TurnId = "missing"
                  SessionId = "s1"
                  UserId = "u1"
                  Sentiment = FeedbackSentiment.Negative
                  Comment = None
                  CreatedAt = DateTimeOffset.UtcNow
                  Metadata = Map.empty }

            do! svc.SubmitFeedbackAsync feedback
        })
            .GetAwaiter()
            .GetResult()
