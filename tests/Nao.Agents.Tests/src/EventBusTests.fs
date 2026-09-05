namespace Nao.Events.Tests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence
open Nao.Agents

module private EventBusTestHelpers =
    /// Test consumer that records the events it receives (and can optionally throw).
    let recordingConsumer shouldFail =
        let received = ResizeArray<NaoEvent>()

        let consumer =
            EventConsumer.create (fun evt ->
                if shouldFail then
                    failwith "boom"

                received.Add evt
                Task.CompletedTask)

        consumer, received

open EventBusTestHelpers

[<TestClass>]
type EventBusTests() =

    let scope () =
        EventScope.Create(
            userId = "dev",
            sessionId = "s1",
            conversationId = "c1",
            workspaceKey = "ws",
            actionId = "turn-1",
            sessionKey = "dev/s1",
            correlation = CorrelationContext.root ()
        )

    let sampleTurn () =
        { TurnRecord.empty (CorrelationContext.root ()) with
            TurnId = "turn-1"
            SessionId = "s1"
            UserId = "dev"
            Input = "hello"
            Output = "hi"
            CreatedAt = DateTimeOffset.UtcNow }

    let tempDir () =
        let dir =
            Path.Combine(Path.GetTempPath(), "nao-events-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory dir |> ignore
        dir

    [<TestMethod>]
    member _.PublishAsync_FansOutToAllConsumers() =
        let bus = InMemoryEventBus.create ()
        let a, aReceived = recordingConsumer false
        let b, bReceived = recordingConsumer false
        EventBus.subscribe a bus
        EventBus.subscribe b bus

        EventBus.publishAsync (TurnCompleted(scope (), sampleTurn ())) bus |> _.Wait()

        Assert.AreEqual(1, aReceived.Count)
        Assert.AreEqual(1, bReceived.Count)

    [<TestMethod>]
    member _.PublishAsync_IsolatesAFailingConsumer() =
        let bus = InMemoryEventBus.create ()
        let failing, _ = recordingConsumer true
        let healthy, healthyReceived = recordingConsumer false
        EventBus.subscribe failing bus
        EventBus.subscribe healthy bus

        // Must not throw even though one consumer fails...
        EventBus.publishAsync (TurnCompleted(scope (), sampleTurn ())) bus |> _.Wait()

        // ...and the healthy consumer still receives the event.
        Assert.AreEqual(1, healthyReceived.Count)

    [<TestMethod>]
    member _.DuplicateSubscriptionsAndFirstMatchUnsubscribeArePreserved() =
        let bus = InMemoryEventBus.create ()
        let consumer, received = recordingConsumer false
        EventBus.subscribe consumer bus
        EventBus.subscribe consumer bus

        EventBus.unsubscribe consumer bus
        EventBus.publishAsync (TurnCompleted(scope (), sampleTurn ())) bus |> _.Wait()

        Assert.AreEqual(1, received.Count)

    [<TestMethod>]
    member _.UnsubscribeUsesConsumerIdentity() =
        let bus = InMemoryEventBus.create ()
        let received = ResizeArray<NaoEvent>()

        let handle evt =
            received.Add evt
            Task.CompletedTask

        let first = EventConsumer.create handle
        let second = EventConsumer.create handle
        EventBus.subscribe first bus
        EventBus.subscribe second bus

        EventBus.unsubscribe first bus
        EventBus.publishAsync (TurnCompleted(scope (), sampleTurn ())) bus |> _.Wait()

        Assert.AreEqual(1, received.Count)

    [<TestMethod>]
    member _.PublishUsesSubscriptionSnapshot() =
        let bus = InMemoryEventBus.create ()
        let late, received = recordingConsumer false

        let subscribing =
            EventConsumer.create (fun _ ->
                EventBus.subscribe late bus
                Task.CompletedTask)

        EventBus.subscribe subscribing bus

        EventBus.publishAsync (TurnCompleted(scope (), sampleTurn ())) bus |> _.Wait()
        Assert.AreEqual(0, received.Count)
        EventBus.publishAsync (TurnCompleted(scope (), sampleTurn ())) bus |> _.Wait()
        Assert.AreEqual(1, received.Count)

    [<TestMethod>]
    member _.PublishAwaitsConsumersSequentially() =
        let bus = InMemoryEventBus.create ()
        let order = ResizeArray<string>()

        let release =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let first =
            EventConsumer.create (fun _ ->
                task {
                    order.Add "first-start"
                    do! release.Task
                    order.Add "first-end"
                }
                :> Task)

        let second =
            EventConsumer.create (fun _ ->
                order.Add "second"
                Task.CompletedTask)

        EventBus.subscribe first bus
        EventBus.subscribe second bus

        let publishing = EventBus.publishAsync (TurnCompleted(scope (), sampleTurn ())) bus
        CollectionAssert.AreEqual([| "first-start" |], order.ToArray())
        Assert.IsFalse(publishing.IsCompleted)

        release.SetResult()
        publishing.Wait()
        CollectionAssert.AreEqual([| "first-start"; "first-end"; "second" |], order.ToArray())

    [<TestMethod>]
    member _.RoutesTurnToPerSessionFolder() =
        let root = tempDir ()

        let consumer =
            FeedbackEventConsumer.create (fun key -> Path.Combine(root, key.Replace("/", "_"), "feedback"))

        let evt = TurnCompleted(scope (), sampleTurn ())

        EventConsumer.handleAsync evt consumer.Consumer |> _.Wait()

        let expected = Path.Combine(root, "dev_s1", "feedback", "turns.jsonl")
        Assert.IsTrue(File.Exists expected, sprintf "expected turns file at %s" expected)

    [<TestMethod>]
    member _.SeparatesDistinctSessions() =
        let root = tempDir ()

        let consumer =
            FeedbackEventConsumer.create (fun key -> Path.Combine(root, key.Replace("/", "_"), "feedback"))

        let scopeA =
            EventScope.Create("dev", "s1", "c1", "ws", "turn-a", "dev/s1", CorrelationContext.root ())

        let scopeB =
            EventScope.Create("dev", "s2", "c1", "ws", "turn-b", "dev/s2", CorrelationContext.root ())

        EventConsumer.handleAsync (TurnCompleted(scopeA, sampleTurn ())) consumer.Consumer
        |> _.Wait()

        EventConsumer.handleAsync (TurnCompleted(scopeB, sampleTurn ())) consumer.Consumer
        |> _.Wait()

        Assert.IsTrue(File.Exists(Path.Combine(root, "dev_s1", "feedback", "turns.jsonl")))
        Assert.IsTrue(File.Exists(Path.Combine(root, "dev_s2", "feedback", "turns.jsonl")))

    [<TestMethod>]
    member _.FeedbackFor_ReturnsServiceForReads() =
        let root = tempDir ()

        let consumer =
            FeedbackEventConsumer.create (fun key -> Path.Combine(root, key.Replace("/", "_"), "feedback"))

        let svc = FeedbackEventConsumer.feedbackFor "dev/s1" consumer

        Assert.IsNotNull(box svc)
