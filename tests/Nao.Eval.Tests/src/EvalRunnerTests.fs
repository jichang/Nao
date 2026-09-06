namespace Nao.Eval.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval
open Nao.Eval.Evaluators

module private EtclovgHarness =
    let runAsync config context agent request =
        Nao.Agents.EtclovgHarness.runAsync config context agent request CancellationToken.None

module private TestAgents =
    let echo prefix =
        Agent.create "echo" "Echo" "Echoes input with a prefix" 0 [] AgentContract.Text (fun _ input ->
            Task.FromResult(sprintf "%s: %s" prefix input))

    let fixedResponse response =
        Agent.create "fixed" "Fixed" "Returns fixed response" 0 [] AgentContract.Text (fun _ _ ->
            Task.FromResult response)

[<TestClass>]
type EvalRunnerTests() =
    let owner = "eval-tests"

    let authorization =
        let principal =
            SecurityPrincipal.create (TenantId.parse "eval-tenant") (UserId.parse "eval-user") []

        AuthorizationScope.tryCreate
            principal
            None
            (WorkspaceId.parse "eval-workspace")
            (Some(SessionId.parse "eval-session"))
        |> Option.get

    let execution =
        EvalExecutionConfig.create authorization AgentContext.unrestrictedForTests

    let defaultConfig = EvalRunnerConfig.create execution

    [<TestMethod>]
    member _.``RunCase forwards execution correlation to LLM evaluator``() =
        let observed = ref None

        let provider =
            LlmProvider.create (fun () -> "capturing") (fun correlation _conversation _options ->
                observed.Value <- Some correlation

                Task.FromResult(
                    { Content = """{"score":5,"reason":"correct"}"""
                      FinishReason = "stop"
                      TokensUsed = None
                      Usage = None }
                    : CompletionResult
                ))

        let agent = TestAgents.fixedResponse "42"
        let case = EvalCase.create "correlated" "question" "42"
        let dataset = EvalDataset.create owner "correlation" [ case ]
        let run = EvalRun.create owner dataset.Id

        let result =
            EvalRunner.runCaseLightAsync execution run (LlmJudge.create provider) agent case CancellationToken.None
            |> _.Result

        Assert.IsTrue(observed.Value.IsSome)
        Assert.AreEqual(result.ExecutionId, observed.Value.Value.ExecutionId)

    [<TestMethod>]
    member _.``RunCaseAsync evaluates a single case``() =
        let agent = TestAgents.fixedResponse "The answer is 42"
        let case = EvalCase.create "q1" "What is the answer?" "42"
        let evaluator = Contains.evaluator
        let dataset = EvalDataset.create owner "single" [ case ]
        let run = EvalRun.create owner dataset.Id

        let result =
            (EvalRunner.runCaseAsync execution run evaluator agent case CancellationToken.None).Result

        Assert.AreEqual("q1", result.CaseId)
        Assert.AreEqual(owner, result.Owner)
        Assert.AreEqual(dataset.Id, result.DatasetId)
        Assert.AreEqual(run.Id, result.RunId)
        Assert.AreNotEqual(ExecutionId.ofGuid System.Guid.Empty, result.ExecutionId)
        Assert.AreEqual(EvalVerdict.Pass, result.Verdict)
        Assert.IsTrue(result.LatencyMs >= 0L)

    [<TestMethod>]
    member _.``RunDatasetAsync produces a complete report``() =
        let agent = TestAgents.echo "Reply"

        let dataset =
            EvalDataset.create
                owner
                "basic"
                [ EvalCase.create "c1" "hello" "hello" |> EvalCase.withTags [ "greeting" ]
                  EvalCase.create "c2" "world" "world" |> EvalCase.withTags [ "greeting" ]
                  EvalCase.create "c3" "test" "missing" |> EvalCase.withTags [ "other" ] ]

        let evaluator = Contains.evaluator

        let report =
            (EvalRunner.runDatasetAsync defaultConfig evaluator agent dataset CancellationToken.None).Result

        Assert.AreEqual(3, report.TotalCases)
        Assert.AreEqual(2, report.Passed) // "hello" contains "hello", "world" contains "world"
        Assert.AreEqual(1, report.Failed) // "test" does not contain "missing"
        Assert.IsTrue(report.AverageScore > 0.6)

    [<TestMethod>]
    member _.``RunDatasetAsync with parallel config works``() =
        let agent = TestAgents.fixedResponse "hello world"

        let dataset =
            EvalDataset.create
                owner
                "parallel"
                [ EvalCase.create "p1" "a" "hello"
                  EvalCase.create "p2" "b" "hello"
                  EvalCase.create "p3" "c" "hello" ]

        let evaluator = Contains.evaluator
        let config = EvalRunnerConfig.withParallelism 3 execution

        let report =
            (EvalRunner.runDatasetAsync config evaluator agent dataset CancellationToken.None).Result

        Assert.AreEqual(3, report.TotalCases)
        Assert.AreEqual(3, report.Passed)

    [<TestMethod>]
    member _.``CompareAgentsAsync returns per-agent reports``() =
        let agent1 = TestAgents.fixedResponse "The weather is sunny"
        let agent2 = TestAgents.fixedResponse "I don't know"

        let dataset =
            EvalDataset.create owner "comparison" [ EvalCase.create "w1" "What's the weather?" "sunny" ]

        let evaluator = Contains.evaluator

        let results =
            (EvalRunner.compareAgentsAsync
                defaultConfig
                evaluator
                [ ("good", agent1); ("bad", agent2) ]
                dataset
                CancellationToken.None)
                .Result

        Assert.AreEqual(2, results.Length)
        let (_, report1) = results.[0]
        let (_, report2) = results.[1]
        Assert.AreEqual(1, report1.Passed)
        Assert.AreEqual(0, report2.Passed)

    [<TestMethod>]
    member _.``EvalReport format produces readable output``() =
        let agent = TestAgents.fixedResponse "42"

        let dataset =
            EvalDataset.create owner "format-test" [ EvalCase.create "f1" "question" "42" ]

        let evaluator = ExactMatch.evaluator

        let report =
            (EvalRunner.runDatasetAsync defaultConfig evaluator agent dataset CancellationToken.None).Result

        let formatted = EvalReport.format report
        Assert.IsTrue(formatted.Contains("format-test"))
        Assert.IsTrue(formatted.Contains("PASS"))
        Assert.IsTrue(formatted.Contains("f1"))

    [<TestMethod>]
    member _.``Tag breakdown correctly groups results``() =
        let agent = TestAgents.echo "Reply"

        let dataset =
            EvalDataset.create
                owner
                "tags"
                [ EvalCase.create "t1" "hello" "hello" |> EvalCase.withTags [ "math" ]
                  EvalCase.create "t2" "world" "world" |> EvalCase.withTags [ "math" ]
                  EvalCase.create "t3" "test" "nope" |> EvalCase.withTags [ "general" ] ]

        let evaluator = Contains.evaluator

        let report =
            (EvalRunner.runDatasetAsync defaultConfig evaluator agent dataset CancellationToken.None).Result

        Assert.IsTrue(report.TagBreakdown.ContainsKey "math")
        Assert.IsTrue(report.TagBreakdown.ContainsKey "general")
        Assert.AreEqual(2, report.TagBreakdown.["math"].Count)
        Assert.AreEqual(1.0, report.TagBreakdown.["math"].PassRate)
        Assert.AreEqual(1, report.TagBreakdown.["general"].Count)
        Assert.AreEqual(0.0, report.TagBreakdown.["general"].PassRate)

    [<TestMethod>]
    member _.``Harness policy denial prevents evaluation execution``() =
        let mutable agentExecuted = false
        let mutable evaluatorExecuted = false

        let agent =
            Agent.create "blocked" "Blocked" "Must not run" 0 [] AgentContract.Text (fun _ _ ->
                agentExecuted <- true
                Task.FromResult "unexpected")

        let evaluator =
            Evaluator.create "blocked-evaluator" (fun _ _ _ ->
                evaluatorExecuted <- true
                Task.FromResult(EvalVerdict.Pass, "unexpected"))

        let policy =
            { Id = "block-evaluation"
              Description = "Blocks evaluation execution"
              Enforcement = PolicyEnforcement.Block
              Evaluate = fun _ -> Some "evaluation denied" }

        let blockedExecution =
            { execution with
                Harness =
                    { EtclovgConfig.Default with
                        PolicyEngine = Some(PolicyEngine.create [ policy ]) } }

        let case = EvalCase.create "blocked" "question" "answer"
        let dataset = EvalDataset.create owner "blocked" [ case ]
        let run = EvalRun.create owner dataset.Id

        let result =
            EvalRunner.runCaseLightAsync blockedExecution run evaluator agent case CancellationToken.None
            |> _.Result

        Assert.IsFalse(agentExecuted)
        Assert.IsFalse(evaluatorExecuted)
        Assert.AreEqual(EvalVerdict.Fail, result.Verdict)
        Assert.AreEqual("Blocked by policy: evaluation denied", result.Reason)
        Assert.AreEqual("", result.ActualOutput)

    [<TestMethod>]
    member _.``Evaluation matches canonical harness execution semantics``() =
        let agent = TestAgents.echo "executed"

        let policy =
            { Id = "normalize-input"
              Description = "Normalizes execution input"
              Enforcement = PolicyEnforcement.Modify _.ToUpperInvariant()
              Evaluate = fun _ -> Some "normalize" }

        let configuredExecution =
            { execution with
                Harness =
                    { EtclovgConfig.Default with
                        PolicyEngine = Some(PolicyEngine.create [ policy ]) }
                PolicyVersions = Map [ "input", "v1" ]
                DependencyVersions = Map [ "agent", "v1" ] }

        let input = "same request"
        let context = configuredExecution.CreateAgentContext()

        let request =
            ExecutionRequest.create
                configuredExecution.Authorization
                (TurnId.parse "parity-turn")
                "parity-conversation"
                agent.Metadata.Id
                input
                configuredExecution.Sandbox
                configuredExecution.PolicyVersions
                configuredExecution.DependencyVersions
                context.Correlation

        let harnessResult =
            EtclovgHarness.runAsync configuredExecution.Harness context agent request
            |> _.Result

        let case = EvalCase.create "parity" input "executed: SAME REQUEST"
        let dataset = EvalDataset.create owner "parity" [ case ]
        let run = EvalRun.create owner dataset.Id

        let evalResult =
            EvalRunner.runCaseLightAsync configuredExecution run ExactMatch.evaluator agent case CancellationToken.None
            |> _.Result

        Assert.AreEqual(ExecutionTerminalStatus.Succeeded, harnessResult.Status)
        Assert.AreEqual(harnessResult.Outputs.Response.Value, evalResult.ActualOutput)
        Assert.AreEqual(EvalVerdict.Pass, evalResult.Verdict)

    [<TestMethod>]
    member _.``Per-case timeout uses harness deadline semantics``() =
        let agent =
            Agent.create "slow" "Slow" "Exceeds the case deadline" 0 [] AgentContract.Text (fun _ _ ->
                task {
                    do! Task.Delay(TimeSpan.FromSeconds 5.0)
                    return "late"
                })

        let case = EvalCase.create "timeout" "question" "answer"
        let dataset = EvalDataset.create owner "timeout" [ case ]

        let config =
            { defaultConfig with
                TimeoutPerCaseMs = Some 25 }

        let report =
            EvalRunner.runDatasetAsync config Contains.evaluator agent dataset CancellationToken.None
            |> _.Result

        Assert.AreEqual(1, report.Failed)
        Assert.AreEqual("", report.Results.Head.ActualOutput)
        Assert.AreEqual("Execution timed out", report.Results.Head.Reason)

    [<TestMethod>]
    member _.``Caller cancellation stops sequential evaluation``() =
        let mutable executions = 0
        use cancellation = new CancellationTokenSource()

        let agent =
            Agent.create "cancel" "Cancel" "Cancels during execution" 0 [] AgentContract.Text (fun _ _ ->
                executions <- executions + 1
                cancellation.Cancel()
                Task.FromResult "late")

        let tailCount = Random.Shared.Next(2, 7)

        let cases =
            [ for index in 0..tailCount -> EvalCase.create (sprintf "cancel-%d" index) "input" "late" ]

        let dataset = EvalDataset.create owner "caller-cancellation" cases

        let report =
            EvalRunner.runDatasetAsync defaultConfig ExactMatch.evaluator agent dataset cancellation.Token
            |> _.Result

        Assert.AreEqual(1, executions)
        Assert.AreEqual(1, report.TotalCases)
        Assert.AreEqual(EvalVerdict.Fail, report.Results.Head.Verdict)
        Assert.AreEqual("Execution cancelled", report.Results.Head.Reason)

    [<TestMethod>]
    member _.``Caller cancellation interrupts evaluator``() =
        use cancellation = new CancellationTokenSource()

        let evaluator =
            Evaluator.create "cancel-evaluator" (fun _ _ _ ->
                cancellation.Cancel()
                Task.Delay(TimeSpan.FromSeconds 5.0).ContinueWith(fun _ -> EvalVerdict.Pass, "late"))

        let case = EvalCase.create "cancel-evaluator" "input" "output"
        let dataset = EvalDataset.create owner "cancel-evaluator" [ case ]
        let run = EvalRun.create owner dataset.Id

        Assert.ThrowsExactlyAsync<TaskCanceledException>(fun () ->
            EvalRunner.runCaseLightAsync
                execution
                run
                evaluator
                (TestAgents.fixedResponse "output")
                case
                cancellation.Token
            :> Task)
        |> _.Wait()

    [<TestMethod>]
    member _.``Sequential execution stops after first failure``() =
        let mutable executions = 0

        let agent =
            Agent.create "counting" "Counting" "Counts executed cases" 0 [] AgentContract.Text (fun _ input ->
                executions <- executions + 1
                Task.FromResult input)

        let tailCount = Random.Shared.Next(2, 7)

        let cases =
            EvalCase.create "first" "wrong" "expected"
            :: [ for index in 1..tailCount -> EvalCase.create (sprintf "tail-%d" index) "expected" "expected" ]

        let dataset = EvalDataset.create owner "stop-first" cases

        let config =
            { defaultConfig with
                StopOnFirstFailure = true }

        let report =
            EvalRunner.runDatasetAsync config ExactMatch.evaluator agent dataset CancellationToken.None
            |> _.Result

        Assert.AreEqual(1, executions)
        Assert.AreEqual(1, report.TotalCases)
        Assert.AreEqual(1, report.Failed)

    [<TestMethod>]
    member _.``Stop-on-first rejects parallel execution``() =
        let config =
            { defaultConfig with
                MaxParallelism = 2
                StopOnFirstFailure = true }

        let dataset = EvalDataset.create owner "invalid-stop-first" []

        Assert.ThrowsExactlyAsync<ArgumentException>(fun () ->
            EvalRunner.runDatasetAsync
                config
                ExactMatch.evaluator
                (TestAgents.fixedResponse "")
                dataset
                CancellationToken.None
            :> Task)
        |> _.Wait()
