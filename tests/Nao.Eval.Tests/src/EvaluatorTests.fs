namespace Nao.Eval.Tests

open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval
open Nao.Eval.Evaluators

module private CapturingProvider =
    let create response (capturedPrompt: string ref) (capturedCorrelation: CorrelationContext option ref) =
        LlmProvider.create (fun () -> "capturing") (fun correlation conversation _options _ ->
            capturedCorrelation.Value <- Some correlation
            capturedPrompt.Value <- conversation |> List.last |> _.Content

            Task.FromResult(
                { Content = response
                  FinishReason = "stop"
                  TokensUsed = None
                  Usage = None }
                : CompletionResult
            ))

module private Evaluation =
    let context () =
        let principal =
            SecurityPrincipal.createAuthenticated
                (TenantId.parse "eval-tenant")
                (UserId.parse "eval-user")
                (Some(ServiceId.parse "eval-service"))
                []
                [ "private-role" ]
                (Map.ofList [ "private-claim", "private-claim-value" ])
                AuthenticationStrength.MultiFactor

        { Authorization =
            AuthorizationScope.tryCreate principal None (WorkspaceId.parse "eval-workspace") None
            |> Option.get
          Correlation = CorrelationContext.root ()
          CancellationToken = System.Threading.CancellationToken.None }

    let evaluate (evaluator: Evaluator) case actual =
        evaluator.EvaluateAsync (context ()) case actual

open Evaluation

[<TestClass>]
type ExactMatchTests() =

    [<TestMethod>]
    member _.``ExactMatch passes when output matches expected``() =
        let case = EvalCase.create "test1" "input" "hello world"
        let (verdict, _) = (evaluate ExactMatch.evaluator case "Hello World").Result
        Assert.AreEqual(EvalVerdict.Pass, verdict)

    [<TestMethod>]
    member _.``ExactMatch fails when output differs``() =
        let case = EvalCase.create "test2" "input" "hello"
        let (verdict, _) = (evaluate ExactMatch.evaluator case "goodbye").Result
        Assert.AreEqual(EvalVerdict.Fail, verdict)

    [<TestMethod>]
    member _.``ExactMatch case sensitive respects casing``() =
        let case = EvalCase.create "test3" "input" "Hello"
        let (verdict, _) = (evaluate ExactMatch.caseSensitive case "hello").Result
        Assert.AreEqual(EvalVerdict.Fail, verdict)

[<TestClass>]
type ContainsTests() =

    [<TestMethod>]
    member _.``Contains passes when output includes expected``() =
        let case = EvalCase.create "test1" "input" "weather"

        let (verdict, _) =
            (evaluate Contains.evaluator case "The weather today is sunny").Result

        Assert.AreEqual(EvalVerdict.Pass, verdict)

    [<TestMethod>]
    member _.``Contains fails when output lacks expected``() =
        let case = EvalCase.create "test2" "input" "rain"

        let (verdict, _) = (evaluate Contains.evaluator case "The weather is sunny").Result

        Assert.AreEqual(EvalVerdict.Fail, verdict)

    [<TestMethod>]
    member _.``ContainsAll gives partial score for partial matches``() =
        let evaluator = Contains.all [ "hello"; "world"; "foo" ]
        let case = EvalCase.create "test3" "input" ""
        let (verdict, _) = (evaluate evaluator case "hello world").Result

        match verdict with
        | EvalVerdict.Partial score -> Assert.IsTrue(score > 0.6 && score < 0.7) // 2/3
        | _ -> Assert.Fail("Expected Partial verdict")

    [<TestMethod>]
    member _.``ContainsAll passes when all keywords present``() =
        let evaluator = Contains.all [ "hello"; "world" ]
        let case = EvalCase.create "test4" "input" ""
        let (verdict, _) = (evaluate evaluator case "hello beautiful world").Result
        Assert.AreEqual(EvalVerdict.Pass, verdict)

    [<TestMethod>]
    member _.``ContainsAny passes when any keyword present``() =
        let evaluator = Contains.any [ "cat"; "dog"; "fish" ]
        let case = EvalCase.create "test5" "input" ""
        let (verdict, _) = (evaluate evaluator case "I have a dog").Result
        Assert.AreEqual(EvalVerdict.Pass, verdict)

    [<TestMethod>]
    member _.``ContainsAny fails when no keywords present``() =
        let evaluator = Contains.any [ "cat"; "dog"; "fish" ]
        let case = EvalCase.create "test6" "input" ""
        let (verdict, _) = (evaluate evaluator case "I have a bird").Result
        Assert.AreEqual(EvalVerdict.Fail, verdict)

[<TestClass>]
type RegexTests() =

    [<TestMethod>]
    member _.``Regex passes when pattern matches``() =
        let evaluator = RegexEval.matches @"\d+\.\d+"
        let case = EvalCase.create "test1" "input" ""

        let (verdict, _) =
            (evaluate evaluator case "The temperature is 72.5 degrees").Result

        Assert.AreEqual(EvalVerdict.Pass, verdict)

    [<TestMethod>]
    member _.``Regex fails when pattern does not match``() =
        let evaluator = RegexEval.matches @"^\d+$"
        let case = EvalCase.create "test2" "input" ""
        let (verdict, _) = (evaluate evaluator case "not a number").Result
        Assert.AreEqual(EvalVerdict.Fail, verdict)

