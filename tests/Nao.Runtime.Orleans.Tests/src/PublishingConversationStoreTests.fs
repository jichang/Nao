namespace Nao.Runtime.Orleans.Tests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence
open Nao.Runtime.Orleans

/// Records the events published to the bus.
type private ConvRecordingConsumer =
    { Consumer: EventConsumer
      Received: ResizeArray<NaoEvent>
      Signals: unit -> ConversationSignal list }

module private PublishingConversationStoreTestHelpers =
    let convRecordingConsumer () =
        let received = ResizeArray<NaoEvent>()

        let signals () =
            received
            |> Seq.choose (function
                | ConversationCaptured(_, s) -> Some s
                | _ -> None)
            |> List.ofSeq

        let consumer =
            EventConsumer.create (fun evt ->
                received.Add evt
                Task.CompletedTask)

        { Consumer = consumer
          Received = received
          Signals = signals }

open PublishingConversationStoreTestHelpers

[<TestClass>]
type PublishingConversationStoreTests() =

    let newRoot () =
        let dir =
            Path.Combine(Path.GetTempPath(), "nao-pubconv-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory dir |> ignore
        dir

    let cleanup (dir: string) =
        if Directory.Exists dir then
            Directory.Delete(dir, true)

    let messageWith correlation role content turnId : PersistedMessage =
        { Role = role
          Content = content
          Timestamp = DateTimeOffset.UtcNow
          Correlation = correlation
          TurnId = turnId
          Steps = [||]
          Attachments = [||]
          Artifacts = [||] }

    let message role content turnId =
        messageWith (CorrelationContext.root ()) role content turnId

    /// Build a tee over a real FileConversationStore + a subscribed recorder.
    let setup (root: string) =
        let bus = InMemoryEventBus.create ()
        let recorder = convRecordingConsumer ()
        EventBus.subscribe recorder.Consumer bus

        let store =
            FileConversationStore.create root |> PublishingConversationStore.create bus

        store, recorder

    [<TestMethod>]
    member _.Append_WritesToBackingAndPublishes() =
        let root = newRoot ()

        try
            let store, recorder = setup root
            let correlation = CorrelationContext.root ()
            let artifactCount = Random.Shared.Next(1, 5)

            let artifacts =
                [| for index in 1..artifactCount ->
                       let artifact = ArtifactRecord()
                       artifact.Id <- ArtifactId.generate () |> ArtifactId.serialize
                       artifact.Kind <- sprintf "artifact-%d" index
                       artifact.ContentType <- "application/json"
                       artifact.Payload <- sprintf "{\"index\":%d}" index
                       artifact |]

            let input =
                { messageWith correlation "User" "hi" "t1" with
                    Artifacts = artifacts }

            store.AppendAsync "dev/s1" "default" [| input |] |> fun t -> t.Wait()

            // Backing still persisted (reads stay correct).
            let loaded = (store.LoadAsync "dev/s1" "default").Result
            Assert.AreEqual(1, loaded.Length)
            Assert.AreEqual("hi", loaded.[0].Content)
            Assert.AreEqual(correlation, loaded.[0].Correlation)
            Assert.AreEqual(artifacts.Length, loaded.[0].Artifacts.Length)

            for expected, actual in Array.zip artifacts loaded.[0].Artifacts do
                Assert.AreEqual(expected.Id, actual.Id)
                Assert.AreEqual(expected.Kind, actual.Kind)
                Assert.AreEqual(expected.ContentType, actual.ContentType)
                Assert.AreEqual(expected.Payload, actual.Payload)

            let executionMessages =
                store.LoadByExecutionAsync correlation.ExecutionId |> _.Result

            Assert.AreEqual(1, executionMessages.Length)
            Assert.AreEqual(correlation, executionMessages.[0].Correlation)

            let messagesPath =
                Directory.GetFiles(root, "messages.json", SearchOption.AllDirectories)
                |> Array.exactlyOne

            let persistedJson = File.ReadAllText messagesPath
            Assert.IsFalse(persistedJson.Contains('\n'), "Persisted conversation JSON must remain compact")
            Assert.IsFalse(persistedJson.Contains('\r'), "Persisted conversation JSON must remain compact")

            use persisted = JsonDocument.Parse persistedJson
            Assert.AreEqual(1, persisted.RootElement.GetProperty("schemaVersion").GetInt32())
            Assert.AreEqual(1, persisted.RootElement.GetProperty("value").GetArrayLength())

            Assert.AreEqual(
                artifacts.Length,
                persisted.RootElement.GetProperty("value").[0].GetProperty("Artifacts").GetArrayLength()
            )

            // ...and the write was teed to the bus.
            match recorder.Signals() with
            | [ MessagesAppended("default", msgs) ] ->
                Assert.AreEqual(1, msgs.Length)
                Assert.AreEqual("hi", msgs.[0].Content)
                Assert.AreEqual("User", msgs.[0].Role)
                Assert.AreEqual(correlation, msgs.[0].Correlation)
                Assert.AreEqual(artifacts.Length, msgs.[0].Artifacts.Length)

                for expected, actual in List.zip (List.ofArray artifacts) msgs.[0].Artifacts do
                    Assert.AreEqual(expected.Id, ArtifactId.serialize actual.Id)
                    Assert.AreEqual(expected.Kind, actual.Kind)
                    Assert.AreEqual(expected.ContentType, actual.ContentType)
                    Assert.AreEqual(expected.Payload, actual.Payload)
            | other -> Assert.Fail(sprintf "expected one MessagesAppended, got %A" other)
        finally
            cleanup root

    [<TestMethod>]
    member _.Append_RejectsCorruptMessagesWithoutMutationOrPublication() =
        let root = newRoot ()

        try
            let store, recorder = setup root

            store.AppendAsync "dev/s1" "default" [| message "User" "original" "t1" |]
            |> _.GetAwaiter().GetResult()

            let messagesPath =
                Directory.GetFiles(root, "messages.json", SearchOption.AllDirectories)
                |> Array.exactlyOne

            let metaPath =
                Directory.GetFiles(root, "meta.json", SearchOption.AllDirectories)
                |> Array.exactlyOne

            let indexPath =
                Directory.GetFiles(root, "conversations.json", SearchOption.AllDirectories)
                |> Array.exactlyOne

            File.WriteAllText(messagesPath, "{invalid")

            let messagesBefore = File.ReadAllBytes messagesPath
            let metaBefore = File.ReadAllBytes metaPath
            let indexBefore = File.ReadAllBytes indexPath
            let eventsBefore = recorder.Received.Count

            let error =
                Assert.ThrowsExactly<InvalidDataException>(fun () ->
                    store.AppendAsync "dev/s1" "default" [| message "User" "new" "t2" |]
                    |> _.GetAwaiter().GetResult())

            StringAssert.Contains(error.Message, messagesPath)
            StringAssert.Contains(error.Message, "Restore or remove")
            CollectionAssert.AreEqual(messagesBefore, File.ReadAllBytes messagesPath)
            CollectionAssert.AreEqual(metaBefore, File.ReadAllBytes metaPath)
            CollectionAssert.AreEqual(indexBefore, File.ReadAllBytes indexPath)
            Assert.AreEqual(eventsBefore, recorder.Received.Count)
        finally
            cleanup root

    [<TestMethod>]
    member _.Append_RejectsUnsupportedMessagesWithoutMutationOrPublication() =
        let root = newRoot ()

        try
            let store, recorder = setup root

            store.AppendAsync "dev/s1" "default" [| message "User" "original" "t1" |]
            |> _.Wait()

            let messagesPath =
                Directory.GetFiles(root, "messages.json", SearchOption.AllDirectories)
                |> Array.exactlyOne

            let versionOne = File.ReadAllText messagesPath
            let unsupported = versionOne.Replace("\"schemaVersion\":1", "\"schemaVersion\":2")
            File.WriteAllText(messagesPath, unsupported)
            let before = File.ReadAllBytes messagesPath
            let eventsBefore = recorder.Received.Count

            let error =
                Assert.ThrowsExactly<InvalidDataException>(fun () ->
                    store.AppendAsync "dev/s1" "default" [| message "User" "new" "t2" |]
                    |> _.GetAwaiter().GetResult())

            StringAssert.Contains(error.Message, "unsupported schema version 2")
            CollectionAssert.AreEqual(before, File.ReadAllBytes messagesPath)
            Assert.AreEqual(eventsBefore, recorder.Received.Count)
        finally
            cleanup root

    [<TestMethod>]
    member _.Append_RejectsUnversionedMessagesWithoutMutationOrPublication() =
        let root = newRoot ()

        try
            let store, recorder = setup root

            store.AppendAsync "dev/s1" "default" [| message "User" "original" "t1" |]
            |> _.GetAwaiter().GetResult()

            let messagesPath =
                Directory.GetFiles(root, "messages.json", SearchOption.AllDirectories)
                |> Array.exactlyOne

            let metaPath =
                Directory.GetFiles(root, "meta.json", SearchOption.AllDirectories)
                |> Array.exactlyOne

            let indexPath =
                Directory.GetFiles(root, "conversations.json", SearchOption.AllDirectories)
                |> Array.exactlyOne

            File.WriteAllText(messagesPath, "[]")

            let messagesBefore = File.ReadAllBytes messagesPath
            let metaBefore = File.ReadAllBytes metaPath
            let indexBefore = File.ReadAllBytes indexPath
            let eventsBefore = recorder.Received.Count

            let error =
                Assert.ThrowsExactly<InvalidDataException>(fun () ->
                    store.AppendAsync "dev/s1" "default" [| message "User" "new" "t2" |]
                    |> _.GetAwaiter().GetResult())

            StringAssert.Contains(error.Message, messagesPath)
            StringAssert.Contains(error.Message, "docs/migrations")
            CollectionAssert.AreEqual(messagesBefore, File.ReadAllBytes messagesPath)
            CollectionAssert.AreEqual(metaBefore, File.ReadAllBytes metaPath)
            CollectionAssert.AreEqual(indexBefore, File.ReadAllBytes indexPath)
            Assert.AreEqual(eventsBefore, recorder.Received.Count)
        finally
            cleanup root

    [<TestMethod>]
    member _.Append_StampsScopeFromKeyAndTurn() =
        let root = newRoot ()

        try
            let store, recorder = setup root
            let correlation = CorrelationContext.root ()

            store.AppendAsync "dev/s1" "chat-7" [| messageWith correlation "Assistant" "yo" "turn-9" |]
            |> fun t -> t.Wait()

            match List.ofSeq recorder.Received with
            | [ ConversationCaptured(scope, _) ] ->
                Assert.AreEqual("dev", scope.UserId)
                Assert.AreEqual("s1", scope.SessionId)
                Assert.AreEqual("chat-7", scope.ConversationId)
                Assert.AreEqual("turn-9", scope.ActionId)
                Assert.AreEqual("dev/s1", scope.SessionKey)
                Assert.AreEqual(correlation, scope.Correlation)
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

            match recorder.Signals() with
            | [ ConversationSaved("default", msgs) ] -> Assert.AreEqual(2, msgs.Length)
            | other -> Assert.Fail(sprintf "expected ConversationSaved, got %A" other)
        finally
            cleanup root

    [<TestMethod>]
    member _.DeleteConversation_PublishesConversationDeleted() =
        let root = newRoot ()

        try
            let store, recorder = setup root

            store.AppendAsync "dev/s1" "default" [| message "User" "hi" "t1" |]
            |> fun t -> t.Wait()

            store.DeleteConversationAsync "dev/s1" "default" |> fun t -> t.Wait()

            Assert.IsTrue(recorder.Signals() |> List.contains (ConversationDeleted "default"))
        finally
            cleanup root

    [<TestMethod>]
    member _.DeleteSession_PublishesSessionConversationsDeleted() =
        let root = newRoot ()

        try
            let store, recorder = setup root

            store.AppendAsync "dev/s1" "default" [| message "User" "hi" "t1" |]
            |> fun t -> t.Wait()

            store.DeleteSessionAsync "dev/s1" |> fun t -> t.Wait()

            Assert.IsTrue(recorder.Signals() |> List.contains SessionConversationsDeleted)
        finally
            cleanup root

[<TestClass>]
type WorkspaceRegistryTests() =

    [<TestMethod>]
    member _.Register_ReplacesAndRemovePreservesRegistrySemantics() =
        let registry = WorkspaceRegistry.create ()
        let id = WorkspaceId.create "workspace"
        let first = WorkspaceDefinitions.Empty

        let replacement =
            { WorkspaceDefinitions.Empty with
                Tools = [] }

        Assert.IsTrue(registry.TryGet id |> Option.isNone)
        registry.Register(id, first)
        registry.Register(id, replacement)
        Assert.AreSame(replacement, registry.Get id)
        CollectionAssert.AreEquivalent([| id |], registry.ListKeys() |> List.toArray)
        Assert.IsTrue(registry.Remove id)
        Assert.IsFalse(registry.Remove id)

        let throwsWhenMissing =
            try
                registry.Get id |> ignore
                false
            with _ ->
                true

        Assert.IsTrue(throwsWhenMissing)
