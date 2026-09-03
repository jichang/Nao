namespace Nao.Events.Tests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

module private ObservabilityTestHelpers =
    /// Consumer that records the observability events it receives.
    let obsRecordingConsumer () =
        let received = ResizeArray<NaoEvent>()
        let signals () =
            received
            |> Seq.choose (function
                | ObservabilityCaptured(_, s) -> Some s
                | _ -> None)
            |> List.ofSeq
        let consumer = EventConsumer.create (fun evt ->
                received.Add evt
                Task.CompletedTask)
        consumer, received, signals

open ObservabilityTestHelpers

[<TestClass>]
type ObservabilityServicesTests() =

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
        let bus = InMemoryEventBus.create ()
        let observability = ObservabilityServices.create bus (backingFactory root)

        let services = observability.ServicesFor "dev/s1" ""
        services.Metrics.Value.RecordLlmCall 10 20 5L

        // The write still reaches the real backing store (reads stay correct).
        let expected = Path.Combine(root, "dev_s1", "observability", "metrics.jsonl")
        Assert.IsTrue(File.Exists expected, sprintf "expected metrics file at %s" expected)

    [<TestMethod>]
    member _.PublishesObservabilityEvent() =
        let root = tempDir ()
        let bus = InMemoryEventBus.create ()
        let consumer, _, signals = obsRecordingConsumer ()
        EventBus.subscribe consumer bus
        let observability = ObservabilityServices.create bus (backingFactory root)

        let services = observability.ServicesFor "dev/s1" ""
        services.Metrics.Value.RecordLlmCall 10 20 5L

        // The write is teed to the bus as an ObservabilityCaptured event.
        match signals () with
        | [ LlmCallRecorded(i, o, l) ] ->
            Assert.AreEqual(10, i)
            Assert.AreEqual(20, o)
            Assert.AreEqual(5L, l)
        | other -> Assert.Fail(sprintf "expected one LlmCallRecorded signal, got %A" other)

    [<TestMethod>]
    member _.StampsScopeWithSessionKey() =
        let root = tempDir ()
        let bus = InMemoryEventBus.create ()
        let consumer, received, _ = obsRecordingConsumer ()
        EventBus.subscribe consumer bus
        let observability = ObservabilityServices.create bus (backingFactory root)

        (observability.ServicesFor "dev/s1" "").Metrics.Value.RecordToolCall "search" 3L true

        match List.ofSeq received with
        | [ ObservabilityCaptured(scope, ToolCallRecorded("search", 3L, true)) ] ->
            Assert.AreEqual("dev/s1", scope.SessionKey)
            Assert.AreEqual("dev", scope.UserId)
            Assert.AreEqual("s1", scope.SessionId)
        | other -> Assert.Fail(sprintf "unexpected events %A" other)

    [<TestMethod>]
    member _.SeparatesDistinctSessions() =
        let root = tempDir ()
        let bus = InMemoryEventBus.create ()
        let observability = ObservabilityServices.create bus (backingFactory root)

        (observability.ServicesFor "dev/s1" "").Metrics.Value.RecordLlmCall 1 1 1L
        (observability.ServicesFor "dev/s2" "").Metrics.Value.RecordLlmCall 1 1 1L

        Assert.IsTrue(File.Exists(Path.Combine(root, "dev_s1", "observability", "metrics.jsonl")))
        Assert.IsTrue(File.Exists(Path.Combine(root, "dev_s2", "observability", "metrics.jsonl")))

    [<TestMethod>]
    member _.PreservesNoneSinks() =
        let bus = InMemoryEventBus.create ()
        let observability = ObservabilityServices.create bus (fun _ -> HarnessServices.none)

        let services = observability.ServicesFor "dev/s1" ""

        // A backing bundle with no sinks stays empty after wrapping (nothing to tee).
        Assert.IsTrue(services.Metrics.IsNone)
        Assert.IsTrue(services.Tracer.IsNone)
        Assert.IsTrue(services.ExecutionJournal.IsNone)
        Assert.IsTrue(services.TraceStore.IsNone)
        Assert.IsTrue(services.AuditLog.IsNone)