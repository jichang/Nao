namespace Nao.Knowledge

open System
open System.Globalization
open System.Text
open Nao.Agents

type KnowledgeNormalizedQuery =
    private
        { Request: KnowledgeRetrievalRequest
          Text: string
          Normalizer: ComponentVersion
          OriginalHash: ContentHash
          NormalizedHash: ContentHash }

[<RequireQualifiedAccess>]
module KnowledgeNormalizedQuery =
    let request value = value.Request

    let originalQuery value =
        value.Request |> KnowledgeRetrievalRequest.query

    let text value = value.Text
    let normalizer value = value.Normalizer
    let originalHash value = value.OriginalHash
    let normalizedHash value = value.NormalizedHash

[<RequireQualifiedAccess>]
module KnowledgeQueryNormalizer =
    let descriptor = ComponentVersion.create "nao-query-normalizer" "1"

    let private canonicalizeWhitespace (value: string) =
        let normalized = value.Normalize(NormalizationForm.FormKC)
        let output = StringBuilder(normalized.Length)
        let mutable pendingSpace = false

        for character in normalized do
            if Char.IsWhiteSpace character then
                if output.Length > 0 then
                    pendingSpace <- true
            else
                if CharUnicodeInfo.GetUnicodeCategory character = UnicodeCategory.Control then
                    invalidArg (nameof value) "Retrieval query contains a non-whitespace control character."

                if pendingSpace then
                    output.Append(' ') |> ignore
                    pendingSpace <- false

                output.Append(character) |> ignore

        output.ToString()

    let private normalizeWithin maximumCharacters (value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg (nameof value) "Retrieval query must be non-blank."

        if value.Length > maximumCharacters then
            invalidArg (nameof value) "Retrieval text exceeds its declared character limit."

        let normalized = canonicalizeWhitespace value

        if String.IsNullOrWhiteSpace normalized then
            invalidArg (nameof value) "Normalized retrieval query must be non-blank."

        if normalized.Length > maximumCharacters then
            invalidArg (nameof value) "Normalized retrieval text exceeds its declared character limit."

        normalized

    let normalizeText limits value =
        normalizeWithin (KnowledgeRetrievalLimits.maximumQueryCharacters limits) value

    let normalizeGeneratedText strategy limits value =
        let maximumCharacters =
            match strategy with
            | KnowledgeQueryTransformationStrategy.HypotheticalDocument ->
                KnowledgeRetrievalLimits.maximumGeneratedTextCharacters limits
            | _ -> KnowledgeRetrievalLimits.maximumQueryCharacters limits

        normalizeWithin maximumCharacters value

    let normalize (request: KnowledgeRetrievalRequest) =
        let original = KnowledgeRetrievalRequest.query request
        let limits = KnowledgeRetrievalRequest.limits request
        let normalized = normalizeText limits original

        { Request = request
          Text = normalized
          Normalizer = descriptor
          OriginalHash = ContentHash.computeText original
          NormalizedHash = ContentHash.computeText normalized }
