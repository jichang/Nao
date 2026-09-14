namespace Nao.Persistence.Tests

open System
open System.Collections.Generic
open System.IO
open Microsoft.Data.Sqlite
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type ExecutionJournalAuthorizationTests() =
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
          scope "tenant" "user" (Some "group") "workspace" None
          scope "a/b" "c" None "workspace" None
          scope "a_b" "c" None "workspace" None
          scope "a" "b/c" None "workspace" None ]

    let key authorization =
        AuthorizationScope.identity authorization |> AuthorizationIdentity.storageKey

    let timestamp = DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero)

    let record id (correlation: CorrelationContext) actor =
        { Id = id
          IdempotencyKey = IdempotencyKey.forExecution correlation.ExecutionId
          Correlation = correlation
          TurnId = "turn"
          ToolName = actor
          ReplaySafety = ToolReplaySafety.ExternallySideEffecting
          Input = "input"
          Output = "output"
          Failure = None
          ExecutedAt = timestamp
          Reverted = false
          Metadata = Map.empty }

    let checkpoint id (correlation: CorrelationContext) =
        { Id = id
          IdempotencyKey = IdempotencyKey.forExecution correlation.ExecutionId
          Correlation = correlation
          TurnId = "turn"
          AgentId = "agent"
          Phase = HarnessCheckpointPhase.Succeeded
          RecordedAt = timestamp }

    let transition id (correlation: CorrelationContext) actor =
        { Id = id
          IdempotencyKey = IdempotencyKey.forExecution correlation.ExecutionId
          Correlation = correlation
          TurnId = "turn"
          ToolName = actor
          Input = "input"
          Phase = ToolSideEffectPhase.Succeeded
          Detail = None
          RecordedAt = timestamp }

    let withStorage action =
        let root =
            Path.Combine(Path.GetTempPath(), "nao-journal-scope-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory root |> ignore

        let factory =
            DbConnectionFactory.ofFunc (fun () ->
                new SqliteConnection(sprintf "Data Source=%s;Pooling=False" (Path.Combine(root, "journal.db")))
                :> Data.Common.DbConnection)

        try
            action root factory
        finally
            Directory.Delete(root, true)

    [<TestMethod>]
    member _.CompleteAuthorizationIdentityPartitionsEveryOperation() =
        withStorage (fun root factory ->
            let inMemory = InMemoryExecutionJournal.create ()

            for make in
                [ (fun () -> inMemory)
                  (fun () -> ExecutionJournals.file root)
                  (fun () -> ExecutionJournals.ado factory) ] do
                let recordId = Guid.NewGuid()
                let checkpointId = Guid.NewGuid()
                let transitionId = Guid.NewGuid()
                let correlation = CorrelationContext.root ()

                for index, authorization in List.indexed owners do
                    let current = make ()

                    do
                        current.RecordAsync authorization (record recordId correlation (string index))
                        |> _.Wait()

                    do
                        current.Checkpoints.Save authorization (checkpoint checkpointId correlation)
                        |> _.Wait()

                    do
                        current.SideEffects.Save authorization (transition transitionId correlation (string index))
                        |> _.Wait()

                for index, authorization in List.indexed owners do
                    let current = make ()

                    Assert.AreEqual(
                        [ string index ],
                        current.GetHistoryAsync authorization |> _.Result |> List.map _.ToolName
                    )

                    Assert.AreEqual(
                        1,
                        current.GetByExecutionAsync authorization correlation.ExecutionId
                        |> _.Result
                        |> List.length
                    )

                    Assert.AreEqual(1, current.GetRevertibleAsync authorization |> _.Result |> List.length)

                    Assert.AreEqual(
                        1,
                        current.Checkpoints.GetByExecution authorization correlation.ExecutionId
                        |> _.Result
                        |> List.length
                    )

                    Assert.AreEqual(
                        1,
                        current.SideEffects.GetByIdempotencyKey
                            authorization
                            (IdempotencyKey.forExecution correlation.ExecutionId)
                        |> _.Result
                        |> List.length
                    )

                    current.MarkRevertedAsync authorization recordId |> _.Wait()
                    Assert.IsTrue((make ()).GetRevertibleAsync authorization |> _.Result |> List.isEmpty)
                    Assert.AreEqual(Ok 3, (make ()).DeleteScopeAsync authorization |> _.Result)
                    Assert.AreEqual(Ok 0, (make ()).DeleteScopeAsync authorization |> _.Result)

                    for pending in List.skip (index + 1) owners do
                        Assert.AreEqual(1, (make ()).GetHistoryAsync pending |> _.Result |> List.length))

    [<TestMethod>]
    member _.CrossScopeRecordMutationIsRejected() =
        withStorage (fun root _ ->
            let foreign = owners.[1]

            for journal in [ InMemoryExecutionJournal.create (); ExecutionJournals.file root ] do
                let value = record (Guid.NewGuid()) (CorrelationContext.root ()) "actor"
                journal.RecordAsync owner value |> _.Wait()

                Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
                    journal.MarkRevertedAsync foreign value.Id |> _.GetAwaiter().GetResult())
                |> ignore)

    [<TestMethod>]
    member _.PersistenceUsesClaimFreeKeysAndOwnerFreeV1Payloads() =
        withStorage (fun root factory ->
            let principal =
                SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") [ GroupId.parse "group" ]

            let sameOwner =
                AuthorizationScope.tryCreate
                    principal
                    (Some(GroupId.parse "group"))
                    (WorkspaceId.parse "workspace")
                    (Some(SessionId.parse "session"))
                |> Option.get

            let correlation = CorrelationContext.root ()

            for make in
                [ (fun () -> ExecutionJournals.file root)
                  (fun () -> ExecutionJournals.ado factory) ] do
                let journal = make ()

                journal.RecordAsync owner (record (Guid.NewGuid()) correlation "actor")
                |> _.Wait()

                journal.Checkpoints.Save owner (checkpoint (Guid.NewGuid()) correlation)
                |> _.Wait()

                journal.SideEffects.Save owner (transition (Guid.NewGuid()) correlation "tool")
                |> _.Wait()

                Assert.AreEqual(1, (make ()).GetHistoryAsync sameOwner |> _.Result |> List.length)

            let path = Path.Combine(root, "execution-journal", key owner + ".jsonl")
            let payloads = File.ReadAllLines path |> Array.toList

            let rows =
                Ado.query factory "SELECT stream, payload FROM nao_events" [] (fun row ->
                    Ado.getString row "stream", Ado.getString row "payload")
                |> _.Result

            Assert.AreEqual(3, rows.Length)

            Assert.IsTrue(
                rows
                |> List.forall (fun (stream, _) -> stream = "execution-journal/" + key owner)
            )

            for payload in payloads @ (rows |> List.map snd) do
                let document = FSharpJson.deserialize<ExecutionJournalDocument> payload
                Assert.AreEqual(1, document.Version)

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
                      "Owner" ] do
                    Assert.IsFalse(payload.Contains secret))

    [<TestMethod>]
    member _.CorruptForeignScopeDoesNotBlockOwnOperations() =
        withStorage (fun root factory ->
            let foreign = owners.[1]

            for make, corrupt in
                [ (fun () -> ExecutionJournals.file root),
                  (fun () ->
                      File.AppendAllText(
                          Path.Combine(root, "execution-journal", key foreign + ".jsonl"),
                          "{invalid\n"
                      ))
                  (fun () -> ExecutionJournals.ado factory),
                  (fun () ->
                      Ado.executeNonQuery
                          factory
                          "UPDATE nao_events SET payload = 'invalid' WHERE stream = @stream"
                          [ "@stream", box ("execution-journal/" + key foreign) ]
                      |> _.GetAwaiter().GetResult()
                      |> ignore) ] do
                let journal = make ()

                journal.RecordAsync foreign (record (Guid.NewGuid()) (CorrelationContext.root ()) "foreign")
                |> _.Wait()

                corrupt ()

                journal.RecordAsync owner (record (Guid.NewGuid()) (CorrelationContext.root ()) "own")
                |> _.Wait()

                Assert.AreEqual(1, (make ()).GetHistoryAsync owner |> _.Result |> List.length)

                Assert.ThrowsExactly<InvalidDataException>(fun () ->
                    journal.GetHistoryAsync foreign |> _.GetAwaiter().GetResult() |> ignore)
                |> ignore)

    [<TestMethod>]
    member _.FailedAppendAndUnsupportedVersionCannotPublishState() =
        let lines = ResizeArray<string>()
        let mutable failAppend = false

        let store =
            { Append =
                fun line ->
                    if failAppend then
                        raise (IOException "write failed")
                    else
                        lines.Add line
              LoadAll = fun () -> List.ofSeq lines }

        let journal = PersistentExecutionJournal.create (fun _ -> store)
        let original = record (Guid.NewGuid()) (CorrelationContext.root ()) "original"
        journal.RecordAsync owner original |> _.Wait()
        failAppend <- true

        Assert.ThrowsExactly<IOException>(fun () ->
            journal.RecordAsync owner (record (Guid.NewGuid()) (CorrelationContext.root ()) "new")
            |> _.GetAwaiter().GetResult())
        |> ignore

        Assert.AreEqual([ original ], journal.GetHistoryAsync owner |> _.Result)
        Assert.IsTrue(journal.DeleteScopeAsync owner |> _.Result |> Result.isError)
        failAppend <- false

        let unsupported: ExecutionJournalDocument =
            { Version = 99
              Event = ExecutionJournalEvent.DeleteScope }

        let unsupportedStore =
            { Append = fun _ -> Assert.Fail("Unsupported streams must reject before append.")
              LoadAll = fun () -> [ FSharpJson.serialize unsupported ] }

        let unsupportedJournal =
            PersistentExecutionJournal.create (fun _ -> unsupportedStore)

        Assert.ThrowsExactly<InvalidDataException>(fun () ->
            unsupportedJournal.GetHistoryAsync owner |> _.GetAwaiter().GetResult() |> ignore)
        |> ignore

        Assert.AreEqual(1, lines.Count)
