namespace Nao.Reasoning.Bridge

#nowarn "3511"

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents
open Nao.Reasoning

[<RequireQualifiedAccess>]
module ReasoningBridgePipeline =
    let private evidence =
        function
        | ReasoningOutcome.Proven(conclusions, evidence) -> conclusions |> List.collect _.Citations, evidence
        | ReasoningOutcome.Disproven evidence
        | ReasoningOutcome.Unknown(_, evidence)
        | ReasoningOutcome.Inconsistent evidence -> [], evidence
        | ReasoningOutcome.TimedOut
        | ReasoningOutcome.Unsupported _
        | ReasoningOutcome.ResourceLimitExceeded _
        | ReasoningOutcome.Failed _ -> [], ReasoningEvidence.Empty

    let private availableCitations result =
        let conclusionCitations, outcomeEvidence =
            result |> ReasoningResult.outcome |> evidence

        [ yield! conclusionCitations
          yield! outcomeEvidence.Citations
          yield! outcomeEvidence.UnsatCore |> List.collect ReasoningAssertion.citations
          yield!
              ReasoningResult.sourceAssertions result
              |> List.collect ReasoningAssertion.citations ]
        |> List.distinct

    let private ambiguous policy interpretation =
        let minimum = ReasoningBridgePolicy.minimumConfidence policy
        let margin = ReasoningBridgePolicy.ambiguityMargin policy
        let lowConfidence = ReasoningInterpretation.confidence interpretation < minimum

        let entityAmbiguity =
            ReasoningInterpretation.entityResolutions interpretation
            |> List.exists (fun resolution ->
                match ReasoningEntityResolution.alternatives resolution with
                | first :: second :: _ ->
                    let difference =
                        ReasoningEntityAlternative.confidence first
                        - ReasoningEntityAlternative.confidence second

                    difference < margin
                | [ first ] -> ReasoningEntityAlternative.confidence first < minimum
                | [] -> true)

        lowConfidence || entityAmbiguity

    let private validateExplanation interpretation result (draft: ReasoningExplanationDraft) =
        if
            String.IsNullOrWhiteSpace draft.Narrative
            || draft.Narrative <> draft.Narrative.Trim()
        then
            invalidOp "Reasoning explanation generator returned an invalid narrative."

        let citations = availableCitations result

        let citationsByAddress =
            citations
            |> List.map (fun citation -> ReasoningCitation.address citation, citation)
            |> Map.ofList

        let requested = draft.CitationAddresses |> Set.ofList

        if requested.Count <> draft.CitationAddresses.Length then
            invalidOp "Reasoning explanation contains duplicate citation addresses."

        if
            requested
            |> Set.exists (fun address -> not (Map.containsKey address citationsByAddress))
        then
            invalidOp "Reasoning explanation cites evidence absent from the formal result."

        let grounded =
            requested |> Set.toList |> List.map (fun address -> citationsByAddress[address])

        if
            (match ReasoningResult.outcome result with
             | ReasoningOutcome.Proven _
             | ReasoningOutcome.Disproven _
             | ReasoningOutcome.Inconsistent _ -> true
             | _ -> false)
            && List.isEmpty grounded
        then
            invalidOp "Definitive reasoning explanations require formal or source citations."

        GroundedReasoningExplanation.create
            draft.Narrative
            (ReasoningResult.outcome result)
            (ReasoningInterpretation.formalRequest interpretation)
            grounded

    let executeAsync
        (discovery: ReasoningSchemaDiscovery)
        (translator: ReasoningTranslator)
        (validator: ReasoningCandidateValidator)
        (executor: AuthorizationScope -> ReasoningRequest -> CancellationToken -> Task<ReasoningResult>)
        (explainer: ReasoningExplanationGenerator)
        policy
        authorization
        userText
        approval
        (cancellationToken: CancellationToken)
        =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            if String.IsNullOrWhiteSpace userText || userText <> userText.Trim() then
                invalidArg (nameof userText) "Reasoning input must be canonical and non-blank."

            let! schema = discovery.DiscoverAsync authorization cancellationToken
            ReasoningSchema.requireOwner authorization schema |> ignore

            let! interpretations = translator.TranslateAsync authorization schema userText cancellationToken

            if isNull (box interpretations) || List.isEmpty interpretations then
                invalidOp "Reasoning translator returned no candidate interpretations."

            let ids = interpretations |> List.map ReasoningInterpretation.id

            if ids.Length <> (ids |> List.distinct |> List.length) then
                invalidOp "Reasoning translator returned duplicate interpretation identities."

            for interpretation in interpretations do
                if
                    ReasoningInterpretation.schemaId interpretation <> ReasoningSchema.id schema
                    || ReasoningInterpretation.schemaVersion interpretation
                       <> ReasoningSchema.version schema
                    || ReasoningInterpretation.schemaDigest interpretation
                       <> ReasoningSchema.digest schema
                then
                    invalidOp "Reasoning interpretation does not match the discovered schema."

            let interpretations =
                interpretations |> List.sortByDescending ReasoningInterpretation.confidence

            if ambiguous policy interpretations.Head then
                return ReasoningBridgeOutcome.ClarificationRequired interpretations
            else
                let! validations =
                    interpretations
                    |> List.map (fun interpretation ->
                        task {
                            let! result = validator.ValidateAsync authorization schema interpretation cancellationToken

                            match result with
                            | Valid validated ->
                                if
                                    ValidatedReasoningRequest.interpretation validated <> interpretation
                                    || (validated
                                        |> ValidatedReasoningRequest.request
                                        |> ReasoningRequest.query
                                        |> ReasoningArtifact.digest)
                                       <> (interpretation
                                           |> ReasoningInterpretation.formalRequest
                                           |> ReasoningArtifact.digest)
                                then
                                    invalidOp "Reasoning validator returned a request for a different interpretation."

                                validated
                                |> ValidatedReasoningRequest.request
                                |> ReasoningRequest.requireOwner authorization
                                |> ignore
                            | Invalid _ -> ()

                            return interpretation, result
                        })
                    |> Task.WhenAll

                let valid =
                    validations
                    |> Array.choose (fun (_, validation) ->
                        match validation with
                        | Valid request -> Some request
                        | Invalid _ -> None)
                    |> Array.toList

                match valid with
                | [] ->
                    return
                        validations
                        |> Array.map (fun (interpretation, validation) ->
                            match validation with
                            | Invalid(code, detail) -> ReasoningInterpretation.id interpretation, code, detail
                            | Valid _ -> invalidOp "Unexpected valid reasoning candidate.")
                        |> Array.toList
                        |> ReasoningBridgeOutcome.Rejected
                | _ :: _ :: _ ->
                    return
                        valid
                        |> List.map ValidatedReasoningRequest.interpretation
                        |> ReasoningBridgeOutcome.ClarificationRequired
                | [ selected ] ->
                    let interpretation = ValidatedReasoningRequest.interpretation selected

                    let reviewRequired =
                        match ValidatedReasoningRequest.impact selected with
                        | ReasoningImpact.HighImpact _ ->
                            ReasoningBridgePolicy.requireHighImpactReview policy
                            && approval
                               |> Option.exists (ReasoningReviewApproval.matches authorization interpretation)
                               |> not
                        | ReasoningImpact.Ordinary -> false

                    if reviewRequired then
                        return ReasoningBridgeOutcome.ReviewRequired selected
                    else
                        let request = ValidatedReasoningRequest.request selected
                        ReasoningRequest.requireOwner authorization request |> ignore
                        let! formalResult = executor authorization request cancellationToken

                        if ReasoningResult.requestId formalResult <> ReasoningRequest.id request then
                            invalidOp "Reasoning executor returned a result for a different request."

                        let! draft = explainer.GenerateAsync interpretation formalResult cancellationToken

                        let explanation = validateExplanation interpretation formalResult draft

                        return ReasoningBridgeOutcome.Completed(formalResult, explanation)
        }