[<TestClass>]
type LlmJudgeTests() =

    [<TestMethod>]
    member _.``Prompt example and parser share the DTO contract``() =
        let capturedPrompt = ref ""
        let capturedCorrelation = ref None
        let context = context ()

        let provider =
            CapturingProvider.create """{"score":5,"reason":"correct"}""" capturedPrompt capturedCorrelation

        let evaluator = LlmJudge.create provider
        let case = EvalCase.create "judge" "question" "answer"

        let verdict, reason = evaluator.EvaluateAsync context case "answer" |> _.Result

        Assert.AreEqual(EvalVerdict.Pass, verdict)
        Assert.AreEqual("correct", reason)
        Assert.AreEqual(Some context.Correlation, capturedCorrelation.Value)
        StringAssert.Contains(capturedPrompt.Value, """{"score":5,"reason":"brief explanation"}""")
        Assert.IsFalse(capturedPrompt.Value.Contains("private-role"))
        Assert.IsFalse(capturedPrompt.Value.Contains("private-claim"))
        Assert.IsFalse(capturedPrompt.Value.Contains("eval-service"))

    [<TestMethod>]
    member _.``Missing required response field fails``() =
        let capturedPrompt = ref ""
        let capturedCorrelation = ref None

        let provider =
            CapturingProvider.create """{"score":5}""" capturedPrompt capturedCorrelation

        let evaluator = LlmJudge.create provider
        let case = EvalCase.create "judge" "question" "answer"

        let verdict, reason = evaluate evaluator case "answer" |> _.Result

        Assert.AreEqual(EvalVerdict.Fail, verdict)
        StringAssert.StartsWith(reason, "Parse error:")

[<TestClass>]
type VerificationJudgeTests() =

    [<TestMethod>]
    member _.``Functional judge preserves partial score and criteria evidence``() =
        let judge =
            Judge.create "quality" (fun _trace ->
                Task.FromResult
                    { Verdict = JudgementVerdict.Partial 0.65
                      Explanation = "Mostly correct"
                      CriteriaScores = Map.ofList [ "grounding", 0.75 ]
                      Suggestions = [ "Cite the source" ]
                      JudgeName = "quality" })

        let evaluator = VerificationJudge.fromJudge judge "agent"
        let case = EvalCase.create "judge" "question" "reference"

        let verdict, reason = evaluate evaluator case "answer" |> _.Result

        Assert.AreEqual(EvalVerdict.Partial 0.65, verdict)
        Assert.AreEqual("Mostly correct [Criteria: grounding=0.75]", reason)

[<TestClass>]
type CompositeTests() =

    [<TestMethod>]
    member _.``Composite forwards authenticated context to every evaluator``() =
        let context = context ()
        let mutable count = 0

        let capture name =
            Evaluator.create name (fun actualContext _ _ ->
                Assert.AreEqual(context, actualContext)
                System.Threading.Interlocked.Increment(&count) |> ignore
                Task.FromResult(EvalVerdict.Pass, "ok"))

        let composite =
            Composite.all [ capture "first"; Composite.any [ capture "second" ] ]

        let verdict, _ =
            composite.EvaluateAsync context (EvalCase.create "case" "input" "answer") "answer"
            |> _.Result

        Assert.AreEqual(EvalVerdict.Pass, verdict)
        Assert.AreEqual(2, count)

    [<TestMethod>]
    member _.``Cancelled context prevents evaluator invocation``() =
        use cancellation = new System.Threading.CancellationTokenSource()
        cancellation.Cancel()

        let context =
            { context () with
                CancellationToken = cancellation.Token }

        let evaluator =
            Evaluator.create "must-not-run" (fun _ _ _ ->
                Assert.Fail "Cancelled evaluator must not execute."
                Task.FromResult(EvalVerdict.Pass, "unexpected"))

        Assert.ThrowsExactlyAsync<System.OperationCanceledException>(fun () ->
            evaluator.EvaluateAsync context (EvalCase.create "case" "input" "answer") "answer" :> Task)
        |> _.Wait()

    [<TestMethod>]
    member _.``Composite All passes when all evaluators pass``() =
        let evaluator = Composite.all [ Contains.all [ "hello" ]; RegexEval.matches @"\w+" ]
        let case = EvalCase.create "test1" "input" ""
        let (verdict, _) = (evaluate evaluator case "hello world").Result
        Assert.AreEqual(EvalVerdict.Pass, verdict)

    [<TestMethod>]
    member _.``Composite All gives partial when some fail``() =
        let evaluator =
            Composite.all [ Contains.all [ "hello" ]; Contains.all [ "missing" ] ]

        let case = EvalCase.create "test2" "input" ""
        let (verdict, _) = (evaluate evaluator case "hello world").Result

        match verdict with
        | EvalVerdict.Partial _ -> ()
        | _ -> Assert.Fail("Expected Partial verdict")

    [<TestMethod>]
    member _.``Composite Any passes when at least one passes``() =
        let evaluator =
            Composite.any [ Contains.all [ "missing" ]; Contains.all [ "hello" ] ]

        let case = EvalCase.create "test3" "input" ""
        let (verdict, _) = (evaluate evaluator case "hello world").Result
        Assert.AreEqual(EvalVerdict.Pass, verdict)

    [<TestMethod>]
    member _.``Composite Average computes correct score``() =
        let evaluator =
            Composite.average
                [ Contains.all [ "hello"; "world" ] // Both present -> 1.0
                  Contains.all [ "foo"; "bar" ] ] // Neither present -> 0.0

        let case = EvalCase.create "test4" "input" ""
        let (verdict, _) = (evaluate evaluator case "hello world").Result

        match verdict with
        | EvalVerdict.Partial score -> Assert.IsTrue(score >= 0.49 && score <= 0.51) // ~0.5
        | _ -> Assert.Fail("Expected Partial verdict")
