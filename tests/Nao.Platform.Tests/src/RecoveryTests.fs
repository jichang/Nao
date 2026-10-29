namespace Nao.Platform.Tests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Platform

[<TestClass>]
type RecoveryTests() =
    let hash character = String(character, 64)

    let record kind id references =
        RecoveryRecord.create kind "tenant/workspace" id (hash 'a') references

    let reference kind id = RecoveryReference.create kind id

    let completeManifest capturedAt =
        let records =
            [ record RecoveryDataKind.PermissionGrant "grant-1" []
              record RecoveryDataKind.Policy "policy-1" []
              record RecoveryDataKind.KnowledgeSource "source-1" []
              record
                  RecoveryDataKind.KnowledgeRecord
                  "knowledge-1"
                  [ reference RecoveryDataKind.KnowledgeSource "source-1" ]
              record RecoveryDataKind.Memory "memory-1" []
              record RecoveryDataKind.Artifact "artifact-1" []
              record RecoveryDataKind.Trace "trace-1" []
              record RecoveryDataKind.Audit "audit-1" []
              record RecoveryDataKind.ExecutionJournal "journal-1" []
              record RecoveryDataKind.EvaluationDataset "dataset-1" []
              record
                  RecoveryDataKind.EvaluationReport
                  "report-1"
                  [ reference RecoveryDataKind.EvaluationDataset "dataset-1" ]
              record
                  RecoveryDataKind.Session
                  "session-1"
                  [ reference RecoveryDataKind.PermissionGrant "grant-1"
                    reference RecoveryDataKind.Policy "policy-1"
                    reference RecoveryDataKind.Memory "memory-1"
                    reference RecoveryDataKind.Artifact "artifact-1"
                    reference RecoveryDataKind.Trace "trace-1"
                    reference RecoveryDataKind.ExecutionJournal "journal-1" ]
              record
                  RecoveryDataKind.VectorIndex
                  "vector-1"
                  [ reference RecoveryDataKind.KnowledgeRecord "knowledge-1" ]
              record
                  RecoveryDataKind.LexicalIndex
                  "lexical-1"
                  [ reference RecoveryDataKind.KnowledgeRecord "knowledge-1" ]
              record RecoveryDataKind.GraphIndex "graph-1" [ reference RecoveryDataKind.KnowledgeRecord "knowledge-1" ] ]

        BackupManifest.create "backup-1" "region-a" capturedAt (capturedAt - TimeSpan.FromMinutes 1.0) records

    [<TestMethod; TestCategory("RestoreDrill")>]
    member _.RestoreDrillVerifiesCrossStoreReferencesAndSchedulesDerivedRebuilds() : Task =
        task {
            let now = DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero)
            let manifest = completeManifest now

            let plan =
                RestorePlan.create
                    manifest
                    (RecoveryDeletionLedger.create now [])
                    "region-a"
                    (Set.ofList [ "region-a" ])

            Assert.AreEqual(12, RestorePlan.authoritativeRecords plan |> List.length)
            Assert.AreEqual(3, RestorePlan.derivedRebuilds plan |> List.length)

            let restored = ConcurrentDictionary<string * RecoveryDataKind * string, string>()

            let target =
                { RestoreAuthoritativeAsync =
                    fun record _ ->
                        restored[(RecoveryRecord.owner record, RecoveryRecord.kind record, RecoveryRecord.id record)] <-
                            RecoveryRecord.contentSha256 record

                        Task.CompletedTask
                  RebuildDerivedAsync =
                    fun rebuild _ ->
                        for source in DerivedRebuild.sourceReferences rebuild do
                            Assert.IsTrue(
                                restored.ContainsKey(
                                    DerivedRebuild.owner rebuild,
                                    RecoveryReference.kind source,
                                    RecoveryReference.id source
                                )
                            )

                        restored[(DerivedRebuild.owner rebuild, DerivedRebuild.kind rebuild, DerivedRebuild.id rebuild)] <-
                            DerivedRebuild.expectedContentSha256 rebuild

                        Task.CompletedTask
                  ReadContentSha256Async =
                    fun owner kind id _ ->
                        match restored.TryGetValue((owner, kind, id)) with
                        | true, value -> Some value
                        | false, _ -> None
                        |> Task.FromResult }

            let! execution = RecoveryCoordinator.restoreAsync target plan CancellationToken.None

            Assert.AreEqual(12, RecoveryExecutionReport.restoredAuthoritativeRecords execution)

            Assert.AreEqual(3, RecoveryExecutionReport.rebuiltDerivedRecords execution)
            Assert.AreEqual(15, RecoveryExecutionReport.verifiedRecords execution)

            let report =
                RestoreDrill.run
                    (RecoveryObjectives.forProfile DeploymentProfile.ClusteredProduction)
                    now
                    (now + TimeSpan.FromMinutes 10.0)
                    (now + TimeSpan.FromMinutes 2.0)
                    plan

            Assert.IsTrue(RestoreDrill.objectiveMet report)
            Assert.AreEqual(12, RestoreDrill.restoredAuthoritativeRecords report)
            Assert.AreEqual(3, RestoreDrill.scheduledRebuilds report)
        }

    [<TestMethod>]
    member _.DeletionLedgerPreventsBackupResurrectionEvenUnderLegalHold() =
        let now = DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero)
        let manifest = completeManifest now

        let deletions =
            [ RecoveryDeletion.create
                  "tenant/workspace"
                  RecoveryDataKind.Session
                  "session-1"
                  now
                  (Some(now + TimeSpan.FromDays 30.0))
                  (Some "regulatory-case")
              RecoveryDeletion.create "tenant/workspace" RecoveryDataKind.PermissionGrant "grant-1" now None None ]

        let plan =
            RestorePlan.create
                manifest
                (RecoveryDeletionLedger.create now deletions)
                "region-a"
                (Set.ofList [ "region-a" ])

        let restoredIds =
            RestorePlan.authoritativeRecords plan
            |> List.map RecoveryRecord.id
            |> Set.ofList

        Assert.IsFalse(restoredIds.Contains "session-1")
        Assert.IsFalse(restoredIds.Contains "grant-1")
        Assert.AreEqual(2, RestorePlan.appliedDeletions plan |> List.length)

    [<TestMethod>]
    member _.IncompleteDeletionCascadeFailsReferentialConsistency() =
        let now = DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero)

        let deletion =
            RecoveryDeletion.create "tenant/workspace" RecoveryDataKind.PermissionGrant "grant-1" now None None

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            RestorePlan.create
                (completeManifest now)
                (RecoveryDeletionLedger.create now [ deletion ])
                "region-a"
                (Set.ofList [ "region-a" ])
            |> ignore)
        |> ignore

    [<TestMethod>]
    member _.ManifestRejectsCrossOwnerAndDanglingReferences() =
        let now = DateTimeOffset.UtcNow

        let invalid =
            RecoveryRecord.create
                RecoveryDataKind.Session
                "tenant/other-workspace"
                "session"
                (hash 'b')
                [ reference RecoveryDataKind.Policy "policy-1" ]

        let policy = record RecoveryDataKind.Policy "policy-1" []

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            BackupManifest.create "backup" "region-a" now now [ invalid; policy ] |> ignore)
        |> ignore

    [<TestMethod>]
    member _.RecoveryObjectivesAreExplicitForEveryDeploymentProfile() =
        Assert.AreEqual(RecoveryObjective.BestEffort, RecoveryObjectives.forProfile DeploymentProfile.LocalDevelopment)

        Assert.AreEqual(
            RecoveryObjective.ControlPlaneOwned,
            RecoveryObjectives.forProfile DeploymentProfile.IsolatedWorker
        )

        match RecoveryObjectives.forProfile DeploymentProfile.ClusteredProduction with
        | RecoveryObjective.Bounded(dataLoss, recoveryTime) ->
            Assert.AreEqual(TimeSpan.FromMinutes 5.0, dataLoss)
            Assert.AreEqual(TimeSpan.FromHours 1.0, recoveryTime)
        | objective -> Assert.Fail $"Unexpected clustered objective {objective}."
