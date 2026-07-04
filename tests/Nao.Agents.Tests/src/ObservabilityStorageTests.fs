namespace Nao.Events.Tests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence
open Nao.Agents

/// Consumer that records the observability events it receives.
type private ObsRecordingConsumer() =
    let received = ResizeArray<NaoEvent>()
    member _.Received = received
    member _.Signals =
        received
        |> Seq.choose (function
            | ObservabilityCaptured(_, s) -> Some s
            | _ -> None)
        |> List.ofSeq
    interface IEventConsumer with
        member _.HandleAsync(evt) =
            received.Add evt
            Task.CompletedTask

[<TestClass>]
type ObservabilityStorageTests() =

    let tempDir () =
        let dir = Path.Combine(Path.GetTempPath(), "nao-obs-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        dir

    /// Backing factory that roots each session's observability under root/<key>/observability/.
    let backingFactory (root: string) =
        fun (key: string) ->
            Persistence.harnessServices (PersistenceMode.File(Path.Combine(root, key.Replace("/", "_"), "observability")))

    [<TestMethod>]
    member _.WritesMetricsToPerSessionFolder() =
        let root = tempDir ()
        let bus = InMemoryEventBus() :> IEventBus
        let observability = ObservabilityServices(bus, backingFactory root)

        let services = observability.ServicesFor("dev/s1", "")
        services.Metrics.Value.RecordLlmCall 10 20 5L

        // The write still reaches the real backing store (reads stay correct).
        let expected = Path.Combine(root, "dev_s1", "observability", "metrics.jsonl")
        Assert.IsTrue(File.Exists expected, sprintf "expected metrics file at %s" expected)

    [<TestMethod>]
    member _.PublishesObservabilityEvent() =
        let root = tempDir ()
        let bus = InMemoryEventBus() :> IEventBus
        let consumer = ObsRecordingConsumer()
        bus.Subscribe(consumer :> IEventConsumer)
        let observability = ObservabilityServices(bus, backingFactory root)

        let services = observability.ServicesFor("dev/s1", "")
        services.Metrics.Value.RecordLlmCall 10 20 5L

        // The write is teed to the bus as an ObservabilityCaptured event.
        match consumer.Signals with
        | [ LlmCallRecorded(i, o, l) ] ->
            Assert.AreEqual(10, i)
            Assert.AreEqual(20, o)
            Assert.AreEqual(5L, l)
        | other -> Assert.Fail(sprintf "expected one LlmCallRecorded signal, got %A" other)

    [<TestMethod>]
    member _.StampsScopeWithSessionKey() =
        let root = tempDir ()
        let bus = InMemoryEventBus() :> IEventBus
        let consumer = ObsRecordingConsumer()
        bus.Subscribe(consumer :> IEventConsumer)
        let observability = ObservabilityServices(bus, backingFactory root)

        (observability.ServicesFor("dev/s1", "")).Metrics.Value.RecordToolCall "search" 3L true

        match List.ofSeq consumer.Received with
        | [ ObservabilityCaptured(scope, ToolCallRecorded("search", 3L, true)) ] ->
            Assert.AreEqual("dev/s1", scope.SessionKey)
            Assert.AreEqual("dev", scope.UserId)
            Assert.AreEqual("s1", scope.SessionId)
        | other -> Assert.Fail(sprintf "unexpected events %A" other)

    [<TestMethod>]
    member _.SeparatesDistinctSessions() =
        let root = tempDir ()
        let bus = InMemoryEventBus() :> IEventBus
        let observability = ObservabilityServices(bus, backingFactory root)

        (observability.ServicesFor("dev/s1", "")).Metrics.Value.RecordLlmCall 1 1 1L
        (observability.ServicesFor("dev/s2", "")).Metrics.Value.RecordLlmCall 1 1 1L

        Assert.IsTrue(File.Exists(Path.Combine(root, "dev_s1", "observability", "metrics.jsonl")))
        Assert.IsTrue(File.Exists(Path.Combine(root, "dev_s2", "observability", "metrics.jsonl")))

    [<TestMethod>]
    member _.PreservesNoneSinks() =
        let bus = InMemoryEventBus() :> IEventBus
        let observability = ObservabilityServices(bus, fun _ -> HarnessServices.none)

        let services = observability.ServicesFor("dev/s1", "")

        // A backing bundle with no sinks stays empty after wrapping (nothing to tee).
        Assert.IsTrue(services.Metrics.IsNone)
        Assert.IsTrue(services.Tracer.IsNone)
        Assert.IsTrue(services.ExecutionJournal.IsNone)
        Assert.IsTrue(services.TraceStore.IsNone)
        Assert.IsTrue(services.AuditLog.IsNone)
