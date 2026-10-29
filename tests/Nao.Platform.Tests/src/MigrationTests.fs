namespace Nao.Platform.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Platform

[<TestClass; TestCategory("Migration")>]
type MigrationTests() =
    let now = DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero)

    let preflight () =
        MigrationPreflight.create "schema-v1-before" "schema-v1-after" "verified-backup" now 0 2048L 1024L

    let step id apply rollback =
        MigrationStep.create
            id
            (fun _ ->
                apply ()
                Task.CompletedTask)
            (fun _ ->
                rollback ()
                Task.CompletedTask)

    [<TestMethod>]
    member _.PreflightFailsWithoutWriterFencingOrCapacity() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            MigrationPreflight.create "before" "after" "backup" now 1 2048L 1024L |> ignore)
        |> ignore

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            MigrationPreflight.create "before" "after" "backup" now 0 512L 1024L |> ignore)
        |> ignore

    [<TestMethod>]
    member _.FailedMigrationResumesAfterLastDurableStep() : Task =
        task {
            let applied = ResizeArray<string>()
            let mutable secondAttempt = false

            let first = step "first" (fun () -> applied.Add "first") ignore

            let second =
                MigrationStep.create
                    "second"
                    (fun _ ->
                        task {
                            if not secondAttempt then
                                secondAttempt <- true
                                invalidOp "transient migration failure"

                            applied.Add "second"
                        }
                        :> Task)
                    (fun _ -> Task.CompletedTask)

            let plan = MigrationPlan.create "migration-1" (preflight ()) [ first; second ]
            let store = MigrationCheckpointStores.inMemory ()

            let! _ =
                Assert.ThrowsExactlyAsync<InvalidOperationException>(fun () ->
                    MigrationRunner.runAsync now store plan CancellationToken.None :> Task)

            let! failed = store.LoadAsync "migration-1" CancellationToken.None
            Assert.IsTrue(failed.IsSome)
            Assert.AreEqual(MigrationStatus.Failed, failed.Value |> MigrationCheckpoint.status)
            CollectionAssert.AreEqual([| "first" |], failed.Value |> MigrationCheckpoint.completedSteps |> List.toArray)

            let! completed = MigrationRunner.runAsync (now + TimeSpan.FromMinutes 1.0) store plan CancellationToken.None

            Assert.AreEqual(MigrationStatus.Completed, MigrationCheckpoint.status completed)
            CollectionAssert.AreEqual([| "first"; "second" |], applied.ToArray())
        }

    [<TestMethod>]
    member _.RollbackRunsCompletedStepsInReverseAndIsDurable() : Task =
        task {
            let rolledBack = ResizeArray<string>()

            let plan =
                MigrationPlan.create
                    "migration-rollback"
                    (preflight ())
                    [ step "first" ignore (fun () -> rolledBack.Add "first")
                      step "second" ignore (fun () -> rolledBack.Add "second") ]

            let store = MigrationCheckpointStores.inMemory ()

            let! _ = MigrationRunner.runAsync now store plan CancellationToken.None

            let! checkpoint =
                MigrationRunner.rollbackAsync (now + TimeSpan.FromMinutes 1.0) store plan CancellationToken.None

            Assert.AreEqual(MigrationStatus.RolledBack, MigrationCheckpoint.status checkpoint)
            CollectionAssert.AreEqual([| "second"; "first" |], rolledBack.ToArray())

            CollectionAssert.AreEqual(
                [| "second"; "first" |],
                checkpoint |> MigrationCheckpoint.rolledBackSteps |> List.toArray
            )
        }

    [<TestMethod>]
    member _.CheckpointCannotResumeAChangedPlan() : Task =
        task {
            let store = MigrationCheckpointStores.inMemory ()

            let original =
                MigrationPlan.create "stable-id" (preflight ()) [ step "first" ignore ignore ]

            let changed =
                MigrationPlan.create "stable-id" (preflight ()) [ step "different" ignore ignore ]

            let! _ = MigrationRunner.runAsync now store original CancellationToken.None

            let! _ =
                Assert.ThrowsExactlyAsync<InvalidOperationException>(fun () ->
                    MigrationRunner.runAsync now store changed CancellationToken.None :> Task)

            return ()
        }
