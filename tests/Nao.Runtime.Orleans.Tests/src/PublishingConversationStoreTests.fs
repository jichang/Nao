namespace Nao.Runtime.Orleans.Tests

open System
open System.IO
open System.Text.Json
open System.Threading
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

    let owner = scope "tenant" "user" (Some "group") "workspace@1" (Some "session")

    let owners =
        [ owner
          scope "other" "user" (Some "group") "workspace@1" (Some "session")
          scope "tenant" "other" (Some "group") "workspace@1" (Some "session")
          scope "tenant" "user" (Some "other") "workspace@1" (Some "session")
          scope "tenant" "user" None "workspace@1" (Some "session")
          scope "tenant" "user" (Some "group") "other@1" (Some "session")
          scope "tenant" "user" (Some "group") "workspace@1" (Some "other")
          scope "tenant" "user" (Some "group") "workspace@1" None ]

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
    member _.ArtifactTransportRestoresAddressIntegrityAndLineage() =
        let correlation = CorrelationContext.root ()
        let artifactId = ArtifactId.generate ()
        let parentId = ArtifactId.generate ()
        let payload = """{"result":"verified"}"""
        let recordedAt = DateTimeOffset(2026, 10, 13, 3, 50, 0, TimeSpan.Zero)

        let idempotencyKey =
            IdempotencyKey.forExecution correlation.ExecutionId |> IdempotencyKey.value

        let schemaVersion = ArtifactSchemaVersion.current |> ArtifactSchemaVersion.value

        let role = ArtifactRole.Produced |> ArtifactRole.serialize
        let address = ArtifactAddress.forProduced artifactId |> ArtifactAddress.value
        let contentHash = ArtifactContentHash.compute payload |> ArtifactContentHash.value

        let record =
            ArtifactRecord(
                Id = ArtifactId.serialize artifactId,
                IdempotencyKey = idempotencyKey,
                SchemaVersion = schemaVersion,
                Role = role,
                Address = address,
                Kind = "report",
                ContentType = "application/json",
                Payload = payload,
                ContentHash = contentHash,
                RecordedBy = ExecutionId.serialize correlation.ExecutionId,
                RecordedAt = recordedAt,
                ParentArtifactIds = [| ArtifactId.serialize parentId |]
            )

        let transported =
            { message correlation "Assistant" "done" "turn" with
                Artifacts = [| record |] }
            |> PublishingConversationStore.toMessage
            |> _.Artifacts
            |> List.exactlyOne

        Assert.AreEqual(artifactId, Artifact.id transported)
        Assert.AreEqual(ArtifactRole.Produced, Artifact.role transported)
        Assert.AreEqual(payload, Artifact.payload transported)

        let lineage = Artifact.lineage transported
        Assert.AreEqual(correlation.ExecutionId, ArtifactLineage.recordedBy lineage)
        Assert.AreEqual(recordedAt, ArtifactLineage.recordedAt lineage)
        Assert.AreEqual([ parentId ], ArtifactLineage.parentArtifactIds lineage)

    [<TestMethod>]
    member _.StreamKeyAndV1PayloadContainOnlyExplicitOwnerIdentity() =
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
                let persistedOwner = document.RootElement.GetProperty("Owner")
                Assert.AreEqual("tenant", persistedOwner.GetProperty("TenantId").GetString())
                Assert.AreEqual("workspace@1", persistedOwner.GetProperty("WorkspaceId").GetString())
                Assert.AreEqual("user", persistedOwner.GetProperty("UserId").GetString())

                Assert.AreEqual(
                    "session",
                    persistedOwner.GetProperty("SessionId").EnumerateArray()
                    |> Seq.last
                    |> _.GetString()
                )

                for secret in
                    [ "group"
                      "private-service"
                      "private-role"
                      "private-claim"
                      "private-value"
                      "MultiFactor"
                      "Authorization" ] do
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
    member _.PersistedOwnerMismatchFailsBeforeConversationDisclosure() =
        let directory = root ()

        try
            let store = FileConversationStore.create directory

            store.SaveAsync owner "default" [| message (CorrelationContext.root ()) "User" "secret" "turn" |]
            |> _.Wait()

            let path = Path.Combine(directory, "conversation", key owner + ".jsonl")
            let payload = File.ReadAllText path

            let corrupted =
                payload.Replace("\"TenantId\":\"tenant\"", "\"TenantId\":\"other-tenant\"")

            Assert.AreNotEqual(payload, corrupted)
            File.WriteAllText(path, corrupted)

            Assert.ThrowsExactly<InvalidDataException>(fun () ->
                store.LoadAsync owner "default" |> _.GetAwaiter().GetResult() |> ignore)
            |> ignore
        finally
            Directory.Delete(directory, true)

    [<TestMethod>]
    member _.ConversationSequenceGapFailsBeforeProjection() =
        let directory = root ()

        try
            let store = FileConversationStore.create directory

            store.SaveAsync owner "default" [| message (CorrelationContext.root ()) "User" "secret" "turn" |]
            |> _.Wait()

            let path = Path.Combine(directory, "conversation", key owner + ".jsonl")
            let payload = File.ReadAllText path
            let corrupted = payload.Replace("\"Sequence\":1", "\"Sequence\":2")
            Assert.AreNotEqual(payload, corrupted)
            File.WriteAllText(path, corrupted)

            Assert.ThrowsExactly<InvalidDataException>(fun () ->
                store.LoadAsync owner "default" |> _.GetAwaiter().GetResult() |> ignore)
            |> ignore
        finally
            Directory.Delete(directory, true)

    [<TestMethod>]
    member _.ConcurrentConversationCommitsRejectTheStaleSequence() =
        let lines = ResizeArray<string>()
        use readsAligned = new Barrier(2)

        let eventStore =
            { Append =
                fun expected payload ->
                    lock lines (fun () ->
                        let actual = int64 lines.Count

                        if actual <> expected then
                            raise (ConversationSequenceConflictException(expected, actual))

                        lines.Add payload)
              LoadAll =
                fun () ->
                    let snapshot = lock lines (fun () -> List.ofSeq lines)

                    if snapshot.IsEmpty then
                        readsAligned.SignalAndWait()

                    snapshot }

        let first = FileConversationStore.createPersistent (fun _ -> eventStore)
        let second = FileConversationStore.createPersistent (fun _ -> eventStore)
        let correlation = CorrelationContext.root ()

        let commit store content =
            task {
                try
                    do! store.SaveAsync owner "default" [| message correlation "User" content "turn" |]
                    return true
                with :? ConversationSequenceConflictException ->
                    return false
            }

        let results =
            Task.WhenAll(
                Task.Run<bool>(Func<Task<bool>>(fun () -> commit first "first")),
                Task.Run<bool>(Func<Task<bool>>(fun () -> commit second "second"))
            )
            |> _.GetAwaiter().GetResult()

        CollectionAssert.AreEquivalent([| true; false |], results)
        Assert.AreEqual(1, lines.Count)

    [<TestMethod>]
    member _.UnsupportedVersionsAndFailedAppendsDoNotPublishState() =
        let unsupportedStore =
            FileConversationStore.createPersistent (fun _ ->
                { Append = fun _ _ -> Assert.Fail("Unsupported stream must not append.")
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
                fun _ line ->
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
                Assert.AreEqual("workspace@1", eventScope.WorkspaceKey)

                Assert.AreEqual(
                    RuntimeAddress.sessionFromScope owner |> Option.get |> RuntimeAddress.sessionKey,
                    eventScope.SessionKey
                )

                Assert.AreEqual(correlation, messages.Head.Correlation)
            | events -> Assert.Fail(sprintf "Unexpected events: %A" events)
        finally
            Directory.Delete(directory, true)
