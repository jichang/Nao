namespace Nao.Reasoning.Bridge.Tests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Eval
open Nao.Reasoning
open Nao.Reasoning.Bridge

[<TestClass>]
type ReasoningBridgeTests() =
    let authorizationFor user =
        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse user) []

        AuthorizationScope.tryCreate principal None (WorkspaceId.parse "workspace") None
        |> Option.get

    let authorization = authorizationFor "user"
    let reviewerAuthorization = authorizationFor "reviewer"
    let citation = ReasoningCitation.create "fixture:fact" "v1" None

    let schema =
        ReasoningSchema.create
            authorization
            "schema"
            "1"
            (String('a', 64))
            (ReasoningArtifact.create "text/plain" "entity")
            (ReasoningArtifact.create "application/sparql-query" "ASK {}")

    let entity id confidence =
        ReasoningEntityAlternative.create id id confidence [ citation ]

    let interpretation confidence alternatives =
        ReasoningInterpretation.create
            (Guid.NewGuid())
            schema
            (ReasoningArtifact.create "application/sparql-query" "ASK {}")
            confidence
            [ ReasoningEntityResolution.create "it" alternatives ]
            [ citation ]

    let limits =
        ReasoningLimits.create (TimeSpan.FromSeconds 1.0) 1000L 100L 10 10 10 1000 0

    let semanticMode =
        ReasoningSemanticMode.create
            ReasoningWorldAssumption.OpenWorld
            ReasoningNegationSemantics.Classical
            ReasoningMonotonicity.Monotonic
            Set.empty
            Map.empty

    let request id =
        let assertion =
            ReasoningAssertion.create "fact" (ReasoningArtifact.create "text/plain" "fact") [ citation ]

        ReasoningRequest.create
            authorization
            id
            [ assertion ]
            semanticMode
            (ReasoningArtifact.create "application/sparql-query" "ASK {}")
            (ReasoningEngineRequirements.create
                (Some "engine")
                Set.empty
                (Set.singleton ReasoningExecutionMode.IsolatedProcess)
                false
                false)
            limits
            [ ReasoningInputIdentity.create "schema" "schema" "1" (String('a', 64)) ]

    let formalResult request =
        let engine =
            ReasoningEngineIdentity.create
                "engine"
                "1"
                (String('b', 64))
                ReasoningExecutionMode.IsolatedProcess
                Set.empty

        let artifact = ReasoningArtifact.create "text/plain" "derived"

        ReasoningResult.create
            request
            engine
            (ReasoningOutcome.Proven(
                [ { Artifact = artifact
                    Citations = [ citation ] } ],
                ReasoningEvidence.Empty
            ))
            { WallTime = TimeSpan.Zero
              CpuTime = None
              PeakMemoryBytes = None
              Steps = None
              Conclusions = 1
              ProofSteps = 0
              ResponseBytes = None
              ExternalCalls = Some 0 }
            { TraceId = Guid.NewGuid()
              StartedAt = DateTimeOffset.UtcNow
              CompletedAt = DateTimeOffset.UtcNow
              CacheHit = false }
            [ artifact ]

    let discovery = { DiscoverAsync = fun _ _ -> Task.FromResult schema }

    let translator interpretations =
        { TranslateAsync = fun _ _ _ _ -> Task.FromResult interpretations }

    let explainer addresses =
        { GenerateAsync =
            fun _ _ _ ->
                Task.FromResult
                    { Narrative = "Interpretation: ASK. Formal conclusion: proven."
                      CitationAddresses = addresses } }

    let policy = ReasoningBridgePolicy.create 0.7 0.1 true

    [<TestMethod>]
    member _.AmbiguousInterpretationsRequireClarificationBeforeValidation() =
        let mutable validations = 0
        let mutable executions = 0
        let candidate = interpretation 0.9 [ entity "a" 0.51; entity "b" 0.49 ]

        let validator =
            { ValidateAsync =
                fun _ _ _ _ ->
                    validations <- validations + 1
                    Task.FromResult(Invalid("unexpected", "unexpected")) }

        let executor _ _ _ =
            executions <- executions + 1
            Task.FromException<ReasoningResult>(InvalidOperationException())

        let result =
            ReasoningBridgePipeline.executeAsync
                discovery
                (translator [ candidate ])
                validator
                executor
                (explainer [])
                policy
                authorization
                "Is it valid?"
                None
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        match result with
        | ReasoningBridgeOutcome.ClarificationRequired _ -> ()
        | outcome -> Assert.Fail $"Expected clarification, got {outcome}."

        Assert.AreEqual(0, validations)
        Assert.AreEqual(0, executions)

    [<TestMethod>]
    member _.InvalidCandidatesNeverReachTheReasoner() =
        let mutable executions = 0
        let candidate = interpretation 0.9 [ entity "a" 0.9 ]

        let validator =
            { ValidateAsync = fun _ _ _ _ -> Task.FromResult(Invalid("schema", "not allowed")) }

        let executor _ _ _ =
            executions <- executions + 1
            Task.FromException<ReasoningResult>(InvalidOperationException())

        match
            ReasoningBridgePipeline.executeAsync
                discovery
                (translator [ candidate ])
                validator
                executor
                (explainer [])
                policy
                authorization
                "Is it valid?"
                None
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
        with
        | ReasoningBridgeOutcome.Rejected [ (_, "schema", "not allowed") ] -> ()
        | outcome -> Assert.Fail $"Expected rejection, got {outcome}."

        Assert.AreEqual(0, executions)

    [<TestMethod>]
    member _.HighImpactRequestsExposeFormalPreviewAndRequireMatchingApproval() =
        let candidate = interpretation 0.9 [ entity "a" 0.9 ]
        let reasoningRequest = request (Guid.NewGuid())

        let validator =
            { ValidateAsync =
                fun _ _ value _ ->
                    Task.FromResult(
                        Valid(
                            ValidatedReasoningRequest.create
                                value
                                reasoningRequest
                                (ReasoningImpact.HighImpact "employment")
                        )
                    ) }

        let run approval =
            ReasoningBridgePipeline.executeAsync
                discovery
                (translator [ candidate ])
                validator
                (fun _ value _ -> Task.FromResult(formalResult value))
                (explainer [ ReasoningCitation.address citation ])
                policy
                authorization
                "Is it valid?"
                approval
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        match run None with
        | ReasoningBridgeOutcome.ReviewRequired preview ->
            Assert.AreEqual(
                ReasoningInterpretation.formalRequest candidate,
                preview
                |> ValidatedReasoningRequest.interpretation
                |> ReasoningInterpretation.formalRequest
            )
        | outcome -> Assert.Fail $"Expected review, got {outcome}."

        let approval =
            ReasoningReviewApproval.create
                candidate
                authorization
                reviewerAuthorization
                DateTimeOffset.UtcNow
                "Approved formal query."

        match run (Some approval) with
        | ReasoningBridgeOutcome.Completed(result, explanation) ->
            Assert.IsTrue(ReasoningExplanationEvaluator.isGrounded result explanation)

            match ReasoningResult.outcome result, GroundedReasoningExplanation.outcome explanation with
            | ReasoningOutcome.Proven _, ReasoningOutcome.Proven _ -> ()
            | outcomes -> Assert.Fail $"Expected formal proven outcomes, got {outcomes}."
        | outcome -> Assert.Fail $"Expected completion, got {outcome}."

    [<TestMethod>]
    member _.ExplanationCannotFabricateEvidence() =
        let candidate = interpretation 0.9 [ entity "a" 0.9 ]
        let reasoningRequest = request (Guid.NewGuid())

        let validator =
            { ValidateAsync =
                fun _ _ value _ ->
                    Task.FromResult(
                        Valid(ValidatedReasoningRequest.create value reasoningRequest ReasoningImpact.Ordinary)
                    ) }

        Assert.ThrowsExactly<InvalidOperationException>(fun () ->
            ReasoningBridgePipeline.executeAsync
                discovery
                (translator [ candidate ])
                validator
                (fun _ value _ -> Task.FromResult(formalResult value))
                (explainer [ "fabricated:evidence" ])
                policy
                authorization
                "Is it valid?"
                None
                CancellationToken.None
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore
