namespace Nao.Runtime.Orleans.Tests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence
open Nao.Runtime.Orleans

/// Records the events published to the bus.
type private ConvRecordingConsumer() =
    let received = ResizeArray<NaoEvent>()
    member _.Received = received
    member _.Signals =
        received
        |> Seq.choose (function
            | ConversationCaptured(_, s) -> Some s
            | _ -> None)
        |> List.ofSeq
    interface IEventConsumer with
        member _.HandleAsync(evt) =
            received.Add evt
            Task.CompletedTask

[<TestClass>]
type PublishingConversationStoreTests() =

    let newRoot () =
        let dir = Path.Combine(Path.GetTempPath(), "nao-pubconv-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        dir

    let cleanup (dir: string) =
        if Directory.Exists dir then Directory.Delete(dir, true)

    let message role content turnId : PersistedMessage =
        { Role = role
          Content = content
          Timestamp = DateTimeOffset.UtcNow
          TurnId = turnId
          Steps = [||]
          Attachments = [||] }

    /// Build a tee over a real FileConversationStore + a subscribed recorder.
    let setup (root: string) =
        let bus = InMemoryEventBus() :> IEventBus
        let recorder = ConvRecordingConsumer()
        bus.Subscribe(recorder :> IEventConsumer)
        let store = PublishingConversationStore(bus, FileConversationStore(root)) :> IConversationStore
        store, recorder

    [<TestMethod>]
    member _.Append_WritesToBackingAndPublishes() =
        let root = newRoot ()
        try
            let store, recorder = setup root
            store.AppendAsync "dev/s1" "default" [| message "User" "hi" "t1" |] |> fun t -> t.Wait()

            // Backing still persisted (reads stay correct).
            let loaded = (store.LoadAsync "dev/s1" "default").Result
            Assert.AreEqual(1, loaded.Length)
            Assert.AreEqual("hi", loaded.[0].Content)

            // ...and the write was teed to the bus.
            match recorder.Signals with
            | [ MessagesAppended("default", msgs) ] ->
                Assert.AreEqual(1, msgs.Length)
                Assert.AreEqual("hi", msgs.[0].Content)
                Assert.AreEqual("User", msgs.[0].Role)
            | other -> Assert.Fail(sprintf "expected one MessagesAppended, got %A" other)
        finally
            cleanup root

    [<TestMethod>]
    member _.Append_StampsScopeFromKeyAndTurn() =
        let root = newRoot ()
        try
            let store, recorder = setup root
            store.AppendAsync "dev/s1" "chat-7" [| message "Assistant" "yo" "turn-9" |] |> fun t -> t.Wait()

            match List.ofSeq recorder.Received with
            | [ ConversationCaptured(scope, _) ] ->
                Assert.AreEqual("dev", scope.UserId)
                Assert.AreEqual("s1", scope.SessionId)
                Assert.AreEqual("chat-7", scope.ConversationId)
                Assert.AreEqual("turn-9", scope.ActionId)
                Assert.AreEqual("dev/s1", scope.SessionKey)
            | other -> Assert.Fail(sprintf "unexpected events %A" other)
        finally
            cleanup root

    [<TestMethod>]
    member _.EmptyAppend_PublishesNothing() =
        let root = newRoot ()
        try
            let store, recorder = setup root
            store.AppendAsync "dev/s1" "default" [||] |> fun t -> t.Wait()
            Assert.AreEqual(0, recorder.Received.Count)
        finally
            cleanup root

    [<TestMethod>]
    member _.Save_PublishesConversationSaved() =
        let root = newRoot ()
        try
            let store, recorder = setup root
            store.SaveAsync "dev/s1" "default" [| message "User" "a" "t1"; message "Assistant" "b" "t1" |]
            |> fun t -> t.Wait()

            match recorder.Signals with
            | [ ConversationSaved("default", msgs) ] -> Assert.AreEqual(2, msgs.Length)
            | other -> Assert.Fail(sprintf "expected ConversationSaved, got %A" other)
        finally
            cleanup root

    [<TestMethod>]
    member _.DeleteConversation_PublishesConversationDeleted() =
        let root = newRoot ()
        try
            let store, recorder = setup root
            store.AppendAsync "dev/s1" "default" [| message "User" "hi" "t1" |] |> fun t -> t.Wait()
            store.DeleteConversationAsync "dev/s1" "default" |> fun t -> t.Wait()

            Assert.IsTrue(recorder.Signals |> List.contains (ConversationDeleted "default"))
        finally
            cleanup root

    [<TestMethod>]
    member _.DeleteSession_PublishesSessionConversationsDeleted() =
        let root = newRoot ()
        try
            let store, recorder = setup root
            store.AppendAsync "dev/s1" "default" [| message "User" "hi" "t1" |] |> fun t -> t.Wait()
            store.DeleteSessionAsync "dev/s1" |> fun t -> t.Wait()

            Assert.IsTrue(recorder.Signals |> List.contains SessionConversationsDeleted)
        finally
            cleanup root
