namespace Nao.Eval.Tests

open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Eval
open Nao.Eval.Evaluators

[<TestClass>]
type ExactMatchEvaluatorTests() =

    [<TestMethod>]
    member _.``ExactMatch passes when output matches expected``() =
        let case = EvalCase.create "test1" "input" "hello world"
        let (verdict, _) = (ExactMatch.evaluator.EvaluateAsync case "Hello World").Result
        Assert.AreEqual(EvalVerdict.Pass, verdict)

    [<TestMethod>]
    member _.``ExactMatch fails when output differs``() =
        let case = EvalCase.create "test2" "input" "hello"
        let (verdict, _) = (ExactMatch.evaluator.EvaluateAsync case "goodbye").Result
        Assert.AreEqual(EvalVerdict.Fail, verdict)

    [<TestMethod>]
    member _.``ExactMatch case sensitive respects casing``() =
        let case = EvalCase.create "test3" "input" "Hello"
        let (verdict, _) = (ExactMatch.caseSensitive.EvaluateAsync case "hello").Result
        Assert.AreEqual(EvalVerdict.Fail, verdict)

[<TestClass>]
type ContainsEvaluatorTests() =

    [<TestMethod>]
    member _.``Contains passes when output includes expected``() =
        let case = EvalCase.create "test1" "input" "weather"
        let (verdict, _) = (Contains.evaluator.EvaluateAsync case "The weather today is sunny").Result
        Assert.AreEqual(EvalVerdict.Pass, verdict)

    [<TestMethod>]
    member _.``Contains fails when output lacks expected``() =
        let case = EvalCase.create "test2" "input" "rain"
        let (verdict, _) = (Contains.evaluator.EvaluateAsync case "The weather is sunny").Result
        Assert.AreEqual(EvalVerdict.Fail, verdict)

    [<TestMethod>]
    member _.``ContainsAll gives partial score for partial matches``() =
        let evaluator = Contains.all ["hello"; "world"; "foo"]
        let case = EvalCase.create "test3" "input" ""
        let (verdict, _) = (evaluator.EvaluateAsync case "hello world").Result
        match verdict with
        | EvalVerdict.Partial score ->
            Assert.IsTrue(score > 0.6 && score < 0.7) // 2/3
        | _ -> Assert.Fail("Expected Partial verdict")

    [<TestMethod>]
    member _.``ContainsAll passes when all keywords present``() =
        let evaluator = Contains.all ["hello"; "world"]
        let case = EvalCase.create "test4" "input" ""
        let (verdict, _) = (evaluator.EvaluateAsync case "hello beautiful world").Result
        Assert.AreEqual(EvalVerdict.Pass, verdict)

    [<TestMethod>]
    member _.``ContainsAny passes when any keyword present``() =
        let evaluator = Contains.any ["cat"; "dog"; "fish"]
        let case = EvalCase.create "test5" "input" ""
        let (verdict, _) = (evaluator.EvaluateAsync case "I have a dog").Result
        Assert.AreEqual(EvalVerdict.Pass, verdict)

    [<TestMethod>]
    member _.``ContainsAny fails when no keywords present``() =
        let evaluator = Contains.any ["cat"; "dog"; "fish"]
        let case = EvalCase.create "test6" "input" ""
        let (verdict, _) = (evaluator.EvaluateAsync case "I have a bird").Result
        Assert.AreEqual(EvalVerdict.Fail, verdict)

[<TestClass>]
type RegexEvaluatorTests() =

    [<TestMethod>]
    member _.``Regex passes when pattern matches``() =
        let evaluator = RegexEval.matches @"\d+\.\d+"
        let case = EvalCase.create "test1" "input" ""
        let (verdict, _) = (evaluator.EvaluateAsync case "The temperature is 72.5 degrees").Result
        Assert.AreEqual(EvalVerdict.Pass, verdict)

    [<TestMethod>]
    member _.``Regex fails when pattern does not match``() =
        let evaluator = RegexEval.matches @"^\d+$"
        let case = EvalCase.create "test2" "input" ""
        let (verdict, _) = (evaluator.EvaluateAsync case "not a number").Result
        Assert.AreEqual(EvalVerdict.Fail, verdict)

[<TestClass>]
type CompositeEvaluatorTests() =

    [<TestMethod>]
    member _.``Composite All passes when all evaluators pass``() =
        let evaluator = Composite.all [
            Contains.all ["hello"]
            RegexEval.matches @"\w+"
        ]
        let case = EvalCase.create "test1" "input" ""
        let (verdict, _) = (evaluator.EvaluateAsync case "hello world").Result
        Assert.AreEqual(EvalVerdict.Pass, verdict)

    [<TestMethod>]
    member _.``Composite All gives partial when some fail``() =
        let evaluator = Composite.all [
            Contains.all ["hello"]
            Contains.all ["missing"]
        ]
        let case = EvalCase.create "test2" "input" ""
        let (verdict, _) = (evaluator.EvaluateAsync case "hello world").Result
        match verdict with
        | EvalVerdict.Partial _ -> ()
        | _ -> Assert.Fail("Expected Partial verdict")

    [<TestMethod>]
    member _.``Composite Any passes when at least one passes``() =
        let evaluator = Composite.any [
            Contains.all ["missing"]
            Contains.all ["hello"]
        ]
        let case = EvalCase.create "test3" "input" ""
        let (verdict, _) = (evaluator.EvaluateAsync case "hello world").Result
        Assert.AreEqual(EvalVerdict.Pass, verdict)

    [<TestMethod>]
    member _.``Composite Average computes correct score``() =
        let evaluator = Composite.average [
            Contains.all ["hello"; "world"]  // Both present -> 1.0
            Contains.all ["foo"; "bar"]      // Neither present -> 0.0
        ]
        let case = EvalCase.create "test4" "input" ""
        let (verdict, _) = (evaluator.EvaluateAsync case "hello world").Result
        match verdict with
        | EvalVerdict.Partial score ->
            Assert.IsTrue(score >= 0.49 && score <= 0.51) // ~0.5
        | _ -> Assert.Fail("Expected Partial verdict")
