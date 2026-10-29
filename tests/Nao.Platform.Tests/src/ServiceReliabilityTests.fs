namespace Nao.Platform.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Platform

[<TestClass>]
type ServiceReliabilityTests() =
    let policy =
        SloExclusionPolicy.create
            "production-exclusions-v1"
            (Set.ofList
                [ SloExclusionCategory.PlannedMaintenance
                  SloExclusionCategory.CustomerCancellation
                  SloExclusionCategory.InvalidCustomerInput ])
            "Exclude only explicitly classified non-platform demand and approved maintenance."

    let objective =
        ServiceLevelObjective.create
            "cluster.agent.availability"
            DeploymentProfile.ClusteredProduction
            ServiceOperation.AgentExecution
            ServiceIndicator.Availability
            (ServiceThreshold.Minimum 1.0)
            0.99
            (TimeSpan.FromHours 1.0)
            (Some policy)

    let at = DateTimeOffset(2026, 10, 28, 3, 0, 0, TimeSpan.Zero)

    [<TestMethod>]
    member _.ReportsErrorBudgetAndDistinguishesFailureDomains() =
        let samples =
            [ for index in 1..98 do
                  ReliabilitySample.create
                      "cluster.agent.availability"
                      (at.AddMinutes(float -index / 10.0))
                      1.0
                      None
                      ReliabilitySampleDisposition.Eligible

              ReliabilitySample.create
                  "cluster.agent.availability"
                  (at.AddMinutes -1.0)
                  0.0
                  (Some ReliabilityFailureDomain.Provider)
                  ReliabilitySampleDisposition.Eligible

              ReliabilitySample.create
                  "cluster.agent.availability"
                  (at.AddMinutes -2.0)
                  0.0
                  (Some ReliabilityFailureDomain.Storage)
                  ReliabilitySampleDisposition.Eligible

              ReliabilitySample.create
                  "cluster.agent.availability"
                  (at.AddMinutes -3.0)
                  0.0
                  (Some ReliabilityFailureDomain.CustomerInput)
                  (ReliabilitySampleDisposition.Excluded(
                      SloExclusionCategory.InvalidCustomerInput,
                      "production-exclusions-v1",
                      "request did not satisfy the published schema"
                  )) ]

        let report = ServiceLevelReport.evaluate objective at samples

        Assert.AreEqual(100, ServiceLevelReport.eligibleCount report)
        Assert.AreEqual(98, ServiceLevelReport.compliantCount report)
        Assert.AreEqual(2, ServiceLevelReport.violationCount report)
        Assert.AreEqual(1, ServiceLevelReport.excludedCount report)
        Assert.AreEqual(2.0, ServiceLevelReport.errorBudgetConsumed report |> Option.get, 0.0001)

        Assert.AreEqual(
            1,
            ServiceLevelReport.violationsByDomain report
            |> Map.find ReliabilityFailureDomain.Provider
        )

        Assert.AreEqual(
            1,
            ServiceLevelReport.violationsByDomain report
            |> Map.find ReliabilityFailureDomain.Storage
        )

    [<TestMethod>]
    member _.RejectsUndocumentedExclusions() =
        let sample =
            ReliabilitySample.create
                "cluster.agent.availability"
                at
                0.0
                None
                (ReliabilitySampleDisposition.Excluded(
                    SloExclusionCategory.PlannedMaintenance,
                    "unapproved-policy",
                    "maintenance"
                ))

        Assert.ThrowsExactly<ArgumentException>(fun () -> ServiceLevelReport.evaluate objective at [ sample ] |> ignore)
        |> ignore

    [<TestMethod>]
    member _.EmptyWindowsRemainUnmeasuredAndViolationsRequireAttribution() =
        let emptyReport = ServiceLevelReport.evaluate objective at []
        Assert.IsTrue(ServiceLevelReport.compliance emptyReport |> Option.isNone)
        Assert.IsTrue(ServiceLevelReport.errorBudgetConsumed emptyReport |> Option.isNone)

        let unattributedViolation =
            ReliabilitySample.create "cluster.agent.availability" at 0.0 None ReliabilitySampleDisposition.Eligible

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            ServiceLevelReport.evaluate objective at [ unattributedViolation ] |> ignore)
        |> ignore

    [<TestMethod>]
    member _.BurnAlertCarriesDiagnosticsRunbookRollbackAndDominantDomain() =
        let samples =
            [ ReliabilitySample.create
                  "cluster.agent.availability"
                  at
                  0.0
                  (Some ReliabilityFailureDomain.Provider)
                  ReliabilitySampleDisposition.Eligible
              ReliabilitySample.create
                  "cluster.agent.availability"
                  (at.AddSeconds -1.0)
                  0.0
                  (Some ReliabilityFailureDomain.Provider)
                  ReliabilitySampleDisposition.Eligible
              ReliabilitySample.create
                  "cluster.agent.availability"
                  (at.AddSeconds -2.0)
                  0.0
                  (Some ReliabilityFailureDomain.Platform)
                  ReliabilitySampleDisposition.Eligible ]

        let report = ServiceLevelReport.evaluate objective at samples

        let rule =
            ReliabilityAlertRule.create
                "agent-availability-fast-burn"
                ReliabilityAlertSeverity.Critical
                2
                1.0
                "trace:nao.execution"
                "operations-runbooks.html#provider-outage"
                (Some "disable the failing provider route or roll back the responsible release")

        let alert = ReliabilityAlert.evaluate rule report |> Option.get

        Assert.AreEqual(ReliabilityFailureDomain.Provider, ReliabilityAlert.dominantFailureDomain alert |> Option.get)
        Assert.AreEqual("trace:nao.execution", ReliabilityAlert.diagnosticsReference alert)
        Assert.IsTrue(ReliabilityAlert.rollbackProcedure alert |> Option.isSome)

    [<TestMethod>]
    member _.CatalogDefinesEveryIndicatorForEachOperationAndProfile() =
        let profiles =
            [ DeploymentProfile.LocalDevelopment
              DeploymentProfile.SingleNodeProduction
              DeploymentProfile.ClusteredProduction
              DeploymentProfile.IsolatedWorker ]

        let operations =
            [ ServiceOperation.AgentExecution
              ServiceOperation.KnowledgeQuery
              ServiceOperation.KnowledgeIngestion
              ServiceOperation.AdministrativeMutation
              ServiceOperation.RecoveryRestore ]

        for profile in profiles do
            for operation in operations do
                let objectives = ServiceObjectiveCatalog.objectives profile operation (Some policy)
                Assert.AreEqual(6, objectives.Length)

                Assert.AreEqual(
                    6,
                    objectives
                    |> List.map ServiceLevelObjective.indicator
                    |> List.distinct
                    |> List.length
                )

    [<TestMethod>]
    member _.IncidentReviewCannotCloseWithoutRegressionEvidence() =
        Assert.ThrowsExactly<ArgumentException>(fun () ->
            IncidentReviewRecord.create
                "incident-2026-10-28"
                at
                ReliabilityFailureDomain.Platform
                "A rollout exhausted worker admission."
                [ "Bound admission before deployment promotion." ]
                []
            |> ignore)
        |> ignore

        let review =
            IncidentReviewRecord.create
                "incident-2026-10-28"
                at
                ReliabilityFailureDomain.Platform
                "A rollout exhausted worker admission."
                [ "Bound admission before deployment promotion." ]
                [ "ServiceReliabilityTests.BurnAlertCarriesDiagnosticsRunbookRollbackAndDominantDomain" ]

        Assert.AreEqual(ReliabilityFailureDomain.Platform, IncidentReviewRecord.failureDomain review)
        Assert.AreEqual(1, IncidentReviewRecord.regressionTests review |> List.length)
