namespace Nao.Eval.Tests

open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval
open Nao.Eval.Evaluators

/// A simple deterministic agent for testing the eval runner
type EchoAgent(prefix: string) =
    let id = { Name = "echo"; Description = "Echoes input with a prefix" }

    interface IAgent with
        member _.Id = id
        member _.RunAsync(input: string) =
            Task.FromResult(sprintf "%s: %s" prefix input)
        member _.HandleMessageAsync(_msg: AgentMessage) =
            Task.FromResult(None)

/// Agent that returns a fixed response regardless of input
type FixedAgent(response: string) =
    let id = { Name = "fixed"; Description = "Returns fixed response" }

    interface IAgent with
        member _.Id = id
        member _.RunAsync(_input: string) = Task.FromResult(response)
        member _.HandleMessageAsync(_msg: AgentMessage) = Task.FromResult(None)

[<TestClass>]
type EvalRunnerTests() =

    [<TestMethod>]
    member _.``RunCaseAsync evaluates a single case``() =
        let agent = FixedAgent("The answer is 42") :> IAgent
        let case = EvalCase.create "q1" "What is the answer?" "42"
        let evaluator = Contains.evaluator
        let result = (EvalRunner.runCaseAsync evaluator agent case).Result
        Assert.AreEqual("q1", result.CaseId)
        Assert.AreEqual(EvalVerdict.Pass, result.Verdict)
        Assert.IsTrue(result.LatencyMs >= 0L)

    [<TestMethod>]
    member _.``RunDatasetAsync produces a complete report``() =
        let agent = EchoAgent("Reply") :> IAgent
        let dataset = EvalDataset.create "basic" [
            EvalCase.create "c1" "hello" "hello" |> EvalCase.withTags ["greeting"]
            EvalCase.create "c2" "world" "world" |> EvalCase.withTags ["greeting"]
            EvalCase.create "c3" "test" "missing" |> EvalCase.withTags ["other"]
        ]
        let evaluator = Contains.evaluator
        let report = (EvalRunner.runDatasetAsync EvalRunnerConfig.Default evaluator agent dataset).Result

        Assert.AreEqual(3, report.TotalCases)
        Assert.AreEqual(2, report.Passed) // "hello" contains "hello", "world" contains "world"
        Assert.AreEqual(1, report.Failed) // "test" does not contain "missing"
        Assert.IsTrue(report.AverageScore > 0.6)

    [<TestMethod>]
    member _.``RunDatasetAsync with parallel config works``() =
        let agent = FixedAgent("hello world") :> IAgent
        let dataset = EvalDataset.create "parallel" [
            EvalCase.create "p1" "a" "hello"
            EvalCase.create "p2" "b" "hello"
            EvalCase.create "p3" "c" "hello"
        ]
        let evaluator = Contains.evaluator
        let config = EvalRunnerConfig.Parallel 3
        let report = (EvalRunner.runDatasetAsync config evaluator agent dataset).Result

        Assert.AreEqual(3, report.TotalCases)
        Assert.AreEqual(3, report.Passed)

    [<TestMethod>]
    member _.``CompareAgentsAsync returns per-agent reports``() =
        let agent1 = FixedAgent("The weather is sunny") :> IAgent
        let agent2 = FixedAgent("I don't know") :> IAgent
        let dataset = EvalDataset.create "comparison" [
            EvalCase.create "w1" "What's the weather?" "sunny"
        ]
        let evaluator = Contains.evaluator
        let results = (EvalRunner.compareAgentsAsync EvalRunnerConfig.Default evaluator [("good", agent1); ("bad", agent2)] dataset).Result

        Assert.AreEqual(2, results.Length)
        let (_, report1) = results.[0]
        let (_, report2) = results.[1]
        Assert.AreEqual(1, report1.Passed)
        Assert.AreEqual(0, report2.Passed)

    [<TestMethod>]
    member _.``EvalReport format produces readable output``() =
        let agent = FixedAgent("42") :> IAgent
        let dataset = EvalDataset.create "format-test" [
            EvalCase.create "f1" "question" "42"
        ]
        let evaluator = ExactMatch.evaluator
        let report = (EvalRunner.runDatasetAsync EvalRunnerConfig.Default evaluator agent dataset).Result
        let formatted = EvalReport.format report
        Assert.IsTrue(formatted.Contains("format-test"))
        Assert.IsTrue(formatted.Contains("PASS"))
        Assert.IsTrue(formatted.Contains("f1"))

    [<TestMethod>]
    member _.``Tag breakdown correctly groups results``() =
        let agent = EchoAgent("Reply") :> IAgent
        let dataset = EvalDataset.create "tags" [
            EvalCase.create "t1" "hello" "hello" |> EvalCase.withTags ["math"]
            EvalCase.create "t2" "world" "world" |> EvalCase.withTags ["math"]
            EvalCase.create "t3" "test" "nope" |> EvalCase.withTags ["general"]
        ]
        let evaluator = Contains.evaluator
        let report = (EvalRunner.runDatasetAsync EvalRunnerConfig.Default evaluator agent dataset).Result

        Assert.IsTrue(report.TagBreakdown.ContainsKey "math")
        Assert.IsTrue(report.TagBreakdown.ContainsKey "general")
        Assert.AreEqual(2, report.TagBreakdown.["math"].Count)
        Assert.AreEqual(1.0, report.TagBreakdown.["math"].PassRate)
        Assert.AreEqual(1, report.TagBreakdown.["general"].Count)
        Assert.AreEqual(0.0, report.TagBreakdown.["general"].PassRate)
