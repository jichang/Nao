namespace Nao.Eval.Tests

open System
open System.IO
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval

[<TestClass>]
type EvalArchiveTests() =
    let tempFile () =
        Path.Combine(Path.GetTempPath(), "nao-eval-" + Guid.NewGuid().ToString("N"), "archive.jsonl")

    let dataset owner id createdAt name =
        { EvalDataset.create owner name [ EvalCase.create "case" "input" "expected" ] with
            Id = id
            CreatedAt = createdAt }

    let report owner datasetId id runAt name =
        let run =
            { Id = id
              Owner = owner
              DatasetId = datasetId
              StartedAt = runAt }

        EvalReport.fromResults run name []

    let exercise (make: unit -> EvalArchive) =
        task {
            let ownerA, ownerB = "eval/a", "eval/b"
            let cutoff = DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero)
            let recentCount = Random.Shared.Next(2, 7)
            let sharedDatasetId = Guid.NewGuid()
            let archive = make ()

            for index in 1..recentCount do
                do!
                    archive.SaveDatasetAsync(
                        dataset ownerA (Guid.NewGuid()) (cutoff.AddMinutes(float index)) (sprintf "recent-%d" index)
                    )

            let oldDataset = dataset ownerA (Guid.NewGuid()) (cutoff.AddTicks(-1L)) "old"
            let boundaryDataset = dataset ownerA sharedDatasetId cutoff "boundary"
            let otherDataset = dataset ownerB sharedDatasetId cutoff "other-owner"
            do! archive.SaveDatasetAsync oldDataset
            do! archive.SaveDatasetAsync boundaryDataset
            do! archive.SaveDatasetAsync otherDataset

            do! archive.SaveReportAsync(report ownerA sharedDatasetId (Guid.NewGuid()) (cutoff.AddTicks(-1L)) "old")
            do! archive.SaveReportAsync(report ownerA sharedDatasetId (Guid.NewGuid()) cutoff "boundary")
            do! archive.SaveReportAsync(report ownerA sharedDatasetId (Guid.NewGuid()) (cutoff.AddMinutes 1) "recent")
            do! archive.SaveReportAsync(report ownerB sharedDatasetId (Guid.NewGuid()) cutoff "other-owner")

            let reloaded = make ()
            Assert.AreEqual(Some boundaryDataset, reloaded.GetDatasetAsync ownerA sharedDatasetId |> _.Result)
            Assert.AreEqual(Some otherDataset, reloaded.GetDatasetAsync ownerB sharedDatasetId |> _.Result)

            match! reloaded.DeleteExpiredAsync ownerA cutoff with
            | Error failure -> Assert.Fail failure.Message
            | Ok count -> Assert.AreEqual(2, count)

            let afterExpiry = make ()
            Assert.AreEqual(Some boundaryDataset, afterExpiry.GetDatasetAsync ownerA sharedDatasetId |> _.Result)
            Assert.AreEqual(2, (afterExpiry.GetReportsAsync ownerA sharedDatasetId 10).Result.Length)
            Assert.AreEqual(1, (afterExpiry.GetReportsAsync ownerB sharedDatasetId 10).Result.Length)

            match! afterExpiry.DeleteOwnerAsync ownerB with
            | Error failure -> Assert.Fail failure.Message
            | Ok count -> Assert.AreEqual(2, count)

            match! afterExpiry.DeleteOwnerAsync " " with
            | Ok _ -> Assert.Fail "Blank evaluation owners must be rejected."
            | Error failure -> Assert.AreEqual(PlatformErrorCategory.InvalidInput, failure.Category)

            match! afterExpiry.DeleteOwnerAsync ownerA with
            | Error failure -> Assert.Fail failure.Message
            | Ok count -> Assert.AreEqual(recentCount + 3, count)

            let afterDeletion = make ()
            Assert.AreEqual(None, afterDeletion.GetDatasetAsync ownerA sharedDatasetId |> _.Result)
            Assert.AreEqual(None, afterDeletion.GetDatasetAsync ownerB sharedDatasetId |> _.Result)

            let replacement = dataset ownerA sharedDatasetId (cutoff.AddDays 1) "replacement"
            do! afterDeletion.SaveDatasetAsync replacement
            Assert.AreEqual(Some replacement, (make ()).GetDatasetAsync ownerA sharedDatasetId |> _.Result)
        }

    [<TestMethod>]
    member _.LifecycleParityAcrossBackends() =
        let inMemory = EvalArchives.inMemory ()
        let file = tempFile ()

        let verify name make =
            try
                exercise make |> _.GetAwaiter().GetResult()
            with ex ->
                Assert.Fail(sprintf "%s backend failed: %s" name ex.Message)

        verify "in-memory" (fun () -> inMemory)
        verify "file" (fun () -> EvalArchives.file file)

    [<TestMethod>]
    member _.RejectsResultOutsideReportIdentity() =
        let owner = "eval/owner"
        let datasetId = Guid.NewGuid()
        let run = EvalRun.create owner datasetId

        let mismatchedResult: EvalResult =
            { Id = Guid.NewGuid()
              Owner = "eval/other"
              DatasetId = datasetId
              RunId = run.Id
              ExecutionId = ExecutionId.generate ()
              CaseId = "case"
              ActualOutput = "output"
              Verdict = EvalVerdict.Pass
              Reason = "ok"
              LatencyMs = 1L
              EvaluatorName = "test"
              Timestamp = run.StartedAt
              ExecutionTrace = None }

        let invalidReport = EvalReport.fromResults run "invalid" [ mismatchedResult ]
        let archive = EvalArchives.inMemory ()

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            archive.SaveReportAsync(invalidReport).GetAwaiter().GetResult())
        |> ignore

    [<TestMethod>]
    member _.ResultsAreScopedByOwnerAndExecutionAcrossBackends() =
        let ownerA, ownerB = "eval/a", "eval/b"
        let executionId = ExecutionId.generate ()
        let distractorExecutionId = ExecutionId.generate ()

        let report owner executionId caseId =
            let run = EvalRun.create owner (Guid.NewGuid())

            let result: EvalResult =
                { Id = Guid.NewGuid()
                  Owner = owner
                  DatasetId = run.DatasetId
                  RunId = run.Id
                  ExecutionId = executionId
                  CaseId = caseId
                  ActualOutput = "output"
                  Verdict = EvalVerdict.Pass
                  Reason = "ok"
                  LatencyMs = 1L
                  EvaluatorName = "test"
                  Timestamp = run.StartedAt
                  ExecutionTrace = None }

            EvalReport.fromResults run caseId [ result ]

        let verify make =
            task {
                let archive = make ()
                let expectedA = report ownerA executionId "owner-a"
                let expectedB = report ownerB executionId "owner-b"
                do! archive.SaveReportAsync expectedA
                do! archive.SaveReportAsync(report ownerA distractorExecutionId "distractor")
                do! archive.SaveReportAsync expectedB

                let reloaded = make ()
                let! ownerAResults = reloaded.GetResultsByExecutionAsync ownerA executionId
                let! ownerBResults = reloaded.GetResultsByExecutionAsync ownerB executionId
                let! unknownResults = reloaded.GetResultsByExecutionAsync ownerA (ExecutionId.generate ())
                Assert.AreEqual([ expectedA.Results.Head ], ownerAResults)
                Assert.AreEqual([ expectedB.Results.Head ], ownerBResults)
                Assert.IsTrue(unknownResults.IsEmpty)
            }

        let inMemory = EvalArchives.inMemory ()
        verify (fun () -> inMemory) |> _.GetAwaiter().GetResult()
        let file = tempFile ()
        verify (fun () -> EvalArchives.file file) |> _.GetAwaiter().GetResult()
