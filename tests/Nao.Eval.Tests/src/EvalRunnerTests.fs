namespace Nao.Eval.Tests

open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval
open Nao.Eval.Evaluators

module private TestAgents =
    let echo prefix =
        Agent.createContextual "echo" "Echo" "Echoes input with a prefix" 0 [] AgentContract.Text (fun _ input ->
            Task.FromResult(sprintf "%s: %s" prefix input))

    let fixedResponse response =
        Agent.createContextual "fixed" "Fixed" "Returns fixed response" 0 [] AgentContract.Text (fun _ _ ->
            Task.FromResult response)

[<TestClass>]
type EvalRunnerTests() =
    let owner = "eval-tests"

    [<TestMethod>]
    member _.``RunCaseAsync evaluates a single case``() =
        let agent = TestAgents.fixedResponse "The answer is 42"
        let case = EvalCase.create "q1" "What is the answer?" "42"
        let evaluator = Contains.evaluator
        let dataset = EvalDataset.create owner "single" [ case ]
        let run = EvalRun.create owner dataset.Id
        let result = (EvalRunner.runCaseAsync run evaluator agent case).Result
        Assert.AreEqual("q1", result.CaseId)
        Assert.AreEqual(owner, result.Owner)
        Assert.AreEqual(dataset.Id, result.DatasetId)
        Assert.AreEqual(run.Id, result.RunId)
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
            (EvalRunner.runDatasetAsync EvalRunnerConfig.Default evaluator agent dataset).Result

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
        let config = EvalRunnerConfig.Parallel 3
        let report = (EvalRunner.runDatasetAsync config evaluator agent dataset).Result

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
                EvalRunnerConfig.Default
                evaluator
                [ ("good", agent1); ("bad", agent2) ]
                dataset)
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
            (EvalRunner.runDatasetAsync EvalRunnerConfig.Default evaluator agent dataset).Result

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
            (EvalRunner.runDatasetAsync EvalRunnerConfig.Default evaluator agent dataset).Result

        Assert.IsTrue(report.TagBreakdown.ContainsKey "math")
        Assert.IsTrue(report.TagBreakdown.ContainsKey "general")
        Assert.AreEqual(2, report.TagBreakdown.["math"].Count)
        Assert.AreEqual(1.0, report.TagBreakdown.["math"].PassRate)
        Assert.AreEqual(1, report.TagBreakdown.["general"].Count)
        Assert.AreEqual(0.0, report.TagBreakdown.["general"].PassRate)
