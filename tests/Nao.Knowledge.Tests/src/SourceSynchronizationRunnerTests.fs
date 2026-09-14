namespace Nao.Knowledge.Tests

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Knowledge

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type SourceSynchronizationRunnerTests() =
    let authorization =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

    let now = DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero)
    let connectorVersion = ComponentVersion.create "test.connector" "1"

    let source =
        KnowledgeSource.create
            (KnowledgeSourceId.parse "resilient-source")
            (Uri "https://source.example.test/")
            (AuthorizationScope.identity authorization)
            DataClassification.Internal
            (TrustAssessment.create KnowledgeTrust.Unverified "test source" now)
            Map.empty
            now
            (KnowledgeLifecycle.active None false)

    let request =
        { Authorization = authorization
          Source = source
          Mode = SourceConnectorMode.Pull
          Checkpoint = None
          Policy =
            { AuthorizeAsync = fun _ _ _ -> Task.FromResult true
              Network = SourceNetworkPolicy.Denied }
          RequestedAt = now }

    let batch failures =
        { SourceId = source.Id
          Connector = connectorVersion
          StartedAt = now
          CompletedAt = now
          Changes = []
          Failures = failures
          Checkpoint = SourceConnectorCheckpoint.create connectorVersion "{}" }

    let stores () =
        let quarantined = ResizeArray<QuarantinedSourceItem>()
        let deadLettered = ResizeArray<DeadLetteredSourceSync>()

        let store =
            { SaveQuarantinedAsync =
                fun _ item _ ->
                    lock quarantined (fun () -> quarantined.Add item)
                    Task.CompletedTask
              SaveDeadLetteredAsync =
                fun _ item _ ->
                    lock deadLettered (fun () -> deadLettered.Add item)
                    Task.CompletedTask
              ListQuarantinedAsync = fun _ _ _ -> Task.FromResult(List.ofSeq quarantined)
              ListDeadLetteredAsync = fun _ _ _ -> Task.FromResult(List.ofSeq deadLettered) }

        store, quarantined, deadLettered

    let config retry concurrency pending store delay =
        { Retry = retry
          MaximumConcurrency = concurrency
          MaximumPending = pending
          FailureStore = store
          DelayAsync = delay
          Clock = fun () -> now }

    [<TestMethod>]
    member _.``Transient connector failure retries then quarantines item failures``() =
        let store, quarantined, deadLettered = stores ()
        let delays = ResizeArray<TimeSpan>()
        let mutable attempts = 0

        let connector =
            { Descriptor =
                { Connector = connectorVersion
                  Modes = Set.singleton SourceConnectorMode.Pull }
              SynchronizeAsync =
                fun _ _ ->
                    attempts <- attempts + 1

                    if attempts < 3 then
                        Task.FromException<SourceSyncBatch>(IOException "temporarily unavailable")
                    else
                        Task.FromResult(
                            batch
                                [ { Uri = source.Uri
                                    Kind = SourceItemFailureKind.InvalidContent
                                    Message = "malformed item"
                                    Attempts = 1 } ]
                        ) }

        let runner =
            SourceSynchronizationRunner.create (
                config (SourceRetryPolicy.Fixed(2, TimeSpan.FromSeconds 1L)) 1 1 store (fun delay _ ->
                    delays.Add delay
                    Task.CompletedTask)
            )

        let outcome =
            runner.RunAsync connector request CancellationToken.None
            |> _.GetAwaiter().GetResult()

        match outcome with
        | SourceSyncOutcome.Completed _ -> ()
        | _ -> Assert.Fail "Expected synchronization to recover."

        Assert.AreEqual(3, attempts)
        Assert.AreEqual(2, delays.Count)
        Assert.AreEqual(1, quarantined.Count)
        Assert.AreEqual(3, QuarantinedSourceItem.syncAttempts quarantined.[0])
        Assert.AreEqual(0, deadLettered.Count)

    [<TestMethod>]
    member _.``Non-retryable failure is dead-lettered without delay``() =
        let store, quarantined, deadLettered = stores ()
        let mutable attempts = 0
        let mutable delays = 0

        let connector =
            { Descriptor =
                { Connector = connectorVersion
                  Modes = Set.singleton SourceConnectorMode.Pull }
              SynchronizeAsync =
                fun _ _ ->
                    attempts <- attempts + 1

                    Task.FromException<SourceSyncBatch>(UnauthorizedAccessException "source denied") }

        let runner =
            SourceSynchronizationRunner.create (
                config (SourceRetryPolicy.Fixed(5, TimeSpan.Zero)) 1 1 store (fun _ _ ->
                    delays <- delays + 1
                    Task.CompletedTask)
            )

        let outcome =
            runner.RunAsync connector request CancellationToken.None
            |> _.GetAwaiter().GetResult()

        match outcome with
        | SourceSyncOutcome.DeadLettered item ->
            Assert.AreEqual(1, DeadLetteredSourceSync.attempts item)
            Assert.AreEqual(PlatformErrorCategory.PermissionDenied, (DeadLetteredSourceSync.failure item).Category)
        | _ -> Assert.Fail "Expected a dead-letter outcome."

        Assert.AreEqual(1, attempts)
        Assert.AreEqual(0, delays)
        Assert.AreEqual(0, quarantined.Count)
        Assert.AreEqual(1, deadLettered.Count)

    [<TestMethod>]
    member _.``Batch runner bounds concurrency and rejects excess backlog``() =
        let store, _, _ = stores ()
        let mutable active = 0
        let mutable maximumActive = 0

        let connector =
            { Descriptor =
                { Connector = connectorVersion
                  Modes = Set.singleton SourceConnectorMode.Pull }
              SynchronizeAsync =
                fun _ cancellationToken ->
                    task {
                        let current = Interlocked.Increment &active

                        lock store (fun () -> maximumActive <- max maximumActive current)

                        try
                            do! Task.Delay(30, cancellationToken)
                            return batch []
                        finally
                            Interlocked.Decrement &active |> ignore
                    } }

        let runner =
            SourceSynchronizationRunner.create (config SourceRetryPolicy.None 2 4 store (fun _ _ -> Task.CompletedTask))

        let work =
            List.replicate
                4
                { Connector = connector
                  Request = request }

        let outcomes =
            runner.RunManyAsync work CancellationToken.None |> _.GetAwaiter().GetResult()

        Assert.AreEqual(4, outcomes.Length)
        Assert.AreEqual(2, maximumActive)

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            runner.RunManyAsync
                ({ Connector = connector
                   Request = request }
                 :: work)
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.``Caller cancellation is neither retried nor dead-lettered``() =
        let store, _, deadLettered = stores ()
        let mutable attempts = 0

        let connector =
            { Descriptor =
                { Connector = connectorVersion
                  Modes = Set.singleton SourceConnectorMode.Pull }
              SynchronizeAsync =
                fun _ cancellationToken ->
                    attempts <- attempts + 1
                    Task.FromCanceled<SourceSyncBatch>(cancellationToken) }

        let runner =
            SourceSynchronizationRunner.create (
                config (SourceRetryPolicy.Fixed(3, TimeSpan.Zero)) 1 1 store (fun _ _ -> Task.CompletedTask)
            )

        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        Assert.ThrowsExactly<OperationCanceledException>(fun () ->
            runner.RunAsync connector request cancellation.Token
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore

        Assert.AreEqual(0, attempts)
        Assert.AreEqual(0, deadLettered.Count)

    [<TestMethod>]
    member _.``Reference failure store isolates inspection by authorization identity``() =
        let store = SourceFailureStores.inMemory ()

        let connector =
            { Descriptor =
                { Connector = connectorVersion
                  Modes = Set.singleton SourceConnectorMode.Pull }
              SynchronizeAsync =
                fun _ _ ->
                    Task.FromResult(
                        batch
                            [ { Uri = source.Uri
                                Kind = SourceItemFailureKind.InvalidContent
                                Message = "quarantine"
                                Attempts = 1 } ]
                    ) }

        let runner =
            SourceSynchronizationRunner.create (config SourceRetryPolicy.None 1 1 store (fun _ _ -> Task.CompletedTask))

        runner.RunAsync connector request CancellationToken.None
        |> _.GetAwaiter().GetResult()
        |> ignore

        let own =
            store.ListQuarantinedAsync authorization source.Id CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let foreignAuthorization =
            SecurityPrincipal.create (TenantId.parse "other") (UserId.parse "user") []
            |> fun principal ->
                AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
                |> Option.get

        let foreign =
            store.ListQuarantinedAsync foreignAuthorization source.Id CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.AreEqual(1, own.Length)
        Assert.AreEqual(0, foreign.Length)
