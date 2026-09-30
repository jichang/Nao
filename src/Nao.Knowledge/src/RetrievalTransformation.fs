namespace Nao.Knowledge

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Nao.Agents

type KnowledgeQueryTransformationUsage =
    { ModelCalls: int
      InputTokens: int
      OutputTokens: int
      EstimatedCostUsd: decimal }

[<RequireQualifiedAccess>]
module KnowledgeQueryTransformationUsage =
    let zero =
        { ModelCalls = 0
          InputTokens = 0
          OutputTokens = 0
          EstimatedCostUsd = 0m }

type KnowledgeQueryTransformationModelDescriptor =
    { Model: ComponentVersion
      ConfigurationHash: ContentHash
      Deterministic: bool }

type KnowledgeQueryTransformationInput =
    { Strategy: KnowledgeQueryTransformationStrategy
      Query: string
      MaximumGeneratedVariants: int
      MaximumGeneratedCharacters: int
      Budget: KnowledgeRetrievalBudget }

type KnowledgeQueryTransformationOutput =
    { Variants: string list
      Usage: KnowledgeQueryTransformationUsage }

type KnowledgeQueryTransformationModel =
    { Descriptor: KnowledgeQueryTransformationModelDescriptor
      TransformAsync:
          AuthorizationScope
              -> KnowledgeQueryTransformationInput
              -> CancellationToken
              -> Task<KnowledgeQueryTransformationOutput> }

[<RequireQualifiedAccess>]
type KnowledgeQueryVariantKind =
    | Original
    | Rewrite
    | Decomposition
    | HypotheticalDocument
    | MultiQuery

type KnowledgeQueryVariant =
    private
        { Kind: KnowledgeQueryVariantKind
          Ordinal: int
          Text: string
          ContentHash: ContentHash
          GeneratedBy: KnowledgeQueryTransformationModelDescriptor option }

[<RequireQualifiedAccess>]
module KnowledgeQueryVariant =
    let kind value = value.Kind
    let ordinal value = value.Ordinal
    let text value = value.Text
    let contentHash value = value.ContentHash
    let generatedBy value = value.GeneratedBy

type KnowledgeQueryTransformationPlan =
    private
        { NormalizedQuery: KnowledgeNormalizedQuery
          Strategy: KnowledgeQueryTransformationStrategy
          Variants: KnowledgeQueryVariant list
          Usage: KnowledgeQueryTransformationUsage }

[<RequireQualifiedAccess>]
module KnowledgeQueryTransformationPlan =
    let normalizedQuery value = value.NormalizedQuery

    let request value =
        value.NormalizedQuery |> KnowledgeNormalizedQuery.request

    let strategy value = value.Strategy
    let variants value = value.Variants
    let usage value = value.Usage

