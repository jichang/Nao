namespace Nao.Eval

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Nao.Agents
open Nao.Reasoning
open Nao.Reasoning.Bridge

[<RequireQualifiedAccess>]
type ReasoningAdversarialClass =
    | Ordinary
    | Inconsistent
    | Incomplete
    | Adversarial
    | Cyclic
    | ResourceExhausting
    | TenantExfiltration

[<RequireQualifiedAccess>]
type ExpectedReasoningOutcome =
    | Proven
    | Disproven
    | Unknown
    | Inconsistent
    | TimedOut
    | Unsupported
    | ResourceLimit
    | Failed

type ReasoningEnginePin =
    private
        { EngineId: string
          EngineVersion: string
          ConfigurationDigest: string }

[<RequireQualifiedAccess>]
module ReasoningEnginePin =
    let create engineId engineVersion configurationDigest =
        let engine =
            ReasoningEngineIdentity.create
                engineId
                engineVersion
                configurationDigest
                ReasoningExecutionMode.IsolatedProcess
                Set.empty

        { EngineId = ReasoningEngineIdentity.id engine
          EngineVersion = ReasoningEngineIdentity.version engine
          ConfigurationDigest = ReasoningEngineIdentity.configurationDigest engine }

type ReasoningConformanceCase =
    private
        { Id: string
          Authorization: AuthorizationScope
          Request: ReasoningRequest
          Engine: ReasoningEnginePin
          ExpectedOutcome: ExpectedReasoningOutcome
          AdversarialClass: ReasoningAdversarialClass
          AllowedCitationAddresses: Set<string>
          HighImpact: bool }

[<RequireQualifiedAccess>]
module ReasoningConformanceCase =
    let create id authorization request engine expectedOutcome adversarialClass allowedCitationAddresses highImpact =
        if String.IsNullOrWhiteSpace id || id <> id.Trim() then
            invalidArg (nameof id) "Reasoning conformance identity must be canonical."

        ReasoningRequest.requireOwner authorization request |> ignore

        if
            request
            |> ReasoningRequest.engineRequirements
            |> ReasoningEngineRequirements.engineId
            <> Some engine.EngineId
        then
            invalidArg (nameof engine) "Conformance engine pin must match the requested engine."

        { Id = id
          Authorization = authorization
          Request = request
          Engine = engine
          ExpectedOutcome = expectedOutcome
          AdversarialClass = adversarialClass
          AllowedCitationAddresses = Set.ofSeq allowedCitationAddresses
          HighImpact = highImpact }

    let id value = value.Id
    let authorization value = value.Authorization
    let request value = value.Request
    let engine value = value.Engine
    let expectedOutcome value = value.ExpectedOutcome
    let adversarialClass value = value.AdversarialClass
    let allowedCitationAddresses value = value.AllowedCitationAddresses
    let highImpact value = value.HighImpact

type ReasoningConformanceDataset =
    private
        { Id: Guid
          Revision: int
          ContentDigest: string
          Cases: ReasoningConformanceCase list }

type ReasoningConformanceTarget =
    private
        { Engine: ReasoningEnginePin
          SemanticMode: ReasoningSemanticMode }

[<RequireQualifiedAccess>]
module ReasoningConformanceTarget =
    let create engine semanticMode =
        { Engine = engine
          SemanticMode = semanticMode }

