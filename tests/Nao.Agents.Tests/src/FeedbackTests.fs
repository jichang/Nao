module FeedbackTests

open System
open System.IO
open Microsoft.Data.Sqlite
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

let private scope tenant user group workspace session =
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

let private owner =
    scope "tenant" "user" (Some "group") "workspace" (Some "session")

let private owners =
    [ owner
      scope "other" "user" (Some "group") "workspace" (Some "session")
      scope "tenant" "other" (Some "group") "workspace" (Some "session")
      scope "tenant" "user" (Some "other") "workspace" (Some "session")
      scope "tenant" "user" None "workspace" (Some "session")
      scope "tenant" "user" (Some "group") "other" (Some "session")
      scope "tenant" "user" (Some "group") "workspace" (Some "other")
      scope "tenant" "user" (Some "group") "workspace" None ]

let private other = owners.[1]

let private key authorization =
    AuthorizationScope.identity authorization |> AuthorizationIdentity.storageKey

let private timestamp = DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero)

let private turn id correlation createdAt =
    { TurnRecord.empty correlation with
        TurnId = id
        AgentName = "agent"
        Input = "input"
        Output = "output"
        CreatedAt = createdAt }

let private feedback id turnId createdAt =
    { Id = id
      TurnId = turnId
      Sentiment = FeedbackSentiment.Negative
      Comment = Some "comment"
      CreatedAt = createdAt
      Metadata = Map.empty }

