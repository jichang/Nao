namespace Nao.Knowledge

open System
open System.Text.Json
open Nao.Protocols

[<CLIMutable>]
type KnowledgeGroundedProtocolEnvelopeDto = { Status: string }

[<CLIMutable>]
type KnowledgeGroundedProtocolClaimDto =
    { Id: string
      Offset: int
      Length: int
      Kind: string
      Confidence: double
      CitationIds: string array }

[<CLIMutable>]
type KnowledgeGroundedProtocolCitationDto =
    { Id: string
      Marker: string
      ChunkId: string }

[<CLIMutable>]
type KnowledgeGroundedProtocolAnswerDto =
    { Status: string
      Answer: string
      Confidence: double
      Claims: KnowledgeGroundedProtocolClaimDto array
      Citations: KnowledgeGroundedProtocolCitationDto array }

[<CLIMutable>]
type KnowledgeGroundedProtocolInsufficiencyDto =
    { Status: string
      Reason: string
      Explanation: string }

type KnowledgeGroundedResponseProtocolConfig =
    private
        { MaximumResponseCharacters: int
          MaximumClaims: int
          MaximumCitations: int
          MaximumCitationsPerClaim: int }

[<RequireQualifiedAccess>]
module KnowledgeGroundedResponseProtocolConfig =
    let create maximumResponseCharacters maximumClaims maximumCitations maximumCitationsPerClaim =
        if maximumResponseCharacters <= 0 then
            invalidArg (nameof maximumResponseCharacters) "Response character limit must be positive."

        if maximumClaims <= 0 then
            invalidArg (nameof maximumClaims) "Claim limit must be positive."

        if maximumCitations <= 0 then
            invalidArg (nameof maximumCitations) "Citation limit must be positive."

        if maximumCitationsPerClaim <= 0 || maximumCitationsPerClaim > maximumCitations then
            invalidArg (nameof maximumCitationsPerClaim) "Per-claim citation limit must fit the citation limit."

        { MaximumResponseCharacters = maximumResponseCharacters
          MaximumClaims = maximumClaims
          MaximumCitations = maximumCitations
          MaximumCitationsPerClaim = maximumCitationsPerClaim }

    let maximumResponseCharacters value = value.MaximumResponseCharacters
    let maximumClaims value = value.MaximumClaims
    let maximumCitations value = value.MaximumCitations
    let maximumCitationsPerClaim value = value.MaximumCitationsPerClaim

