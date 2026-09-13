namespace Nao.Eval.Tests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval

[<TestClass>]
type EvalArchiveTests() =
    let scope tenant user group workspace session =
        let principal =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse tenant)
                (UserId.parse user)
                (Some(ServiceId.parse "private-service"))
                [ GroupId.parse "group"; GroupId.parse "other-group" ]
                [ "private-role" ]
                (Map.ofList [ "private-claim", "private-claim-value" ])
                AuthenticationStrength.MultiFactor

        AuthorizationScope.tryCreate
            principal
            (group |> Option.map GroupId.parse)
            (WorkspaceId.parse workspace)
            (session |> Option.map SessionId.parse)
        |> Option.get

    let scopeA = scope "tenant-a" "user" (Some "group") "workspace" (Some "session")
    let scopeB = scope "tenant-b" "user" (Some "group") "workspace" (Some "session")

    let otherScopes =
        [ scopeB
          scope "tenant-a" "other-user" (Some "group") "workspace" (Some "session")
          scope "tenant-a" "user" (Some "other-group") "workspace" (Some "session")
          scope "tenant-a" "user" None "workspace" (Some "session")
          scope "tenant-a" "user" (Some "group") "other-workspace" (Some "session")
          scope "tenant-a" "user" (Some "group") "workspace" (Some "other-session")
          scope "tenant-a" "user" (Some "group") "workspace" None ]

    let tempFile () =
        Path.Combine(Path.GetTempPath(), "nao-eval-" + Guid.NewGuid().ToString("N"), "archive.jsonl")

    let cleanup path =
        if File.Exists path then
            File.Delete path

        let directory =
            Path.GetDirectoryName path
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Archive fixture must have a parent directory.")

        if Directory.Exists directory then
            Directory.Delete directory

    let withArchives verify =
        let memory = EvalArchives.inMemory ()
        verify (fun () -> memory)
        let path = tempFile ()

        try
            verify (fun () -> EvalArchives.file path)
        finally
            cleanup path

    let dataset authorization id createdAt name =
        { EvalDataset.create
              (AuthorizationScope.identity authorization)
              name
              [ EvalCase.create "case" "input" "expected" ] with
            Id = id
            CreatedAt = createdAt }

    let report authorization datasetId id runAt name =
        let run =
            { Id = id
              Owner = AuthorizationScope.identity authorization
              DatasetId = datasetId
              StartedAt = runAt }

        EvalReport.fromResults run name []

    let resultReport authorization datasetId runId executionId =
        let run =
            { EvalRun.create (AuthorizationScope.identity authorization) datasetId with
                Id = runId }

        let result: EvalResult =
            { Id = Guid.NewGuid()
              Owner = run.Owner
              DatasetId = datasetId
              RunId = run.Id
              ExecutionId = executionId
              CaseId = "case"
              ActualOutput = "output"
              Verdict = EvalVerdict.Pass
              Reason = "ok"
              LatencyMs = 1L
              EvaluatorName = "test"
              Timestamp = run.StartedAt
              ExecutionTrace = None }

        EvalReport.fromResults run "report" [ result ]

    [<TestMethod>]
    member _.LifecycleParityAcrossBackends() =
        withArchives (fun make ->
            task {
                let cutoff = DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero)
                let recentCount = Random.Shared.Next(2, 7)
                let sharedDatasetId = Guid.NewGuid()
                let archive = make ()

                for index in 1..recentCount do
                    do!
                        archive.SaveDatasetAsync
                            scopeA
                            (dataset
                                scopeA
                                (Guid.NewGuid())
                                (cutoff.AddMinutes(float index))
                                (sprintf "recent-%d" index))

                let oldDataset = dataset scopeA (Guid.NewGuid()) (cutoff.AddTicks(-1L)) "old"
                let boundaryDataset = dataset scopeA sharedDatasetId cutoff "boundary"
                let otherDataset = dataset scopeB sharedDatasetId cutoff "other-tenant"
                do! archive.SaveDatasetAsync scopeA oldDataset
                do! archive.SaveDatasetAsync scopeA boundaryDataset
                do! archive.SaveDatasetAsync scopeB otherDataset

                do!
                    archive.SaveReportAsync
                        scopeA
                        (report scopeA sharedDatasetId (Guid.NewGuid()) (cutoff.AddTicks(-1L)) "old")

                do! archive.SaveReportAsync scopeA (report scopeA sharedDatasetId (Guid.NewGuid()) cutoff "boundary")

                do!
                    archive.SaveReportAsync
                        scopeA
                        (report scopeA sharedDatasetId (Guid.NewGuid()) (cutoff.AddMinutes 1) "recent")

                do! archive.SaveReportAsync scopeB (report scopeB sharedDatasetId (Guid.NewGuid()) cutoff "other")

                let reloaded = make ()
                Assert.AreEqual(Some boundaryDataset, reloaded.GetDatasetAsync scopeA sharedDatasetId |> _.Result)
                Assert.AreEqual(Some otherDataset, reloaded.GetDatasetAsync scopeB sharedDatasetId |> _.Result)
                let! expired = reloaded.DeleteExpiredAsync scopeA cutoff
                Assert.AreEqual(Ok 2, expired)

                let afterExpiry = make ()
                Assert.AreEqual(Some boundaryDataset, afterExpiry.GetDatasetAsync scopeA sharedDatasetId |> _.Result)
                Assert.AreEqual(2, (afterExpiry.GetReportsAsync scopeA sharedDatasetId 10).Result.Length)
                Assert.AreEqual(1, (afterExpiry.GetReportsAsync scopeB sharedDatasetId 10).Result.Length)
                let! deletedB = afterExpiry.DeleteOwnerAsync scopeB
                Assert.AreEqual(Ok 2, deletedB)
                let! deletedA = afterExpiry.DeleteOwnerAsync scopeA
                Assert.AreEqual(Ok(recentCount + 3), deletedA)

                let afterDeletion = make ()
                Assert.AreEqual(None, afterDeletion.GetDatasetAsync scopeA sharedDatasetId |> _.Result)
                Assert.AreEqual(None, afterDeletion.GetDatasetAsync scopeB sharedDatasetId |> _.Result)
                let replacement = dataset scopeA sharedDatasetId (cutoff.AddDays 1) "replacement"
                do! afterDeletion.SaveDatasetAsync scopeA replacement
                Assert.AreEqual(Some replacement, (make ()).GetDatasetAsync scopeA sharedDatasetId |> _.Result)
            }
            |> _.GetAwaiter().GetResult())

    [<TestMethod>]
    member _.EveryOwnershipDimensionIsEnforcedAcrossBackends() =
        withArchives (fun make ->
            task {
                let archive = make ()
                let datasetId, runId = Guid.NewGuid(), Guid.NewGuid()
                let executionId = ExecutionId.generate ()
                let ownDataset = dataset scopeA datasetId DateTimeOffset.UtcNow "own"
                let ownReport = resultReport scopeA datasetId runId executionId
                do! archive.SaveDatasetAsync scopeA ownDataset
                do! archive.SaveReportAsync scopeA ownReport

                for other in otherScopes do
                    let current = make ()
                    Assert.AreEqual(None, current.GetDatasetAsync other datasetId |> _.Result)
                    Assert.IsTrue((current.GetReportsAsync other datasetId 10).Result.IsEmpty)
                    Assert.IsTrue((current.GetResultsByExecutionAsync other executionId).Result.IsEmpty)

                    let! _ =
                        Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(fun () ->
                            current.SaveDatasetAsync other ownDataset)

                    let! _ =
                        Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(fun () ->
                            current.SaveReportAsync other ownReport)

                    let! expired = current.DeleteExpiredAsync other DateTimeOffset.MaxValue
                    Assert.AreEqual(Ok 0, expired)
                    let! deleted = current.DeleteOwnerAsync other
                    Assert.AreEqual(Ok 0, deleted)

                    let otherDataset = dataset other datasetId DateTimeOffset.UtcNow "other"
                    let otherReport = resultReport other datasetId runId executionId
                    do! current.SaveDatasetAsync other otherDataset
                    do! current.SaveReportAsync other otherReport
                    let reloaded = make ()
                    Assert.AreEqual(Some otherDataset, reloaded.GetDatasetAsync other datasetId |> _.Result)
                    Assert.AreEqual([ otherReport ], reloaded.GetReportsAsync other datasetId 10 |> _.Result)

                    Assert.AreEqual(
                        otherReport.Results,
                        reloaded.GetResultsByExecutionAsync other executionId |> _.Result
                    )

                    Assert.AreEqual(Some ownDataset, reloaded.GetDatasetAsync scopeA datasetId |> _.Result)
                    Assert.AreEqual([ ownReport ], reloaded.GetReportsAsync scopeA datasetId 10 |> _.Result)

                    Assert.AreEqual(
                        ownReport.Results,
                        reloaded.GetResultsByExecutionAsync scopeA executionId |> _.Result
                    )
            }
            |> _.GetAwaiter().GetResult())

    [<TestMethod>]
    member _.RejectsResultsOutsideReportIdentityAcrossBackends() =
        withArchives (fun make ->
            let archive = make ()

            let valid =
                resultReport scopeA (Guid.NewGuid()) (Guid.NewGuid()) (ExecutionId.generate ())

            let result = valid.Results.Head

            for invalid in
                [ { result with
                      Owner = AuthorizationScope.identity scopeB }
                  { result with
                      DatasetId = Guid.NewGuid() }
                  { result with RunId = Guid.NewGuid() } ] do
                Assert.ThrowsExactlyAsync<ArgumentException>(fun () ->
                    archive.SaveReportAsync scopeA { valid with Results = [ invalid ] })
                |> _.Wait()

            Assert.IsTrue(((make ()).GetReportsAsync scopeA valid.DatasetId 10).Result.IsEmpty))

    [<TestMethod>]
    member _.ExecutionLookupFiltersOtherExecutionsWithinTheSameScope() =
        withArchives (fun make ->
            let archive = make ()

            let expected =
                resultReport scopeA (Guid.NewGuid()) (Guid.NewGuid()) (ExecutionId.generate ())

            let distractor =
                resultReport scopeA (Guid.NewGuid()) (Guid.NewGuid()) (ExecutionId.generate ())

            archive.SaveReportAsync scopeA expected |> _.GetAwaiter().GetResult()
            archive.SaveReportAsync scopeA distractor |> _.GetAwaiter().GetResult()
            let reloaded = make ()

            Assert.AreEqual(
                expected.Results,
                reloaded.GetResultsByExecutionAsync scopeA expected.Results.Head.ExecutionId
                |> _.Result
            )

            Assert.IsTrue((reloaded.GetResultsByExecutionAsync scopeA (ExecutionId.generate ())).Result.IsEmpty))

    [<TestMethod>]
    member _.ArchivePersistsOwnershipWithoutAuthenticationEvidence() =
        let path = tempFile ()

        try
            let archive = EvalArchives.file path
            let record = dataset scopeA (Guid.NewGuid()) DateTimeOffset.UtcNow "dataset"
            archive.SaveDatasetAsync scopeA record |> _.GetAwaiter().GetResult()

            archive.SaveReportAsync scopeA (resultReport scopeA record.Id (Guid.NewGuid()) (ExecutionId.generate ()))
            |> _.GetAwaiter().GetResult()

            archive.DeleteOwnerAsync scopeA |> _.GetAwaiter().GetResult() |> ignore
            let content = File.ReadAllText path
            StringAssert.Contains(content, "tenant-a")

            for excluded in
                [ "private-service"
                  "private-role"
                  "private-claim"
                  "MultiFactor"
                  "Principal" ] do
                Assert.IsFalse(content.Contains excluded, sprintf "Archive leaked %s." excluded)
        finally
            cleanup path

    [<TestMethod>]
    member _.PersistenceFailureDoesNotMutateLiveState() =
        let path = tempFile ()
        let backup = path + ".backup"

        try
            let archive = EvalArchives.file path
            let original = dataset scopeA (Guid.NewGuid()) DateTimeOffset.UtcNow "original"
            archive.SaveDatasetAsync scopeA original |> _.GetAwaiter().GetResult()
            File.Move(path, backup)
            Directory.CreateDirectory path |> ignore

            try
                Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(fun () ->
                    archive.SaveDatasetAsync scopeA { original with Name = "uncommitted" })
                |> _.Wait()

                let deleted = archive.DeleteOwnerAsync scopeA |> _.GetAwaiter().GetResult()
                Assert.IsTrue(Result.isError deleted)
                Assert.AreEqual(Some original, archive.GetDatasetAsync scopeA original.Id |> _.Result)
            finally
                Directory.Delete path
                File.Move(backup, path)

            Assert.AreEqual(Some original, (EvalArchives.file path).GetDatasetAsync scopeA original.Id |> _.Result)
        finally
            if File.Exists backup then
                File.Delete backup

            cleanup path