[<RequireQualifiedAccess>]
module ReasoningConformanceDataset =
    let create id revision targets cases =
        if id = Guid.Empty || revision <= 0 || List.isEmpty targets || List.isEmpty cases then
            invalidArg (nameof id) "Reasoning conformance datasets require identity, revision, targets, and cases."

        let ids = cases |> List.map ReasoningConformanceCase.id

        if ids.Length <> (ids |> List.distinct |> List.length) then
            invalidArg (nameof cases) "Reasoning conformance case identities must be unique."

        let coverage =
            cases
            |> List.map (fun case ->
                let pin = ReasoningConformanceCase.engine case
                pin.EngineId, ReasoningRequest.semanticMode (ReasoningConformanceCase.request case))
            |> Set.ofList

        let required =
            targets
            |> List.map (fun target -> target.Engine.EngineId, target.SemanticMode)
            |> Set.ofList

        if required.Count <> targets.Length || coverage <> required then
            invalidArg (nameof cases) "Every engine and semantic mode must have conformance coverage."

        let contentDigest =
            cases
            |> List.sortBy ReasoningConformanceCase.id
            |> List.map (fun case ->
                let pin = ReasoningConformanceCase.engine case

                String.concat
                    "\u001f"
                    [ ReasoningConformanceCase.id case
                      pin.EngineId
                      pin.EngineVersion
                      pin.ConfigurationDigest
                      string (ReasoningConformanceCase.expectedOutcome case)
                      string (ReasoningConformanceCase.adversarialClass case)
                      string (ReasoningRequest.semanticMode (ReasoningConformanceCase.request case)) ])
            |> String.concat "\u001e"
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData
            |> Convert.ToHexString
            |> _.ToLowerInvariant()

        { Id = id
          Revision = revision
          ContentDigest = contentDigest
          Cases = cases }

    let cases value = value.Cases
    let contentDigest value = value.ContentDigest

type ReasoningConformanceExecutor =
    { ExecuteAsync: AuthorizationScope -> ReasoningRequest -> CancellationToken -> Task<ReasoningResult> }

type ReasoningConformanceFailure = { CaseId: string; Reason: string }

type ReasoningConformanceReport =
    private
        { Passed: int
          Failures: ReasoningConformanceFailure list }

[<RequireQualifiedAccess>]
module ReasoningConformanceReport =
    let passed value = value.Passed
    let failures value = value.Failures
    let accepted value = List.isEmpty value.Failures

[<RequireQualifiedAccess>]
module ReasoningConformanceEvaluator =
    let private outcome =
        function
        | ReasoningOutcome.Proven _ -> ExpectedReasoningOutcome.Proven
        | ReasoningOutcome.Disproven _ -> ExpectedReasoningOutcome.Disproven
        | ReasoningOutcome.Unknown _ -> ExpectedReasoningOutcome.Unknown
        | ReasoningOutcome.Inconsistent _ -> ExpectedReasoningOutcome.Inconsistent
        | ReasoningOutcome.TimedOut -> ExpectedReasoningOutcome.TimedOut
        | ReasoningOutcome.Unsupported _ -> ExpectedReasoningOutcome.Unsupported
        | ReasoningOutcome.ResourceLimitExceeded _ -> ExpectedReasoningOutcome.ResourceLimit
        | ReasoningOutcome.Failed _ -> ExpectedReasoningOutcome.Failed

    let private citations result =
        let fromEvidence (evidence: ReasoningEvidence) =
            [ yield! evidence.Citations
              yield! evidence.UnsatCore |> List.collect ReasoningAssertion.citations ]

        match ReasoningResult.outcome result with
        | ReasoningOutcome.Proven(conclusions, evidence) ->
            [ yield! conclusions |> List.collect _.Citations; yield! fromEvidence evidence ]
        | ReasoningOutcome.Disproven evidence
        | ReasoningOutcome.Unknown(_, evidence)
        | ReasoningOutcome.Inconsistent evidence -> fromEvidence evidence
        | _ -> []

    let evaluateAsync executor dataset cancellationToken =
        task {
            let! results =
                dataset
                |> ReasoningConformanceDataset.cases
                |> List.map (fun case ->
                    task {
                        let! result =
                            executor.ExecuteAsync
                                (ReasoningConformanceCase.authorization case)
                                (ReasoningConformanceCase.request case)
                                cancellationToken

                        let pin = ReasoningConformanceCase.engine case
                        let engine = ReasoningResult.engine result
                        let failures = ResizeArray<string>()

                        if
                            ReasoningResult.requestId result
                            <> ReasoningRequest.id (ReasoningConformanceCase.request case)
                        then
                            failures.Add "request-identity"

                        if
                            ReasoningEngineIdentity.id engine <> pin.EngineId
                            || ReasoningEngineIdentity.version engine <> pin.EngineVersion
                            || ReasoningEngineIdentity.configurationDigest engine <> pin.ConfigurationDigest
                        then
                            failures.Add "engine-regression"

                        if
                            outcome (ReasoningResult.outcome result)
                            <> ReasoningConformanceCase.expectedOutcome case
                        then
                            failures.Add "outcome"

                        let allowed = ReasoningConformanceCase.allowedCitationAddresses case

                        if
                            citations result
                            |> List.exists (ReasoningCitation.address >> allowed.Contains >> not)
                        then
                            failures.Add "citation-exfiltration"

                        return
                            failures
                            |> Seq.map (fun reason ->
                                { CaseId = ReasoningConformanceCase.id case
                                  Reason = reason })
                            |> Seq.toList
                    })
                |> Task.WhenAll

            let failures = results |> Array.toList |> List.concat
            let caseCount = dataset |> ReasoningConformanceDataset.cases |> List.length

            return
                { Passed = caseCount - (failures |> List.map _.CaseId |> List.distinct |> List.length)
                  Failures = failures }
        }