[<RequireQualifiedAccess>]
module KnowledgeGroundedResponseProtocol =
    let private jsonOptions =
        JsonSerializerOptions(PropertyNameCaseInsensitive = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let private error summary expected suggestedFix =
        { ResponseParseError.create summary with
            Expected = expected
            SuggestedFix = suggestedFix }

    let private parseInsufficiencyReason =
        function
        | "context-insufficient" -> Some KnowledgeGroundedInsufficiencyReason.ContextInsufficient
        | "no-material-claims" -> Some KnowledgeGroundedInsufficiencyReason.NoMaterialClaims
        | "model-abstained" -> Some KnowledgeGroundedInsufficiencyReason.ModelAbstained
        | "policy-rejected" -> Some KnowledgeGroundedInsufficiencyReason.PolicyRejected
        | _ -> None

    let private parseClaimPresentation =
        function
        | "source-quotation" -> Some KnowledgeGroundedClaimPresentation.SourceQuotation
        | "generated-interpretation" -> Some KnowledgeGroundedClaimPresentation.GeneratedInterpretation
        | _ -> None

    let private parseAnswer config (response: string) =
        try
            let value =
                JsonSerializer.Deserialize<KnowledgeGroundedProtocolAnswerDto>(response, jsonOptions)

            if isNull (box value) || value.Status <> "answer" then
                Error(error "Answer response has an invalid status." (Some "'answer'") None)
            elif isNull value.Claims || value.Claims.Length = 0 then
                Error(error "Answer response requires at least one material claim." None None)
            elif value.Claims.Length > KnowledgeGroundedResponseProtocolConfig.maximumClaims config then
                Error(error "Answer response exceeds the claim limit." None None)
            elif isNull value.Citations then
                Error(error "Answer response requires a citations array." None None)
            elif value.Citations.Length > KnowledgeGroundedResponseProtocolConfig.maximumCitations config then
                Error(error "Answer response exceeds the citation limit." None None)
            else
                try
                    let citations =
                        value.Citations
                        |> Array.map (fun citation ->
                            KnowledgeGroundedCitationReference.create
                                (KnowledgeGroundedCitationReferenceId.parse citation.Id)
                                citation.Marker
                                (KnowledgeChunkId.parse citation.ChunkId))
                        |> List.ofArray

                    let citationIds =
                        citations |> List.map KnowledgeGroundedCitationReference.id |> Set.ofList

                    let claims =
                        value.Claims
                        |> Array.map (fun claim ->
                            if isNull claim.CitationIds || claim.CitationIds.Length = 0 then
                                invalidArg
                                    (nameof claim.CitationIds)
                                    "Every material claim must assert at least one citation."

                            if
                                claim.CitationIds.Length > KnowledgeGroundedResponseProtocolConfig.maximumCitationsPerClaim
                                    config
                            then
                                invalidArg
                                    (nameof claim.CitationIds)
                                    "A material claim exceeds the per-claim citation limit."

                            let assertedIds =
                                claim.CitationIds
                                |> Array.map KnowledgeGroundedCitationReferenceId.parse
                                |> List.ofArray

                            assertedIds
                            |> List.iter (fun id ->
                                if not (citationIds.Contains id) then
                                    invalidArg
                                        (nameof claim.CitationIds)
                                        "A material claim references an undefined citation ID.")

                            let presentation =
                                parseClaimPresentation claim.Kind
                                |> Option.defaultWith (fun () ->
                                    invalidArg
                                        (nameof claim.Kind)
                                        "A material claim kind must be 'source-quotation' or 'generated-interpretation'.")

                            KnowledgeGroundedClaim.create
                                value.Answer
                                (KnowledgeGroundedClaimId.parse claim.Id)
                                claim.Offset
                                claim.Length
                                presentation
                                assertedIds
                                (Confidence.create claim.Confidence))
                        |> List.ofArray

                    KnowledgeGroundedAnswerProposal.create
                        value.Answer
                        claims
                        citations
                        (Confidence.create value.Confidence)
                    |> KnowledgeGroundedGenerationResult.ProposedAnswer
                    |> List.singleton
                    |> Ok
                with :? ArgumentException as parseError ->
                    Error(error "Answer response violates the grounded-answer contract." None (Some parseError.Message))
        with :? JsonException as jsonError ->
            Error(error "Answer response cannot be deserialized." None (Some jsonError.Message))

    let private parseInsufficiency (response: string) =
        try
            let value =
                JsonSerializer.Deserialize<KnowledgeGroundedProtocolInsufficiencyDto>(response, jsonOptions)

            if isNull (box value) || value.Status <> "insufficient" then
                Error(error "Insufficiency response has an invalid status." (Some "'insufficient'") None)
            else
                match parseInsufficiencyReason value.Reason with
                | None ->
                    Error(
                        error
                            "Insufficiency response has an unsupported reason."
                            (Some
                                "'context-insufficient', 'no-material-claims', 'model-abstained', or 'policy-rejected'")
                            None
                    )
                | Some reason ->
                    try
                        KnowledgeGroundedInsufficiency.create reason value.Explanation
                        |> KnowledgeGroundedGenerationResult.InsufficientEvidence
                        |> List.singleton
                        |> Ok
                    with :? ArgumentException as parseError ->
                        Error(
                            error
                                "Insufficiency response violates the grounded-answer contract."
                                None
                                (Some parseError.Message)
                        )
        with :? JsonException as jsonError ->
            Error(error "Insufficiency response cannot be deserialized." None (Some jsonError.Message))

    let private parse config (response: string) =
        if String.IsNullOrWhiteSpace response then
            Error(error "Grounded generation returned an empty response." None None)
        elif response.Length > KnowledgeGroundedResponseProtocolConfig.maximumResponseCharacters config then
            Error(error "Grounded generation response exceeds its character limit." None None)
        else
            try
                let envelope =
                    JsonSerializer.Deserialize<KnowledgeGroundedProtocolEnvelopeDto>(response, jsonOptions)

                if isNull (box envelope) then
                    Error(error "Grounded generation response cannot be deserialized." None None)
                else
                    match envelope.Status with
                    | "answer" -> parseAnswer config response
                    | "insufficient" -> parseInsufficiency response
                    | _ ->
                        Error(
                            error
                                "Grounded generation response has an unsupported status."
                                (Some "'answer' or 'insufficient'")
                                None
                        )
            with :? JsonException as jsonError ->
                Error(
                    { error "Grounded generation response is not valid JSON." None (Some jsonError.Message) with
                        Location =
                            if jsonError.LineNumber.HasValue && jsonError.BytePositionInLine.HasValue then
                                Some
                                    $"line {jsonError.LineNumber.Value + 1L}, byte {jsonError.BytePositionInLine.Value + 1L}"
                            else
                                None }
                )

    let private descriptor =
        let exampleDocumentId = KnowledgeDocumentId.parse "grounded-protocol-example"

        let exampleVersionId =
            KnowledgeDocumentVersionId.derive exampleDocumentId 1 (ContentHash.computeText "example-version")

        let exampleChunkId =
            KnowledgeChunkId.derive exampleVersionId "example-chunker" 0 (ContentHash.computeText "Example fact.")
            |> KnowledgeChunkId.value

        { Name = "nao-grounded-answer-json-v1"
          Description =
            "Returns either a structured answer whose every material claim cites retrieved context, or typed insufficiency."
          Instructions =
            [ "Return one JSON object and no prose, markdown, or code fence."
              "Use status 'answer' with: status, answer, confidence, claims, citations."
              "Each claim must contain: id, offset, length, kind, confidence, citationIds."
              "Claim kind must be 'source-quotation' for exact source text or 'generated-interpretation' for generated wording."
              "Every material claim must have one or more citationIds."
              "Each citation must contain: id, marker, chunkId."
              "Copy citation markers and chunk IDs exactly from the supplied context; never invent them."
              "Claim offsets and lengths are zero-based character ranges into the exact answer string."
              "Use status 'insufficient' with: status, reason, explanation when evidence is insufficient."
              "Insufficiency reason must be context-insufficient, no-material-claims, model-abstained, or policy-rejected." ]
          Examples =
            [ $$"""{"status":"answer","answer":"Example fact.","confidence":0.9,"claims":[{"id":"claim-1","offset":0,"length":13,"kind":"generated-interpretation","confidence":0.9,"citationIds":["citation-1"]}],"citations":[{"id":"citation-1","marker":"[nao-citation:{{exampleChunkId}}]","chunkId":"{{exampleChunkId}}"}]}"""
              """{"status":"insufficient","reason":"context-insufficient","explanation":"The supplied context does not support an answer."}""" ]
          MediaType = Some "application/json"
          Metadata = Map.ofList [ "nao.protocol.kind", "grounded-answer"; "nao.protocol.version", "1" ] }

    let create config =
        if isNull (box config) then
            nullArg (nameof config)

        ResponseProtocol.create descriptor (parse config) (fun parseError ->
            "Return a corrected response that follows nao-grounded-answer-json-v1. "
            + ResponseParseError.format parseError)
