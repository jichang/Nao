namespace Nao.Eval.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval

[<TestClass>]
type ProductionEvaluationTests() =
    let timestamp = DateTimeOffset(2026, 10, 19, 5, 0, 0, TimeSpan.Zero)

    let scope tenant user =
        SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse user) []
        |> fun principal ->
            AuthorizationScope.tryCreate
                principal
                None
                (WorkspaceId.parse "workspace")
                (Some(SessionId.parse "session"))
            |> Option.get

    let owner = scope "tenant" "owner"
    let reviewer = scope "tenant" "reviewer"
    let foreignReviewer = scope "foreign" "reviewer"

    let adapterComponent name configuration =
        EvalComponentIdentity.create name "1" configuration

    let configuration suffix =
        { Agent = adapterComponent "agent" suffix
          Prompt = adapterComponent "prompt" suffix
          ResponseProtocol = adapterComponent "protocol" suffix
          Tools = []
          Provider = Some(adapterComponent "provider" suffix)
          Model = Some(adapterComponent "model" suffix)
          Harness = adapterComponent "harness" suffix
          Policies = [ adapterComponent "policy" suffix ]
          KnowledgeIndexes = [ adapterComponent "index" suffix ]
          Evaluators = [ adapterComponent "evaluator" suffix ] }

    let redactor =
        { Identity = adapterComponent "production-redactor" "secret replacement"
          Redact =
            fun text ->
                let redacted = text.Replace("secret", "[redacted]")
                let removed = if redacted = text then 0 else 1
                EvalProductionRedaction.create redacted removed [ "replace-secret-v1" ] }

    let policy =
        EvalProductionSamplingPolicy.create
            "negative-production-signals"
            1m
            "private-sampling-salt"
            (String.replicate 64 "a")
            (TimeSpan.FromDays 30.0)
            (Set.ofList
                [ EvalProductionSignal.Failure
                  EvalProductionSignal.Denial
                  EvalProductionSignal.Retry
                  EvalProductionSignal.Abandonment
                  EvalProductionSignal.LowConfidence
                  EvalProductionSignal.NegativeFeedback ])
            redactor

    let turn =
        { TurnRecord.empty (CorrelationContext.root ()) with
            TurnId = "turn-1"
            AgentName = "agent"
            Input = "find secret guidance"
            Output = "secret answer"
            CreatedAt = timestamp }

    let segments =
        Map.ofList
            [ EvalSegmentDimension.Scenario, "support"
              EvalSegmentDimension.Language, "en"
              EvalSegmentDimension.Provider, "provider"
              EvalSegmentDimension.Model, "model" ]

    let capture signal =
        EvalProductionObservation.tryCapture
            owner
            timestamp
            (configuration "candidate")
            segments
            signal
            None
            turn
            policy
        |> Option.get

    [<TestMethod>]
    member _.``Negative feedback is privacy sampled and bound to exact execution configuration``() =
        let feedback =
            { Id = Guid.NewGuid()
              TurnId = turn.TurnId
              Sentiment = FeedbackSentiment.Negative
              Comment = Some "the secret answer is wrong"
              CreatedAt = timestamp
              Metadata = Map.empty }

        let observation =
            EvalProductionObservation.captureFeedback
                owner
                timestamp
                (configuration "candidate")
                segments
                turn
                feedback
                policy
            |> Option.get

        Assert.AreEqual(turn.Correlation.ExecutionId, EvalProductionObservation.executionId observation)
        Assert.AreEqual(configuration "candidate", EvalProductionObservation.configuration observation)

        Assert.AreEqual(
            "[redacted] answer",
            observation |> EvalProductionObservation.output |> EvalProductionRedaction.text
        )

        Assert.AreEqual(
            "the [redacted] answer is wrong",
            observation
            |> EvalProductionObservation.feedback
            |> Option.get
            |> EvalProductionRedaction.text
        )

        Assert.AreEqual(String.replicate 64 "a", EvalProductionObservation.consentRecordSha256 observation)
        Assert.AreEqual(timestamp.AddDays 30.0, EvalProductionObservation.retainUntil observation)

    [<TestMethod>]
    member _.``Reviewed candidates become permanent governed regression cases``() =
        let observation = capture EvalProductionSignal.Failure
        let candidate = EvalProductionCandidate.propose owner timestamp observation

        Assert.ThrowsExactly<ArgumentException>(fun () ->
            EvalProductionCandidate.review
                foreignReviewer
                timestamp
                "foreign"
                "Must be rejected."
                (EvalExpectedBehavior.answer "safe answer")
                Set.empty
                "Foreign review."
                candidate
            |> ignore)
        |> ignore

        let regressionCase, review =
            EvalProductionCandidate.review
                reviewer
                timestamp
                "production-regression"
                "Production failure promoted after independent review."
                (EvalExpectedBehavior.answer "safe answer")
                (Set.ofList [ "production"; "regression" ])
                "Sanitization and expected behavior reviewed."
                candidate

        Assert.AreEqual("production-regression", review.CaseId)

        match EvalCase.source regressionCase, EvalCase.reviewStatus regressionCase with
        | EvalCaseSource.ProductionDerived sanitization, EvalReviewStatus.Approved approval ->
            Assert.AreEqual(approval, sanitization.Approval)
            CollectionAssert.Contains(sanitization.AppliedRules |> List.toArray, "replace-secret-v1")
        | _ -> Assert.Fail("Expected an approved production-derived case.")

        let alert =
            { Id = Guid.NewGuid()
              Dimension = EvalDriftDimension.Model
              Segment = segments
              Magnitude = 0.2m
              Severity = EvalDriftSeverity.Critical
              Escalation = EvalDriftEscalation.Rollback
              BaselineConfiguration = configuration "baseline"
              CurrentConfiguration = configuration "candidate"
              ObservedAt = timestamp }

        let incident =
            EvalProductionIncident.openFromAlert timestamp alert
            |> EvalProductionIncident.close
                reviewer
                (timestamp.AddHours 1.0)
                "Regression fixed and retained."
                regressionCase

        Assert.AreEqual(Some "production-regression", EvalProductionIncident.regressionCaseId incident)

    [<TestMethod>]
    member _.``Failure clustering preserves signal segment and configuration identity``() =
        let first = capture EvalProductionSignal.Retry

        let second =
            EvalProductionObservation.tryCapture
                owner
                (timestamp.AddMinutes 1.0)
                (configuration "candidate")
                segments
                EvalProductionSignal.Retry
                None
                turn
                policy
            |> Option.get

        let cluster =
            EvalProductionFailureClustering.cluster [ first; second ] |> List.exactlyOne

        Assert.AreEqual(2, cluster.Count)
        Assert.AreEqual(EvalProductionSignal.Retry, cluster.Signal)
        Assert.AreEqual(segments, cluster.Segments)
        Assert.AreEqual(configuration "candidate", cluster.Configuration)

    [<TestMethod>]
    member _.``Drift alerts name every changed dimension and affected segment``() =
        let scalarDimensions =
            [ EvalDriftDimension.Model, EvalDriftDirection.DecreaseIsRegression, 0.9m, 0.7m
              EvalDriftDimension.Prompt, EvalDriftDirection.DecreaseIsRegression, 0.9m, 0.7m
              EvalDriftDimension.Retrieval, EvalDriftDirection.DecreaseIsRegression, 0.9m, 0.7m
              EvalDriftDimension.Cost, EvalDriftDirection.IncreaseIsRegression, 1m, 1.3m
              EvalDriftDimension.Latency, EvalDriftDirection.IncreaseIsRegression, 100m, 140m ]

        for dimension, direction, baseline, current in scalarDimensions do
            let rule =
                EvalDriftRule.create
                    dimension
                    direction
                    0.1m
                    0.2m
                    50
                    EvalDriftEscalation.Notify
                    EvalDriftEscalation.Rollback

            let observation =
                { Dimension = dimension
                  Segment = segments
                  Evidence = EvalDriftEvidence.Scalar(baseline, current, 100, 100)
                  BaselineConfiguration = configuration "baseline"
                  CurrentConfiguration = configuration "candidate"
                  ObservedAt = timestamp }

            let alert = EvalDriftDetection.detect rule observation |> Option.get
            Assert.AreEqual(dimension, alert.Dimension)
            Assert.AreEqual(segments, alert.Segment)
            Assert.AreEqual(EvalDriftSeverity.Critical, alert.Severity)
            Assert.AreEqual(EvalDriftEscalation.Rollback, alert.Escalation)

        let trafficRule =
            EvalDriftRule.create
                EvalDriftDimension.TrafficDistribution
                EvalDriftDirection.AnyChange
                0.1m
                0.3m
                50
                EvalDriftEscalation.Notify
                EvalDriftEscalation.BlockRelease

        let traffic =
            { Dimension = EvalDriftDimension.TrafficDistribution
              Segment = segments
              Evidence =
                EvalDriftEvidence.Distribution(
                    Map.ofList [ "support", 0.8m; "coding", 0.2m ],
                    Map.ofList [ "support", 0.4m; "coding", 0.6m ],
                    100
                )
              BaselineConfiguration = configuration "baseline"
              CurrentConfiguration = configuration "candidate"
              ObservedAt = timestamp }

        let alert = EvalDriftDetection.detect trafficRule traffic |> Option.get
        Assert.AreEqual(EvalDriftDimension.TrafficDistribution, alert.Dimension)
        Assert.AreEqual(0.4m, alert.Magnitude)
        Assert.AreEqual(EvalDriftEscalation.BlockRelease, alert.Escalation)

    [<TestMethod>]
    member _.``Shadow canary and AB assignments retain scoped opaque cohorts``() =
        let variants =
            [ { Name = "baseline"
                AllocationBasisPoints = 9_000
                Configuration = configuration "baseline" }
              { Name = "candidate"
                AllocationBasisPoints = 1_000
                Configuration = configuration "candidate" } ]

        for mode in [ EvalExperimentMode.Canary; EvalExperimentMode.AB ] do
            let experiment =
                EvalExperiment.create "release" mode "private-assignment-salt" variants

            let first = EvalExperiment.assign owner turn.Correlation.ExecutionId experiment
            let again = EvalExperiment.assign owner turn.Correlation.ExecutionId experiment

            let foreign =
                EvalExperiment.assign foreignReviewer turn.Correlation.ExecutionId experiment

            Assert.AreEqual(first.Primary, again.Primary)
            Assert.AreEqual(first.CohortSha256, again.CohortSha256)
            Assert.AreNotEqual(first.CohortSha256, foreign.CohortSha256)
            Assert.IsFalse(first.CohortSha256.Contains("tenant", StringComparison.Ordinal))

        let shadowVariants =
            [ { Name = "baseline"
                AllocationBasisPoints = 10_000
                Configuration = configuration "baseline" }
              { Name = "candidate"
                AllocationBasisPoints = 0
                Configuration = configuration "candidate" } ]

        let shadow =
            EvalExperiment.create "shadow" EvalExperimentMode.Shadow "private-assignment-salt" shadowVariants
            |> EvalExperiment.assign owner turn.Correlation.ExecutionId

        Assert.AreEqual("baseline", shadow.Primary.Name)
        Assert.AreEqual("candidate", shadow.Shadows |> List.exactlyOne |> _.Name)
