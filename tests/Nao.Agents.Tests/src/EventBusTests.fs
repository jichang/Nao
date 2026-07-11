namespace Nao.Events.Tests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence
open Nao.Agents

/// Test consumer that records the events it receives (and can optionally throw).
type private RecordingConsumer(?fail: bool) =
    let received = ResizeArray<NaoEvent>()
    let shouldFail = defaultArg fail false
    member _.Received = received
    interface IEventConsumer with
        member _.HandleAsync(evt) =
            if shouldFail then failwith "boom"
            received.Add evt
            Task.CompletedTask

[<TestClass>]
type EventBusTests() =

    let scope () =
        EventScope.Create(
            userId = "dev",
            sessionId = "s1",
            conversationId = "c1",
            workspaceKey = "ws",
            actionId = "turn-1",
            sessionKey = "dev/s1")

    let sampleTurn () =
        { TurnRecord.Empty with
            TurnId = "turn-1"
            SessionId = "s1"
            UserId = "dev"
            Input = "hello"
            Output = "hi"
            CreatedAt = DateTimeOffset.UtcNow }

    let tempDir () =
        let dir = Path.Combine(Path.GetTempPath(), "nao-events-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        dir

    [<TestMethod>]
    member _.PublishAsync_FansOutToAllConsumers() =
        let bus = InMemoryEventBus() :> IEventBus
        let a = RecordingConsumer()
        let b = RecordingConsumer()
        bus.Subscribe(a :> IEventConsumer)
        bus.Subscribe(b :> IEventConsumer)

        bus.PublishAsync(TurnCompleted(scope (), sampleTurn ())).Wait()

        Assert.AreEqual(1, a.Received.Count)
        Assert.AreEqual(1, b.Received.Count)

    [<TestMethod>]
    member _.PublishAsync_IsolatesAFailingConsumer() =
        let bus = InMemoryEventBus() :> IEventBus
        let failing = RecordingConsumer(fail = true)
        let healthy = RecordingConsumer()
        bus.Subscribe(failing :> IEventConsumer)
        bus.Subscribe(healthy :> IEventConsumer)

        // Must not throw even though one consumer fails...
        bus.PublishAsync(TurnCompleted(scope (), sampleTurn ())).Wait()

        // ...and the healthy consumer still receives the event.
        Assert.AreEqual(1, healthy.Received.Count)

    [<TestMethod>]
    member _.RoutesTurnToPerSessionFolder() =
        let root = tempDir ()
        let consumer = FeedbackEventConsumer(fun key -> Path.Combine(root, key.Replace("/", "_"), "feedback"))
        let evt = TurnCompleted(scope (), sampleTurn ())

        (consumer :> IEventConsumer).HandleAsync(evt).Wait()

        let expected = Path.Combine(root, "dev_s1", "feedback", "turns.jsonl")
        Assert.IsTrue(File.Exists expected, sprintf "expected turns file at %s" expected)

    [<TestMethod>]
    member _.SeparatesDistinctSessions() =
        let root = tempDir ()
        let consumer = FeedbackEventConsumer(fun key -> Path.Combine(root, key.Replace("/", "_"), "feedback"))
        let scopeA =
            EventScope.Create("dev", "s1", "c1", "ws", "turn-a", "dev/s1")
        let scopeB =
            EventScope.Create("dev", "s2", "c1", "ws", "turn-b", "dev/s2")

        (consumer :> IEventConsumer).HandleAsync(TurnCompleted(scopeA, sampleTurn ())).Wait()
        (consumer :> IEventConsumer).HandleAsync(TurnCompleted(scopeB, sampleTurn ())).Wait()

        Assert.IsTrue(File.Exists(Path.Combine(root, "dev_s1", "feedback", "turns.jsonl")))
        Assert.IsTrue(File.Exists(Path.Combine(root, "dev_s2", "feedback", "turns.jsonl")))

    [<TestMethod>]
    member _.FeedbackFor_ReturnsServiceForReads() =
        let root = tempDir ()
        let consumer = FeedbackEventConsumer(fun key -> Path.Combine(root, key.Replace("/", "_"), "feedback"))

        let svc = consumer.FeedbackFor "dev/s1"

        Assert.IsNotNull(box svc)