let private withStorage action =
    let root =
        Path.Combine(Path.GetTempPath(), "nao-feedback-scope-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore

    let factory =
        DbConnectionFactory.ofFunc (fun () ->
            new SqliteConnection(sprintf "Data Source=%s;Pooling=False" (Path.Combine(root, "feedback.db")))
            :> Data.Common.DbConnection)

    try
        action root factory
    finally
        Directory.Delete(root, true)

[<TestClass>]
type TurnRecorderTests() =
    [<TestMethod>]
    member _.CapturesTurnWithoutAuthorizationIdentity() =
        let correlation = CorrelationContext.root ()
        let recorder = TurnRecorder.create ("turn", correlation, "agent", "hello")

        let eventScope =
            EventScope.Create("user", "session", "", "workspace", "turn", "user/session", correlation)

        EventConsumer.handleAsync (TurnProgress(eventScope, ToolInvoked("search", "query"))) recorder.Consumer
        |> _.Wait()

        EventConsumer.handleAsync (TurnProgress(eventScope, ToolCompleted("search", "result"))) recorder.Consumer
        |> _.Wait()

        EventConsumer.handleAsync (TurnProgress(eventScope, AnswerProduced "answer")) recorder.Consumer
        |> _.Wait()

        let snapshot = TurnRecorder.snapshot recorder
        Assert.AreEqual("answer", snapshot.Output)
        Assert.AreEqual([ "search" ], snapshot.ToolCalls |> List.map _.Name)

[<TestClass>]
type FeedbackAuthorizationTests() =
    [<TestMethod>]
    member _.CompleteIdentityPartitionsTurnsAndFeedbackAcrossBackends() =
        withStorage (fun root factory ->
            let correlation = CorrelationContext.root ()
            let turnId = "shared-turn"
            let feedbackId = Guid.NewGuid()
            let memoryTurns = TurnStores.inMemory ()
            let memoryFeedback = FeedbackStores.inMemory ()

            for makeTurns, makeFeedback in
                [ (fun () -> memoryTurns), (fun () -> memoryFeedback)
                  (fun () -> TurnStores.file root), (fun () -> FeedbackStores.file root)
                  (fun () -> TurnStores.ado factory), (fun () -> FeedbackStores.ado factory) ] do
                let turns = makeTurns ()
                let feedbacks = makeFeedback ()

                for authorization in owners do
                    turns.SaveAsync authorization (turn turnId correlation timestamp) |> _.Wait()

                    feedbacks.SaveAsync authorization (feedback feedbackId turnId timestamp)
                    |> _.Wait()

                for authorization in owners do
                    Assert.AreEqual(1, (makeTurns ()).GetAllAsync authorization |> _.Result |> List.length)
                    Assert.AreEqual(1, (makeFeedback ()).GetAllAsync authorization |> _.Result |> List.length)

                Assert.AreEqual(
                    1,
                    (makeTurns ()).GetForExecutionAsync owner correlation.ExecutionId
                    |> _.Result
                    |> List.length
                )

                Assert.AreEqual(1, (makeFeedback ()).GetForTurnAsync owner turnId |> _.Result |> List.length)

                Assert.AreEqual(Ok 1, (makeTurns ()).DeleteScopeAsync owner |> _.Result)
                Assert.AreEqual(Ok 1, (makeFeedback ()).DeleteScopeAsync owner |> _.Result)

                for authorization in owners |> List.tail do
                    Assert.AreEqual(1, (makeTurns ()).GetAllAsync authorization |> _.Result |> List.length)
                    Assert.AreEqual(1, (makeFeedback ()).GetAllAsync authorization |> _.Result |> List.length))

    [<TestMethod>]
    member _.RetentionIsStrictAndScopeLocal() =
        let turns = TurnStores.inMemory ()
        let feedbacks = FeedbackStores.inMemory ()
        let correlation = CorrelationContext.root ()

        turns.SaveAsync owner (turn "old" correlation (timestamp.AddTicks(-1L)))
        |> _.Wait()

        turns.SaveAsync owner (turn "current" correlation timestamp) |> _.Wait()

        turns.SaveAsync other (turn "foreign" correlation (timestamp.AddTicks(-1L)))
        |> _.Wait()

        feedbacks.SaveAsync owner (feedback (Guid.NewGuid()) "old" (timestamp.AddTicks(-1L)))
        |> _.Wait()

        feedbacks.SaveAsync owner (feedback (Guid.NewGuid()) "current" timestamp)
        |> _.Wait()

        feedbacks.SaveAsync other (feedback (Guid.NewGuid()) "foreign" (timestamp.AddTicks(-1L)))
        |> _.Wait()

        Assert.AreEqual(Ok 1, turns.DeleteExpiredAsync owner timestamp |> _.Result)
        Assert.AreEqual(Ok 1, feedbacks.DeleteExpiredAsync owner timestamp |> _.Result)
        Assert.AreEqual([ "current" ], turns.GetAllAsync owner |> _.Result |> List.map _.TurnId)
        Assert.AreEqual(1, feedbacks.GetAllAsync owner |> _.Result |> List.length)
        Assert.AreEqual(1, turns.GetAllAsync other |> _.Result |> List.length)
        Assert.AreEqual(1, feedbacks.GetAllAsync other |> _.Result |> List.length)

    [<TestMethod>]
    member _.PersistentStreamsUseClaimFreeKeysAndIdentityFreeV1Payloads() =
        withStorage (fun root factory ->
            let correlation = CorrelationContext.root ()

            for turns, feedbacks in
                [ TurnStores.file root, FeedbackStores.file root
                  TurnStores.ado factory, FeedbackStores.ado factory ] do
                turns.SaveAsync owner (turn "turn" correlation timestamp) |> _.Wait()

                feedbacks.SaveAsync owner (feedback (Guid.NewGuid()) "turn" timestamp)
                |> _.Wait()

            let payloads =
                [ Path.Combine(root, "turn-store", key owner + ".jsonl")
                  Path.Combine(root, "feedback-store", key owner + ".jsonl") ]
                |> List.collect (File.ReadAllLines >> Array.toList)

            let rows =
                Ado.query factory "SELECT stream, payload FROM nao_events" [] (fun row ->
                    Ado.getString row "stream", Ado.getString row "payload")
                |> _.Result

            CollectionAssert.AreEquivalent(
                [| "turn-store/" + key owner; "feedback-store/" + key owner |],
                rows |> List.map fst |> List.toArray
            )

            for payload in payloads @ (rows |> List.map snd) do
                StringAssert.Contains(payload, "\"Version\":1")

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
                      "UserId"
                      "SessionId"
                      "WorkspaceKey" ] do
                    Assert.IsFalse(payload.Contains secret))

    [<TestMethod>]
    member _.CorruptionAndFailedAppendsAreScopeLocal() =
        withStorage (fun root _ ->
            let correlation = CorrelationContext.root ()
            let foreign = TurnStores.file root
            foreign.SaveAsync other (turn "foreign" correlation timestamp) |> _.Wait()
            File.AppendAllText(Path.Combine(root, "turn-store", key other + ".jsonl"), "{invalid\n")

            let own = TurnStores.file root
            own.SaveAsync owner (turn "own" correlation timestamp) |> _.Wait()
            Assert.AreEqual(1, own.GetAllAsync owner |> _.Result |> List.length)

            Assert.ThrowsExactly<InvalidDataException>(fun () ->
                own.GetAllAsync other |> _.GetAwaiter().GetResult() |> ignore)
            |> ignore

            let lines = ResizeArray<string>()
            let mutable fail = false

            let store =
                { Append =
                    fun line ->
                        if fail then
                            raise (IOException "failed")
                        else
                            lines.Add line
                  LoadAll = fun () -> List.ofSeq lines }

            let persistent = PersistentFeedbackStore.create (fun _ -> store)
            let original = feedback (Guid.NewGuid()) "turn" timestamp
            persistent.SaveAsync owner original |> _.Wait()
            fail <- true

            Assert.ThrowsExactly<IOException>(fun () ->
                persistent.SaveAsync owner (feedback (Guid.NewGuid()) "other" timestamp)
                |> _.GetAwaiter().GetResult())
            |> ignore

            Assert.AreEqual([ original ], persistent.GetAllAsync owner |> _.Result))

    [<TestMethod>]
    member _.UnsupportedVersionsAreRejected() =
        let unsupportedTurn: TurnStoreDocument =
            { Version = 99
              Event = TurnStoreEvent.DeleteScope }

        let unsupportedFeedback: FeedbackStoreDocument =
            { Version = 99
              Event = FeedbackStoreEvent.DeleteScope }

        let turnStore =
            PersistentTurnStore.create (fun _ ->
                { Append = fun _ -> Assert.Fail("Unsupported stream must not append.")
                  LoadAll = fun () -> [ FeedbackJson.serialize unsupportedTurn ] })

        let feedbackStore =
            PersistentFeedbackStore.create (fun _ ->
                { Append = fun _ -> Assert.Fail("Unsupported stream must not append.")
                  LoadAll = fun () -> [ FeedbackJson.serialize unsupportedFeedback ] })

        Assert.ThrowsExactly<InvalidDataException>(fun () ->
            turnStore.GetAllAsync owner |> _.GetAwaiter().GetResult() |> ignore)
        |> ignore

        Assert.ThrowsExactly<InvalidDataException>(fun () ->
            feedbackStore.GetAllAsync owner |> _.GetAwaiter().GetResult() |> ignore)
        |> ignore

[<TestClass>]
type FeedbackServiceTests() =
    [<TestMethod>]
    member _.ServiceForwardsAuthenticatedScope() =
        let turns = TurnStores.inMemory ()
        let feedbacks = FeedbackStores.inMemory ()
        let service = FeedbackService.create turns feedbacks
        let correlation = CorrelationContext.root ()
        let entry = feedback (Guid.NewGuid()) "turn" timestamp

        service.RecordTurnAsync owner (turn "turn" correlation timestamp) |> _.Wait()
        service.SubmitFeedbackAsync owner entry |> _.Wait()

        Assert.AreEqual(1, turns.GetAllAsync owner |> _.Result |> List.length)
        Assert.AreEqual([ entry ], feedbacks.GetAllAsync owner |> _.Result)
