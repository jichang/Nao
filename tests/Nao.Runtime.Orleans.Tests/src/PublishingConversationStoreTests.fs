namespace Nao.Runtime.Orleans.Tests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence
open Nao.Runtime.Orleans

module private ConversationTestHelpers =
    let scope tenant user group workspace session =
        let principal =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse tenant)
                (UserId.parse user)
                (Some(ServiceId.parse "private-service"))
                [ GroupId.parse "group"; GroupId.parse "other" ]
                [ "private-role" ]
                (Map.ofList [ "private-claim", "private-value" ])
                AuthenticationStrength.MultiFactor

        AuthorizationScope.tryCreate
            principal
            (group |> Option.map GroupId.parse)
            (WorkspaceId.parse workspace)
            (session |> Option.map SessionId.parse)
        |> Option.get

    let owner = scope "tenant" "user" (Some "group") "workspace" (Some "session")

    let owners =
        [ owner
          scope "other" "user" (Some "group") "workspace" (Some "session")
          scope "tenant" "other" (Some "group") "workspace" (Some "session")
          scope "tenant" "user" (Some "other") "workspace" (Some "session")
          scope "tenant" "user" None "workspace" (Some "session")
          scope "tenant" "user" (Some "group") "other" (Some "session")
          scope "tenant" "user" (Some "group") "workspace" (Some "other")
          scope "tenant" "user" (Some "group") "workspace" None ]

    let key authorization =
        AuthorizationScope.identity authorization |> AuthorizationIdentity.storageKey

    let root () =
        let path =
            Path.Combine(Path.GetTempPath(), "nao-conversation-scope-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory path |> ignore
        path

    let message correlation role content turnId : PersistedMessage =
        { Role = role
          Content = content
          Timestamp = DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero)
          Correlation = correlation
          TurnId = turnId
          Steps = [||]
          Attachments = [||]
          Artifacts = [||] }

open ConversationTestHelpers

