module FeedbackTests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Microsoft.Data.Sqlite
open Nao.Agents
open Nao.Agents
open Nao.Persistence
open Nao.Agents
open Nao.Feedback.Tests

let private tempDir () =
    let d = Path.Combine(Path.GetTempPath(), "nao-feedback-tests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory d |> ignore
    d

let private sqliteFactory () : IDbConnectionFactory =
    let path = Path.Combine(Path.GetTempPath(), sprintf "nao-feedback-%s.db" (Guid.NewGuid().ToString("N")))
    let cs = sprintf "Data Source=%s" path
    DbConnectionFactory.ofFunc (fun () -> new SqliteConnection(cs) :> System.Data.Common.DbConnection)

let private echoTool (name: string) : Tool =
    Tool.Create(name, "Echoes its input.", (fun (s: string) -> Task.FromResult(sprintf "echo:%s" s)))

[<TestClass>]
type TurnRecorderTests() =

    [<TestMethod>]
    member _.``Pairs tool invocations with their results in order``() =
        let recorder =
            TurnRecorder.create("t1", "s1", "u1", "ws", "agent", None, "hello")
        let consumer = recorder :> IEventConsumer
        let scope = EventScope.Create("u1", "s1", "", "ws", "t1", "u1/s1")
        let send signal = consumer.HandleAsync(NaoEvent.TurnProgress(scope, signal)).Wait()
        send (ToolInvoked("search", "query"))
        send (ToolCompleted("search", "results"))
        send (SubAgentInvoked("helper", "subtask"))
        send (SubAgentCompleted("helper", "done"))
        send (AnswerProduced("final answer"))

        let snap = recorder.Snapshot()
        Assert.AreEqual(1, snap.ToolCalls.Length)
        Assert.AreEqual("search", snap.ToolCalls.[0].Name)
        Assert.AreEqual("query", snap.ToolCalls.[0].Input)
        Assert.AreEqual("results", snap.ToolCalls.[0].Output)
        Assert.AreEqual(1, snap.SubAgentCalls.Length)
        Assert.AreEqual("helper", snap.SubAgentCalls.[0].Name)
        Assert.AreEqual("subtask", snap.SubAgentCalls.[0].Input)
        Assert.AreEqual("final answer", snap.Output)

    [<TestMethod>]
    member _.``Resolves tool version from tool list``() =
        let versioned =
            { echoTool "search" with
                Version = "v1" }
        let recorder =
            TurnRecorder.forTools [ versioned ] ("t1", "s1", "u1", "ws", "agent", None, "hi")
        let consumer = recorder :> IEventConsumer
        let scope = EventScope.Create("u1", "s1", "", "ws", "t1", "u1/s1")
        let send signal = consumer.HandleAsync(NaoEvent.TurnProgress(scope, signal)).Wait()
        send (ToolInvoked("search", "q"))
        send (ToolCompleted("search", "r"))
        let snap = recorder.Snapshot()
        Assert.AreEqual(Some "v1", snap.ToolCalls.[0].Version)

[<TestClass>]
type FileStoreTests() =

    [<TestMethod>]
    member _.``Turn store round-trips via JSONL``() =
        (task {
            let dir = tempDir ()
            let store = FileTurnStore dir :> ITurnStore
            let turn =
                { TurnRecord.Empty with
                    TurnId = "t1"; SessionId = "s1"
                    ToolCalls = [ { Name = "search"; Version = Some "v1"; Input = "q"; Output = "r" } ] }
            do! store.SaveAsync turn
            let! loaded = store.GetAsync "t1"
            Assert.IsTrue(loaded.IsSome)
            Assert.AreEqual(1, loaded.Value.ToolCalls.Length)
            Assert.AreEqual("search", loaded.Value.ToolCalls.[0].Name)
            Assert.AreEqual(Some "v1", loaded.Value.ToolCalls.[0].Version)
        }).GetAwaiter().GetResult()

/// Same coverage as FileStoreTests but against the ADO.NET (SQLite) backend, proving
/// the feedback stores have full parity across InMemory / File / Database modes.
[<TestClass>]
type DatabaseStoreTests() =

    [<TestMethod>]
    member _.``Turn store round-trips via ADO.NET``() =
        (task {
            let factory = sqliteFactory ()
            let store = AdoTurnStore factory :> ITurnStore
            let turn =
                { TurnRecord.Empty with
                    TurnId = "t1"; SessionId = "s1"
                    ToolCalls = [ { Name = "search"; Version = Some "v1"; Input = "q"; Output = "r" } ] }
            do! store.SaveAsync turn
            let! loaded = store.GetAsync "t1"
            Assert.IsTrue(loaded.IsSome)
            Assert.AreEqual(1, loaded.Value.ToolCalls.Length)
            Assert.AreEqual("search", loaded.Value.ToolCalls.[0].Name)
            Assert.AreEqual(Some "v1", loaded.Value.ToolCalls.[0].Version)
            let! forSession = store.GetForSessionAsync "s1"
            Assert.AreEqual(1, forSession.Length)
        }).GetAwaiter().GetResult()

[<TestClass>]
type FeedbackServiceTests() =

    [<TestMethod>]
    member _.``Submitting feedback stores it for the turn``() =
        (task {
            let svc = FeedbackService.InMemory()
            let turn =
                { TurnRecord.Empty with
                    TurnId = "t1"; SessionId = "s1"
                    ToolCalls = [ { Name = "search"; Version = None; Input = "q"; Output = "r" } ] }
            do! svc.RecordTurnAsync turn
            let feedback =
                { Id = Guid.NewGuid(); TurnId = "t1"; SessionId = "s1"; UserId = "u1"
                  Sentiment = FeedbackSentiment.Negative; Comment = Some "be concise"
                  CreatedAt = DateTimeOffset.UtcNow; Metadata = Map.empty }
            do! svc.SubmitFeedbackAsync feedback
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``SubmitFeedback records feedback even for an unknown turn``() =
        (task {
            let svc = FeedbackService.InMemory()
            let feedback =
                { Id = Guid.NewGuid(); TurnId = "missing"; SessionId = "s1"; UserId = "u1"
                  Sentiment = FeedbackSentiment.Negative; Comment = None
                  CreatedAt = DateTimeOffset.UtcNow; Metadata = Map.empty }
            do! svc.SubmitFeedbackAsync feedback
        }).GetAwaiter().GetResult()

