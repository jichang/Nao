namespace Nao.Eval.Tests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval
open Nao.Eval.Evaluators

[<TestClass>]
type EvalQualityTests() =
    let authorization =
        let principal =
            SecurityPrincipal.create (TenantId.parse "quality-tenant") (UserId.parse "quality-user") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "quality-workspace") None
        |> Option.get

    let correlation = CorrelationContext.root ()

    let trace toolInput =
        { Id = Guid.NewGuid()
          Correlation = correlation
          AgentId = "quality-agent"
          Input = "question"
          Output = Some """{"status":"ok","answer":"grounded [source#chunk]"}"""
          Steps =
            [ { StepNumber = 1
                Action = TraceAction.ToolInvocation "search"
                Input = toolInput
                Output = "evidence"
                DurationMs = 2L
                Timestamp = DateTimeOffset.UtcNow } ]
          StartedAt = DateTimeOffset.UtcNow
          CompletedAt = Some DateTimeOffset.UtcNow
          Success = true
          Metadata = Map.empty }

    let execution toolInput =
        { Correlation = correlation
          Status = ExecutionTerminalStatus.Succeeded
          Outputs =
            { Response = Some """{"status":"ok","answer":"grounded [source#chunk]"}"""
              Artifacts = [] }
          Usage =
            { ResourceUsage.Zero with
                ToolCalls = 1
                ElapsedTime = TimeSpan.FromMilliseconds 10.0
                EstimatedCostUsd = 0.001M }
          Evidence =
            { Trace = Some(trace toolInput)
              Metrics = None
              Judgement = None
              Regression = None
              AuditEntries = 0 }
          PolicyDecisions =
            { PolicyViolations = []
              PolicyEvaluations = []
              ConstitutionViolations = []
              ConstitutionDisposition = ConstitutionDisposition.Released } }

    let context execution =
        { Authorization = authorization
          Correlation = correlation
          Execution = execution
          CancellationToken = CancellationToken.None }

    let sha256 (value: string) =
        value
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let qualityCase toolInput =
        let expected =
            { Answer = None
              Properties = Map [ "status", "ok" ]
              ToolCalls =
                [ { Tool = "search"
                    Operation = "query"
                    ArgumentsSha256 = Some(sha256 toolInput)
                    MinimumCount = 1
                    MaximumCount = 1 } ]
              ForbiddenActions = [ "delete" ]
              Artifacts = []
              Citations =
                [ { SourceId = "source"
                    ChunkId = Some "chunk" } ]
              Budgets =
                Some
                    { MaximumDuration = Some(TimeSpan.FromSeconds 1.0)
                      MaximumInputTokens = None
                      MaximumOutputTokens = None
                      MaximumToolCalls = Some 1
                      MaximumCostUsd = Some 0.01M }
              TerminalStates = Set.singleton EvalExpectedTerminalState.Succeeded }

        EvalCase.create
            "quality"
            "complete"
            "Complete deterministic evidence"
            [ { Id = "question"
                Role = EvalTurnRole.User
                Content = "question" } ]
            expected
            Set.empty
            Map.empty
            EvalReviewStatus.Unreviewed
            None
            EvalCaseSource.RepositoryAuthored

    [<TestMethod>]
    member _.``Deterministic execution validates structured output tools citations policy resources and terminal state``
        ()
        =
        let toolInput = """{"query":"evidence"}"""

        let schema =
            StructuredOutputSchema.create "answer-schema" "1" "status-required" (fun root ->
                let mutable property = Unchecked.defaultof<JsonElement>

                if
                    root.ValueKind = JsonValueKind.Object
                    && root.TryGetProperty("answer", &property)
                then
                    Ok()
                else
                    Error "answer is required")

        let evaluator = DeterministicExecution.create (Some schema)

        let verdict, reason =
            evaluator.EvaluateAsync
                (context (execution toolInput))
                (qualityCase toolInput)
                """{"status":"ok","answer":"grounded [source#chunk]"}"""
            |> _.Result

        Assert.AreEqual(EvaluatorAuthority.Deterministic, evaluator.Authority)
        Assert.IsTrue(evaluator.Reliability.ReleaseBlocking)
        Assert.AreEqual(EvalVerdict.Pass, verdict)
        Assert.AreEqual("All deterministic execution expectations passed.", reason)

    [<TestMethod>]
    member _.``Deterministic execution fails closed on malformed structured output and missing evidence``() =
        let toolInput = """{"query":"evidence"}"""

        let verdict, reason =
            DeterministicExecution.evaluator.EvaluateAsync
                (context
                    { execution toolInput with
                        Evidence =
                            { (execution toolInput).Evidence with
                                Trace = None } })
                (qualityCase toolInput)
                "not-json"
            |> _.Result

        Assert.AreEqual(EvalVerdict.Fail, verdict)
        StringAssert.Contains(reason, "invalid JSON")
        StringAssert.Contains(reason, "tool 'search' count 0")
        StringAssert.Contains(reason, "required citation")

    [<TestMethod>]
    member _.``High-risk evaluation never lets favorable judges override deterministic failure``() =
        let mutable judgeCalls = 0

        let deterministic =
            Evaluator.deterministic "proof" "1" "proof" true "Fails closed." (fun _ _ _ ->
                Task.FromResult(EvalVerdict.Fail, "protocol invalid"))

        let favorableJudge =
            Evaluator.probabilistic "judge" "1" "judge" true "Untrusted." (fun _ _ _ ->
                judgeCalls <- judgeCalls + 1
                Task.FromResult(EvalVerdict.Pass, "looks good"))

        let gate =
            HighRiskEvaluation.create JudgeAggregationPolicy.Unanimous [ deterministic ] [ favorableJudge ]

        let verdict, reason =
            gate.EvaluateAsync (context (execution "input")) (EvalCase.repository "case" "input" "answer") "answer"
            |> _.Result

        Assert.AreEqual(EvalVerdict.Fail, verdict)
        Assert.AreEqual(0, judgeCalls)
        StringAssert.Contains(reason, "Deterministic validation failed")

    [<TestMethod>]
    member _.``Multi-judge majority aggregates only probabilistic evidence``() =
        let proof =
            Evaluator.deterministic "proof" "1" "proof" true "Fails closed." (fun _ _ _ ->
                Task.FromResult(EvalVerdict.Pass, "valid"))

        let judge name verdict =
            Evaluator.probabilistic name "1" name true "Untrusted." (fun _ _ _ ->
                Task.FromResult(verdict, name + " rationale"))

        let gate =
            HighRiskEvaluation.create
                JudgeAggregationPolicy.Majority
                [ proof ]
                [ judge "one" EvalVerdict.Pass
                  judge "two" EvalVerdict.Pass
                  judge "three" EvalVerdict.Fail ]

        let verdict, reason =
            gate.EvaluateAsync (context (execution "input")) (EvalCase.repository "case" "input" "answer") "answer"
            |> _.Result

        Assert.AreEqual(EvaluatorAuthority.Probabilistic, gate.Authority)
        Assert.AreEqual(EvalVerdict.Pass, verdict)
        StringAssert.Contains(reason, "Untrusted judge evidence accepted")

    [<TestMethod>]
    member _.``Judge calibration measures agreement stability and bias and blocks degraded upgrades``() =
        let observation caseId human judge repetition position length family selfEvaluation =
            { CaseId = caseId
              HumanPassed = human
              JudgePassed = judge
              Repetition = repetition
              Position = position
              ResponseLength = length
              JudgeModelFamily = "judge-family"
              CandidateModelFamily = family
              IsSelfEvaluation = selfEvaluation }

        let prior =
            [ observation "a" true true 0 0 10 "family-a" false
              observation "a" true true 1 1 20 "family-a" false
              observation "b" false false 0 0 10 "family-b" false
              observation "b" false false 1 1 20 "family-b" false ]
            |> JudgeCalibration.measure

        let candidate =
            [ observation "a" true true 0 0 10 "family-a" true
              observation "a" true false 1 1 30 "family-a" true
              observation "b" false true 0 0 10 "family-b" false
              observation "b" false false 1 1 30 "family-b" false ]
            |> JudgeCalibration.measure

        Assert.AreEqual(1.0, prior.Accuracy)
        Assert.AreEqual(1.0, prior.CohenKappa)
        Assert.IsTrue(candidate.Accuracy < prior.Accuracy)
        Assert.IsTrue(candidate.Stability < prior.Stability)
        Assert.IsTrue(candidate.ModelFamilyPassRates.ContainsKey "family-a")

        let failures =
            JudgeUpgradeQualification.qualify
                { MinimumCases = 2
                  MinimumAccuracy = 0.8
                  MinimumKappa = 0.6
                  MinimumStability = 0.9
                  MaximumBias = 0.2
                  MaximumAccuracyRegression = 0.05 }
                prior
                candidate

        Assert.IsFalse(failures.IsEmpty)
        Assert.IsTrue(failures |> List.exists (_.Contains("accuracy regression")))
