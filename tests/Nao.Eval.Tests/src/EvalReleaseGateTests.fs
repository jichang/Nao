namespace Nao.Eval.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval

[<TestClass>]
type EvalReleaseGateTests() =
    let adapterComponent name =
        EvalComponentIdentity.create name "1" "{}"

    let configuration =
        { Agent = adapterComponent "agent"
          Prompt = adapterComponent "prompt"
          ResponseProtocol = adapterComponent "protocol"
          Tools = []
          Provider = Some(adapterComponent "provider")
          Model = Some(adapterComponent "model")
          Harness = adapterComponent "harness"
          Policies = [ adapterComponent "policy" ]
          KnowledgeIndexes = []
          Evaluators = [ adapterComponent "evaluator" ] }

    let recordedAt = DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero)
    let datasetId = Guid.NewGuid()
    let contentHash = String.replicate 64 "a"

    let scope user =
        SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse user) []
        |> fun principal ->
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
            |> Option.get

    let qualityKey = EvalReleaseMetricKey.overall EvalReleaseMetric.Quality

    let sourceKey =
        EvalReleaseMetricKey.segment EvalSegmentDimension.Source "authoritative-docs" EvalReleaseMetric.RetrievalQuality

    let snapshot measurements failures =
        EvalReleaseSnapshot.create
            (Guid.NewGuid())
            datasetId
            1
            contentHash
            configuration
            measurements
            failures
            recordedAt

    let config =
        EvalReleaseGateConfig.create
            [ EvalMetricThreshold.create qualityKey 0.80m 0.02m 30 0.95
              EvalMetricThreshold.create sourceKey 0.75m 0.03m 30 0.95 ]

    let passingSnapshot () =
        snapshot
            [ qualityKey, EvalMetricMeasurement.proportion 96 100
              sourceKey, EvalMetricMeasurement.proportion 95 100 ]
            []

    let promoteInitial gateResult =
        let proposalId, registry =
            EvalBaselineRegistry.propose
                (scope "author")
                "Initial candidate."
                recordedAt
                gateResult
                EvalBaselineRegistry.empty

        let registry =
            EvalBaselineRegistry.review
                (scope "reviewer")
                EvalBaselineReviewDecision.Approved
                "Evidence reviewed."
                recordedAt
                proposalId
                registry

        EvalBaselineRegistry.promote (scope "promoter") "Initial release baseline." recordedAt proposalId registry
        |> fst

    [<TestMethod>]
    [<TestCategory("EvalFast")>]
    [<TestCategory("EvalFull")>]
    member _.``Threshold confidence sample and segment regressions fail closed``() =
        let baselineResult = EvalReleaseGate.evaluate config None (passingSnapshot ())
        let baseline = promoteInitial baselineResult

        let candidate =
            snapshot
                [ qualityKey, EvalMetricMeasurement.proportion 29 30
                  sourceKey, EvalMetricMeasurement.proportion 35 50 ]
                []

        let result = EvalReleaseGate.evaluate config (Some baseline) candidate
        Assert.IsFalse(EvalReleaseGateResult.passed result)

        let source = result.Checks |> List.find (fun check -> check.Key = sourceKey)
        Assert.IsTrue(source.Failures |> List.contains EvalGateFailure.BelowAbsoluteThreshold)
        Assert.IsTrue(source.Failures |> List.contains EvalGateFailure.RegressedBeyondTolerance)

        let lowSamples =
            snapshot
                [ qualityKey, EvalMetricMeasurement.proportion 10 10
                  sourceKey, EvalMetricMeasurement.proportion 10 10 ]
                []
            |> EvalReleaseGate.evaluate config None

        Assert.IsTrue(
            lowSamples.Checks
            |> List.collect _.Failures
            |> List.contains EvalGateFailure.InsufficientSamples
        )

    [<TestMethod>]
    [<TestCategory("EvalFast")>]
    [<TestCategory("EvalFull")>]
    member _.``Correctness and infrastructure failures remain distinct``() =
        let candidate =
            snapshot
                [ qualityKey, EvalMetricMeasurement.proportion 96 100
                  sourceKey, EvalMetricMeasurement.proportion 95 100 ]
                [ { CaseId = "wrong-answer"
                    Classification = EvalFailureClass.Correctness
                    Reason = "Expected answer did not match." }
                  { CaseId = "provider-timeout"
                    Classification = EvalFailureClass.Infrastructure
                    Reason = "Provider was unavailable." } ]

        let result = EvalReleaseGate.evaluate config None candidate
        Assert.IsFalse(EvalReleaseGateResult.passed result)
        Assert.AreEqual("wrong-answer", result.CorrectnessFailures |> List.exactlyOne |> _.CaseId)
        Assert.AreEqual("provider-timeout", result.InfrastructureFailures |> List.exactlyOne |> _.CaseId)

    [<TestMethod>]
    [<TestCategory("EvalFast")>]
    [<TestCategory("EvalFull")>]
    member _.``Canonical reports project metrics and typed failures directly``() =
        let owner = AuthorizationScope.identity (scope "report-owner")

        let run =
            { Id = Guid.NewGuid()
              Owner = owner
              DatasetId = datasetId
              DatasetRevision = 1
              DatasetContentSha256 = contentHash
              Configuration = configuration
              Reproducibility =
                { RandomSeed = 1
                  Deterministic = true
                  Scheduling =
                    { MaximumParallelism = 1
                      StopOnFirstFailure = false }
                  Mode = "pinned-mock"
                  FixtureSchemaVersion = None
                  FixtureFormatVersion = None }
              StartedAt = recordedAt }

        let result caseId verdict outcome latency =
            { Id = Guid.NewGuid()
              Owner = owner
              DatasetId = datasetId
              RunId = run.Id
              ExecutionId = ExecutionId.parse (Guid.NewGuid().ToString("N"))
              CaseId = caseId
              ActualOutput = ""
              Verdict = verdict
              Reason = caseId
              LatencyMs = latency
              EvaluatorName = "evaluator"
              EvaluatorAuthority = EvaluatorAuthority.Deterministic
              RationaleIsUntrusted = false
              Outcome = outcome
              Timestamp = recordedAt
              ExecutionTrace = None }

        let report =
            [ result "pass" EvalVerdict.Pass EvalResultOutcome.Succeeded 10L
              result "provider-failure" EvalVerdict.Fail EvalResultOutcome.InfrastructureFailure 30L ]
            |> EvalReport.fromResults run "release"

        let projected = EvalReleaseProjection.fromReport [] report
        let metrics = EvalReleaseSnapshot.metrics projected

        Assert.IsTrue(
            metrics
            |> Map.containsKey (EvalReleaseMetricKey.overall EvalReleaseMetric.Quality)
        )

        Assert.IsTrue(
            metrics
            |> Map.containsKey (EvalReleaseMetricKey.overall EvalReleaseMetric.SuccessRate)
        )

        Assert.AreEqual(
            "provider-failure",
            projected
            |> EvalReleaseSnapshot.infrastructureFailures
            |> List.exactlyOne
            |> _.CaseId
        )

    [<TestMethod>]
    [<TestCategory("EvalFast")>]
    [<TestCategory("EvalFull")>]
    member _.``Promotion rollback and failure retention are independently audited``() =
        let author = scope "author"
        let reviewer = scope "reviewer"
        let promoter = scope "promoter"

        let failedCandidate =
            snapshot
                [ qualityKey, EvalMetricMeasurement.proportion 50 100
                  sourceKey, EvalMetricMeasurement.proportion 95 100 ]
                [ { CaseId = "historical-failure"
                    Classification = EvalFailureClass.Correctness
                    Reason = "Historical regression." } ]
            |> EvalReleaseGate.evaluate config None

        let _, registry =
            EvalBaselineRegistry.propose
                author
                "Capture failed candidate."
                recordedAt
                failedCandidate
                EvalBaselineRegistry.empty

        let passing = EvalReleaseGate.evaluate config None (passingSnapshot ())

        let firstProposal, registry =
            EvalBaselineRegistry.propose author "Initial baseline candidate." recordedAt passing registry

        let registry =
            EvalBaselineRegistry.review
                reviewer
                EvalBaselineReviewDecision.Approved
                "Evidence reviewed."
                (recordedAt.AddMinutes 1.0)
                firstProposal
                registry

        let first, registry =
            EvalBaselineRegistry.promote
                promoter
                "Promote initial baseline."
                (recordedAt.AddMinutes 2.0)
                firstProposal
                registry

        Assert.IsTrue(
            EvalReleaseBaseline.retainedFailedCaseIds first
            |> Set.contains "historical-failure"
        )

        let secondProposal, registry =
            EvalBaselineRegistry.propose author "Replacement candidate." (recordedAt.AddDays 1.0) passing registry

        let registry =
            EvalBaselineRegistry.review
                reviewer
                EvalBaselineReviewDecision.Approved
                "Replacement reviewed."
                (recordedAt.AddDays 1.0)
                secondProposal
                registry

        let second, registry =
            EvalBaselineRegistry.promote
                promoter
                "Promote replacement."
                (recordedAt.AddDays 1.0)
                secondProposal
                registry

        Assert.AreEqual(Some(EvalReleaseBaseline.id first), EvalReleaseBaseline.previousBaselineId second)

        let registry =
            EvalBaselineRegistry.rollback
                promoter
                "Replacement regressed in production."
                (recordedAt.AddDays 2.0)
                (EvalReleaseBaseline.id first)
                registry

        Assert.AreEqual(
            Some(EvalReleaseBaseline.id first),
            EvalBaselineRegistry.active registry |> Option.map EvalReleaseBaseline.id
        )

        Assert.AreEqual(8, EvalBaselineRegistry.audit registry |> List.length)

        Assert.IsTrue(
            EvalBaselineRegistry.audit registry
            |> List.exists (fun entry -> entry.Action = EvalBaselineAuditAction.RolledBack)
        )

    [<TestMethod>]
    [<TestCategory("EvalFast")>]
    [<TestCategory("EvalFull")>]
    member _.``Release artifacts are reviewable and contain gate evidence``() =
        let result = EvalReleaseGate.evaluate config None (passingSnapshot ())
        let json = EvalReleaseArtifact.serialize result
        StringAssert.Contains(json, "\"passed\": true")
        StringAssert.Contains(json, "\"metric\": \"overall:quality\"")