[<RequireQualifiedAccess>]
module KnowledgeQueryTransformation =
    let private generatedKind =
        function
        | KnowledgeQueryTransformationStrategy.None -> invalidOp "No generated variant exists for this strategy."
        | KnowledgeQueryTransformationStrategy.Rewrite -> KnowledgeQueryVariantKind.Rewrite
        | KnowledgeQueryTransformationStrategy.Decompose -> KnowledgeQueryVariantKind.Decomposition
        | KnowledgeQueryTransformationStrategy.HypotheticalDocument -> KnowledgeQueryVariantKind.HypotheticalDocument
        | KnowledgeQueryTransformationStrategy.MultiQuery -> KnowledgeQueryVariantKind.MultiQuery

    let private validateUsage budget usage =
        if isNull (box usage) then
            invalidOp "Query transformation model returned null usage evidence."

        if
            usage.ModelCalls <= 0
            || usage.InputTokens < 0
            || usage.OutputTokens < 0
            || usage.EstimatedCostUsd < 0m
        then
            invalidOp "Query transformation model returned invalid usage evidence."

        if usage.ModelCalls > KnowledgeRetrievalBudget.maximumModelCalls budget then
            invalidOp "Query transformation exceeded its model-call budget."

        let totalTokens = int64 usage.InputTokens + int64 usage.OutputTokens

        if totalTokens > int64 (KnowledgeRetrievalBudget.maximumTokens budget) then
            invalidOp "Query transformation exceeded its token budget."

        if usage.EstimatedCostUsd > KnowledgeRetrievalBudget.maximumCostUsd budget then
            invalidOp "Query transformation exceeded its cost budget."

    let private validateOutputCount
        (strategy: KnowledgeQueryTransformationStrategy)
        maximumGenerated
        (variants: string list)
        =
        if isNull (box variants) then
            invalidOp "Query transformation model returned a null variant collection."

        if variants.Length > maximumGenerated then
            invalidOp "Query transformation model exceeded the query-variant limit."

        match strategy with
        | KnowledgeQueryTransformationStrategy.Rewrite
        | KnowledgeQueryTransformationStrategy.HypotheticalDocument when variants.Length <> 1 ->
            invalidOp "Query transformation strategy requires exactly one generated variant."
        | KnowledgeQueryTransformationStrategy.Decompose
        | KnowledgeQueryTransformationStrategy.MultiQuery when List.isEmpty variants ->
            invalidOp "Query transformation strategy requires at least one generated variant."
        | _ -> ()

    let private originalVariant normalizedQuery =
        { Kind = KnowledgeQueryVariantKind.Original
          Ordinal = 0
          Text = KnowledgeNormalizedQuery.text normalizedQuery
          ContentHash = KnowledgeNormalizedQuery.normalizedHash normalizedQuery
          GeneratedBy = None }

    let transformAsync
        (model: KnowledgeQueryTransformationModel option)
        (normalizedQuery: KnowledgeNormalizedQuery)
        (cancellationToken: CancellationToken)
        : Task<KnowledgeQueryTransformationPlan> =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            let request = KnowledgeNormalizedQuery.request normalizedQuery
            let strategy = KnowledgeRetrievalRequest.transformation request
            let original = originalVariant normalizedQuery

            match strategy with
            | KnowledgeQueryTransformationStrategy.None ->
                return
                    { NormalizedQuery = normalizedQuery
                      Strategy = strategy
                      Variants = [ original ]
                      Usage = KnowledgeQueryTransformationUsage.zero }
            | _ ->
                let model =
                    model
                    |> Option.defaultWith (fun () ->
                        invalidOp "Query transformation strategy requires a transformation model.")

                if isNull (box model) then
                    nullArg (nameof model)

                let budget = KnowledgeRetrievalRequest.budget request

                if KnowledgeRetrievalBudget.maximumModelCalls budget <= 0 then
                    invalidOp "Query transformation requires a positive model-call budget."

                let limits = KnowledgeRetrievalRequest.limits request
                let maximumGenerated = KnowledgeRetrievalLimits.maximumQueryVariants limits - 1

                if maximumGenerated <= 0 then
                    invalidOp "Query transformation requires capacity for a generated query variant."

                let maximumGeneratedCharacters =
                    match strategy with
                    | KnowledgeQueryTransformationStrategy.HypotheticalDocument ->
                        KnowledgeRetrievalLimits.maximumGeneratedTextCharacters limits
                    | _ -> KnowledgeRetrievalLimits.maximumQueryCharacters limits

                let input =
                    { Strategy = strategy
                      Query = KnowledgeNormalizedQuery.text normalizedQuery
                      MaximumGeneratedVariants = maximumGenerated
                      MaximumGeneratedCharacters = maximumGeneratedCharacters
                      Budget = budget }

                use timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)

                let invocation =
                    model.TransformAsync (KnowledgeRetrievalRequest.authorization request) input timeout.Token

                let! output =
                    task {
                        try
                            return!
                                invocation.WaitAsync(KnowledgeRetrievalBudget.maximumDuration budget, cancellationToken)
                        with :? TimeoutException as error ->
                            timeout.Cancel()
                            return raise (TimeoutException("Query transformation exceeded its duration budget.", error))
                    }

                if isNull (box output) then
                    invalidOp "Query transformation model returned a null output."

                validateOutputCount strategy maximumGenerated output.Variants
                validateUsage budget output.Usage

                let seen = HashSet<string>(StringComparer.Ordinal)
                seen.Add(KnowledgeNormalizedQuery.text normalizedQuery) |> ignore

                let generated =
                    output.Variants
                    |> List.choose (fun value ->
                        let normalized =
                            KnowledgeQueryNormalizer.normalizeGeneratedText strategy limits value

                        if seen.Add normalized then Some normalized else None)

                validateOutputCount strategy maximumGenerated generated
                let kind = generatedKind strategy

                let variants =
                    generated
                    |> List.mapi (fun index text ->
                        { Kind = kind
                          Ordinal = index + 1
                          Text = text
                          ContentHash = ContentHash.computeText text
                          GeneratedBy = Some model.Descriptor })

                return
                    { NormalizedQuery = normalizedQuery
                      Strategy = strategy
                      Variants = original :: variants
                      Usage = output.Usage }
        }
