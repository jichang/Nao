namespace Nao.Eval.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval
open Nao.Reasoning
open Nao.Reasoning.Bridge

[<TestClass>]
type ReasoningSafetyEvaluationTests() =
    let authorization tenant =
        let principal =
            SecurityPrincipal.create (TenantId.parse tenant) (UserId.parse "user") []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    let scope = authorization "tenant"
    let citation = ReasoningCitation.create "fixture:allowed" "v1" None

    let semanticMode =
        ReasoningSemanticMode.create
            ReasoningWorldAssumption.ClosedWorld
            ReasoningNegationSemantics.Classical
            ReasoningMonotonicity.Monotonic
            Set.empty
            Map.empty

    let requestWithMode authorization mode engineId =
        ReasoningRequest.create
            authorization
            (Guid.NewGuid())
            [ ReasoningAssertion.create "fact" (ReasoningArtifact.create "text/plain" "fact") [ citation ] ]
            mode
            (ReasoningArtifact.create "text/plain" "query")
            (ReasoningEngineRequirements.create
                (Some engineId)
                Set.empty
                (Set.singleton ReasoningExecutionMode.IsolatedProcess)
                false
                false)
            (ReasoningLimits.create (TimeSpan.FromSeconds 1.0) 1000L 100L 10 10 10 1000 0)
            [ ReasoningInputIdentity.create "problem" "p" "1" (String('a', 64)) ]

    let request authorization =
        requestWithMode authorization semanticMode "z3"

    let result request version citations =
        ReasoningResult.create
            request
            (ReasoningEngineIdentity.create
                "z3"
                version
                (String('b', 64))
                ReasoningExecutionMode.IsolatedProcess
                Set.empty)
            (ReasoningOutcome.Proven(
                [ { Artifact = ReasoningArtifact.create "text/plain" "true"
                    Citations = citations } ],
                ReasoningEvidence.Empty
            ))
            { WallTime = TimeSpan.Zero
              CpuTime = None
              PeakMemoryBytes = None
              Steps = Some 1L
              Conclusions = 1
              ProofSteps = 0
              ResponseBytes = None
              ExternalCalls = Some 0 }
            { TraceId = Guid.NewGuid()
              StartedAt = DateTimeOffset.UtcNow
              CompletedAt = DateTimeOffset.UtcNow
              CacheHit = false }
            []

    [<TestMethod>]
    member _.ConformancePinsEngineSemanticsOutcomeAndCitationBoundary() =
        let reasoningRequest = request scope
        let pin = ReasoningEnginePin.create "z3" "4.12.2" (String('b', 64))

        let case =
            ReasoningConformanceCase.create
                "resource-adversary"
                scope
                reasoningRequest
                pin
                ExpectedReasoningOutcome.Proven
                ReasoningAdversarialClass.ResourceExhausting
                [ ReasoningCitation.address citation ]
                true

        let target = ReasoningConformanceTarget.create pin semanticMode

        let dataset =
            ReasoningConformanceDataset.create (Guid.NewGuid()) 1 [ target ] [ case ]

        Assert.AreEqual(64, ReasoningConformanceDataset.contentDigest dataset |> String.length)
        Assert.IsTrue(ReasoningHumanReviewPolicy.requiresReview case)

        let evaluator =
            { ExecuteAsync = fun _ value _ -> Task.FromResult(result value "4.12.2" [ citation ]) }

        let report =
            ReasoningConformanceEvaluator.evaluateAsync evaluator dataset CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Assert.IsTrue(ReasoningConformanceReport.accepted report)

        let drifted =
            { ExecuteAsync =
                fun _ value _ ->
                    Task.FromResult(result value "4.13.0" [ ReasoningCitation.create "tenant:other-secret" "v1" None ]) }

        let rejected =
            ReasoningConformanceEvaluator.evaluateAsync drifted dataset CancellationToken.None
            |> _.GetAwaiter().GetResult()

        let reasons = rejected |> ReasoningConformanceReport.failures |> List.map _.Reason

        Assert.IsTrue(List.contains "engine-regression" reasons)
        Assert.IsTrue(List.contains "citation-exfiltration" reasons)

    [<TestMethod>]
    member _.TranslationAmbiguityIsMeasuredSeparatelyFromSolverCorrectness() =
        let schema =
            ReasoningSchema.create
                scope
                "schema"
                "1"
                (String('c', 64))
                (ReasoningArtifact.create "text/plain" "vocabulary")
                (ReasoningArtifact.create "text/plain" "query")

        let candidate =
            ReasoningInterpretation.create
                (Guid.NewGuid())
                schema
                (ReasoningArtifact.create "text/plain" "formal")
                0.9
                [ ReasoningEntityResolution.create
                      "bank"
                      [ ReasoningEntityAlternative.create "financial-bank" "bank" 0.52 [ citation ]
                        ReasoningEntityAlternative.create "river-bank" "bank" 0.48 [ citation ] ] ]
                [ citation ]

        let measurement =
            ReasoningTranslationEvaluator.evaluate
                { ExpectedEntityIds = Set.singleton "financial-bank"
                  AmbiguityExpected = true }
                [ candidate ]
                0.7
                0.1

        Assert.AreEqual(1.0, ReasoningTranslationEvaluator.entityPrecision measurement)
        Assert.AreEqual(1.0, ReasoningTranslationEvaluator.entityRecall measurement)
        Assert.IsTrue(ReasoningTranslationEvaluator.ambiguityCorrect measurement)

    [<TestMethod>]
    member _.DatasetMaintainsAllReasoningAdversarialClassesAgainstPinnedSemantics() =
        let z3 = ReasoningEnginePin.create "z3" "4.12.2" (String('b', 64))
        let souffle = ReasoningEnginePin.create "souffle" "2.5.0" (String('c', 64))
        let elk = ReasoningEnginePin.create "elk" "0.6.0" (String('d', 64))

        let datalogMode =
            ReasoningSemanticMode.create
                ReasoningWorldAssumption.ClosedWorld
                ReasoningNegationSemantics.NegationAsFailure
                ReasoningMonotonicity.NonMonotonic
                Set.empty
                Map.empty

        let owlMode =
            ReasoningSemanticMode.create
                ReasoningWorldAssumption.OpenWorld
                ReasoningNegationSemantics.Classical
                ReasoningMonotonicity.Monotonic
                Set.empty
                Map.empty

        let classes =
            [ ReasoningAdversarialClass.Ordinary
              ReasoningAdversarialClass.Inconsistent
              ReasoningAdversarialClass.Incomplete
              ReasoningAdversarialClass.Adversarial
              ReasoningAdversarialClass.Cyclic
              ReasoningAdversarialClass.ResourceExhausting
              ReasoningAdversarialClass.TenantExfiltration ]

        let cases =
            classes
            |> List.mapi (fun index adversarialClass ->
                let pin, mode, engineId =
                    match index % 3 with
                    | 0 -> z3, semanticMode, "z3"
                    | 1 -> souffle, datalogMode, "souffle"
                    | _ -> elk, owlMode, "elk"

                ReasoningConformanceCase.create
                    $"case-{index}"
                    scope
                    (requestWithMode scope mode engineId)
                    pin
                    ExpectedReasoningOutcome.Proven
                    adversarialClass
                    [ ReasoningCitation.address citation ]
                    false)

        let first =
            ReasoningConformanceDataset.create
                (Guid.Parse "55555555-5555-5555-5555-555555555555")
                1
                [ ReasoningConformanceTarget.create z3 semanticMode
                  ReasoningConformanceTarget.create souffle datalogMode
                  ReasoningConformanceTarget.create elk owlMode ]
                cases

        let second =
            ReasoningConformanceDataset.create
                (Guid.Parse "55555555-5555-5555-5555-555555555555")
                1
                [ ReasoningConformanceTarget.create z3 semanticMode
                  ReasoningConformanceTarget.create souffle datalogMode
                  ReasoningConformanceTarget.create elk owlMode ]
                cases

        Assert.AreEqual(
            ReasoningConformanceDataset.contentDigest first,
            ReasoningConformanceDataset.contentDigest second
        )

    [<TestMethod>]
    member _.ConformanceCasesCannotCrossTenantBoundaries() =
        let foreign = authorization "foreign"

        Assert.ThrowsExactly<UnauthorizedAccessException>(fun () ->
            ReasoningConformanceCase.create
                "cross-tenant"
                foreign
                (request scope)
                (ReasoningEnginePin.create "z3" "4.12.2" (String('b', 64)))
                ExpectedReasoningOutcome.Proven
                ReasoningAdversarialClass.TenantExfiltration
                []
                false
            |> ignore)
        |> ignore