[<TestClass>]
type ConversationAuthorizationTests() =
    [<TestMethod>]
    member _.CompleteIdentityPartitionsAllOperations() =
        let directory = root ()

        try
            let store = FileConversationStore.create directory
            let correlation = CorrelationContext.root ()

            for index, authorization in List.indexed owners do
                store.SaveAsync authorization "shared" [| message correlation "User" (string index) "turn" |]
                |> _.Wait()

            for index, authorization in List.indexed owners do
                let loaded = store.LoadAsync authorization "shared" |> _.Result
                CollectionAssert.AreEqual([| string index |], loaded |> Array.map _.Content)

                Assert.AreEqual(
                    1,
                    store.LoadByExecutionAsync authorization correlation.ExecutionId
                    |> _.Result
                    |> Array.length
                )

                CollectionAssert.AreEqual(
                    [| "shared" |],
                    store.ListConversationsAsync authorization
                    |> _.Result
                    |> Array.map _.ConversationName
                )

            store.DeleteConversationAsync owner "shared" |> _.Wait()
            Assert.AreEqual(0, store.LoadAsync owner "shared" |> _.Result |> Array.length)

            for authorization in owners |> List.tail do
                Assert.AreEqual(1, store.LoadAsync authorization "shared" |> _.Result |> Array.length)

            store.DeleteScopeAsync owners.[1] |> _.Wait()
            Assert.AreEqual(0, store.ListConversationsAsync owners.[1] |> _.Result |> Array.length)
            Assert.AreEqual(1, store.ListConversationsAsync owners.[2] |> _.Result |> Array.length)
        finally
            Directory.Delete(directory, true)

    [<TestMethod>]
    member _.AppendSaveAndReplayPreserveConversationSemantics() =
        let directory = root ()

        try
            let correlation = CorrelationContext.root ()
            let store = FileConversationStore.create directory

            store.AppendAsync owner "default" [| message correlation "User" "first" "turn-1" |]
            |> _.Wait()

            store.AppendAsync owner "default" [| message correlation "Assistant" "second" "turn-1" |]
            |> _.Wait()

            store.AppendAsync owner "default" [||] |> _.Wait()

            let reloaded = FileConversationStore.create directory

            CollectionAssert.AreEqual(
                [| "first"; "second" |],
                reloaded.LoadAsync owner "default" |> _.Result |> Array.map _.Content
            )

            let meta = reloaded.ListConversationsAsync owner |> _.Result |> Array.exactlyOne
            Assert.AreEqual("default", meta.ConversationName)
            Assert.AreEqual(2, meta.MessageCount)

            reloaded.SaveAsync owner "default" [| message correlation "Assistant" "replacement" "turn-2" |]
            |> _.Wait()

            CollectionAssert.AreEqual(
                [| "replacement" |],
                (FileConversationStore.create directory).LoadAsync owner "default"
                |> _.Result
                |> Array.map _.Content
            )
        finally
            Directory.Delete(directory, true)

    [<TestMethod>]
    member _.StreamKeyAndV1PayloadContainNoAuthorizationEvidence() =
        let directory = root ()

        try
            let store = FileConversationStore.create directory

            store.SaveAsync owner "default" [| message (CorrelationContext.root ()) "User" "hello" "turn" |]
            |> _.Wait()

            let path = Path.Combine(directory, "conversation", key owner + ".jsonl")
            Assert.IsTrue(File.Exists path)

            for payload in File.ReadAllLines path do
                use document = JsonDocument.Parse payload
                Assert.AreEqual(1, document.RootElement.GetProperty("Version").GetInt32())

                for secret in
                    [ "tenant"
                      "user"
                      "group"
                      "workspace"
                      "session"
                      "private-service"
                      "private-role"
                      "private-claim"
                      "private-value"
                      "MultiFactor"
                      "Authorization"
                      "Owner"
                      "SessionId" ] do
                    Assert.IsFalse(payload.Contains secret)
        finally
            Directory.Delete(directory, true)

    [<TestMethod>]
    member _.CorruptForeignScopeDoesNotBlockOwnScope() =
        let directory = root ()

        try
            let store = FileConversationStore.create directory
            let foreign = owners.[1]

            store.SaveAsync foreign "default" [| message (CorrelationContext.root ()) "User" "foreign" "turn" |]
            |> _.Wait()

            File.AppendAllText(Path.Combine(directory, "conversation", key foreign + ".jsonl"), "{invalid\n")

            store.SaveAsync owner "default" [| message (CorrelationContext.root ()) "User" "own" "turn" |]
            |> _.Wait()

            Assert.AreEqual(1, store.LoadAsync owner "default" |> _.Result |> Array.length)

            Assert.ThrowsExactly<InvalidDataException>(fun () ->
                store.LoadAsync foreign "default" |> _.GetAwaiter().GetResult() |> ignore)
            |> ignore
        finally
            Directory.Delete(directory, true)

    [<TestMethod>]
    member _.UnsupportedVersionsAndFailedAppendsDoNotPublishState() =
        let unsupportedStore =
            FileConversationStore.createPersistent (fun _ ->
                { Append = fun _ -> Assert.Fail("Unsupported stream must not append.")
                  LoadAll = fun () -> [ """{"Version":99,"Event":"DeleteScope"}""" ] })

        Assert.ThrowsExactly<InvalidDataException>(fun () ->
            unsupportedStore.LoadAsync owner "default"
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        let lines = ResizeArray<string>()
        let mutable fail = false

        let eventStore =
            { Append =
                fun line ->
                    if fail then
                        raise (IOException "failed")
                    else
                        lines.Add line
              LoadAll = fun () -> List.ofSeq lines }

        let store = FileConversationStore.createPersistent (fun _ -> eventStore)
        let original = message (CorrelationContext.root ()) "User" "original" "turn"
        store.SaveAsync owner "default" [| original |] |> _.Wait()
        fail <- true

        Assert.ThrowsExactly<IOException>(fun () ->
            store.AppendAsync owner "default" [| { original with Content = "new" } |]
            |> _.GetAwaiter().GetResult())
        |> ignore

        CollectionAssert.AreEqual([| original |], store.LoadAsync owner "default" |> _.Result)

[<TestClass>]
type PublishingConversationStoreTests() =
    [<TestMethod>]
    member _.PublishesOnlyAfterDurableWrite() =
        let directory = root ()

        try
            let bus = InMemoryEventBus.create ()
            let received = ResizeArray<NaoEvent>()

            EventBus.subscribe
                (EventConsumer.create (fun event ->
                    received.Add event
                    Task.CompletedTask))
                bus

            let store =
                FileConversationStore.create directory |> PublishingConversationStore.create bus

            let correlation = CorrelationContext.root ()

            store.AppendAsync owner "default" [| message correlation "User" "hello" "turn" |]
            |> _.Wait()

            Assert.AreEqual(1, store.LoadAsync owner "default" |> _.Result |> Array.length)

            match List.ofSeq received with
            | [ ConversationCaptured(eventScope, MessagesAppended("default", messages)) ] ->
                Assert.AreEqual("user", eventScope.UserId)
                Assert.AreEqual("session", eventScope.SessionId)
                Assert.AreEqual("workspace", eventScope.WorkspaceKey)
                Assert.AreEqual(correlation, messages.Head.Correlation)
            | events -> Assert.Fail(sprintf "Unexpected events: %A" events)
        finally
            Directory.Delete(directory, true)