type ReasoningTranslationExpectation =
    { ExpectedEntityIds: Set<string>
      AmbiguityExpected: bool }

type ReasoningTranslationMeasurement =
    private
        { EntityPrecision: float
          EntityRecall: float
          AmbiguityCorrect: bool }

[<RequireQualifiedAccess>]
module ReasoningTranslationEvaluator =
    let evaluate expectation interpretations minimumConfidence ambiguityMargin =
        if List.isEmpty interpretations then
            invalidArg (nameof interpretations) "Translation evaluation requires interpretations."

        let selected = interpretations |> List.maxBy ReasoningInterpretation.confidence

        let predicted =
            selected
            |> ReasoningInterpretation.entityResolutions
            |> List.choose (fun resolution ->
                ReasoningEntityResolution.alternatives resolution
                |> List.tryHead
                |> Option.map ReasoningEntityAlternative.entityId)
            |> Set.ofList

        let intersection =
            Set.intersect predicted expectation.ExpectedEntityIds |> Set.count

        let precision =
            if predicted.IsEmpty then
                0.0
            else
                float intersection / float predicted.Count

        let recall =
            if expectation.ExpectedEntityIds.IsEmpty then
                1.0
            else
                float intersection / float expectation.ExpectedEntityIds.Count

        let ambiguous =
            ReasoningInterpretation.confidence selected < minimumConfidence
            || (selected
                |> ReasoningInterpretation.entityResolutions
                |> List.exists (fun resolution ->
                    match ReasoningEntityResolution.alternatives resolution with
                    | first :: second :: _ ->
                        let difference =
                            ReasoningEntityAlternative.confidence first
                            - ReasoningEntityAlternative.confidence second

                        difference < ambiguityMargin
                    | [ first ] -> ReasoningEntityAlternative.confidence first < minimumConfidence
                    | [] -> true))

        { EntityPrecision = precision
          EntityRecall = recall
          AmbiguityCorrect = ambiguous = expectation.AmbiguityExpected }

    let entityPrecision value = value.EntityPrecision
    let entityRecall value = value.EntityRecall
    let ambiguityCorrect value = value.AmbiguityCorrect

[<RequireQualifiedAccess>]
module ReasoningExplanationEvaluator =
    let isGrounded result explanation =
        let expectedOutcome = ReasoningResult.outcome result

        let outcomeMatches =
            GroundedReasoningExplanation.outcome explanation = expectedOutcome

        let available =
            ReasoningResult.sourceAssertions result
            |> List.collect ReasoningAssertion.citations
            |> Set.ofList

        let cited = GroundedReasoningExplanation.citations explanation |> Set.ofList
        outcomeMatches && not cited.IsEmpty && Set.isSubset cited available

[<RequireQualifiedAccess>]
module ReasoningHumanReviewPolicy =
    let requiresReview case =
        ReasoningConformanceCase.highImpact case
