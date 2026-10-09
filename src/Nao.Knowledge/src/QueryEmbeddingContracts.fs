namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

type KnowledgeQueryEmbeddingInput =
    private
        { ContentHash: ContentHash
          Text: string }

[<RequireQualifiedAccess>]
module KnowledgeQueryEmbeddingInput =
    let internal ofVariant variant =
        { ContentHash = KnowledgeQueryVariant.contentHash variant
          Text = KnowledgeQueryVariant.text variant }

    let contentHash value = value.ContentHash
    let text value = value.Text

type KnowledgeQueryEmbeddingLimits =
    private
        { MaximumProviderCalls: int
          MaximumTokens: int
          MaximumCostUsd: decimal }

[<RequireQualifiedAccess>]
module KnowledgeQueryEmbeddingLimits =
    let internal create maximumProviderCalls maximumTokens maximumCostUsd =
        if maximumProviderCalls <= 0 then
            invalidArg (nameof maximumProviderCalls) "Query embedding requires a positive provider-call limit."

        if maximumTokens < 0 then
            invalidArg (nameof maximumTokens) "Query embedding token limit cannot be negative."

        if maximumCostUsd < 0m then
            invalidArg (nameof maximumCostUsd) "Query embedding cost limit cannot be negative."

        { MaximumProviderCalls = maximumProviderCalls
          MaximumTokens = maximumTokens
          MaximumCostUsd = maximumCostUsd }

    let maximumProviderCalls value = value.MaximumProviderCalls
    let maximumTokens value = value.MaximumTokens
    let maximumCostUsd value = value.MaximumCostUsd

type KnowledgeQueryEmbeddingRequest =
    private
        { Inputs: KnowledgeQueryEmbeddingInput list
          Limits: KnowledgeQueryEmbeddingLimits }

[<RequireQualifiedAccess>]
module KnowledgeQueryEmbeddingRequest =
    let internal create inputs limits =
        if isNull (box inputs) || List.isEmpty inputs then
            invalidArg (nameof inputs) "Query embedding requires at least one input."

        if
            (inputs |> List.map KnowledgeQueryEmbeddingInput.contentHash |> Set.ofList).Count
            <> inputs.Length
        then
            invalidArg (nameof inputs) "Query embedding inputs must have distinct content hashes."

        { Inputs = inputs; Limits = limits }

    let inputs value = value.Inputs
    let limits value = value.Limits

type KnowledgeQueryEmbedding =
    private
        { ContentHash: ContentHash
          Values: float array }

[<RequireQualifiedAccess>]
module KnowledgeQueryEmbedding =
    let create contentHash (values: float array) =
        if isNull values || Array.isEmpty values then
            invalidArg (nameof values) "Query embedding values must be non-empty."

        if values |> Array.exists (Double.IsFinite >> not) then
            invalidArg (nameof values) "Query embedding values must be finite."

        { ContentHash = contentHash
          Values = Array.copy values }

    let contentHash value = value.ContentHash
    let values value = Array.copy value.Values

type KnowledgeQueryEmbeddingUsage =
    private
        { ProviderCalls: int
          InputTokens: int
          EstimatedCostUsd: decimal }

[<RequireQualifiedAccess>]
module KnowledgeQueryEmbeddingUsage =
    let create providerCalls inputTokens estimatedCostUsd =
        if providerCalls <= 0 then
            invalidArg (nameof providerCalls) "Query embedding usage requires at least one provider call."

        if inputTokens < 0 then
            invalidArg (nameof inputTokens) "Query embedding input tokens cannot be negative."

        if estimatedCostUsd < 0m then
            invalidArg (nameof estimatedCostUsd) "Query embedding cost cannot be negative."

        { ProviderCalls = providerCalls
          InputTokens = inputTokens
          EstimatedCostUsd = estimatedCostUsd }

    let providerCalls value = value.ProviderCalls
    let inputTokens value = value.InputTokens
    let estimatedCostUsd value = value.EstimatedCostUsd

type KnowledgeQueryEmbeddingResult =
    private
        { Embeddings: KnowledgeQueryEmbedding list
          Usage: KnowledgeQueryEmbeddingUsage }

[<RequireQualifiedAccess>]
module KnowledgeQueryEmbeddingResult =
    let create embeddings usage =
        if isNull (box embeddings) then
            nullArg (nameof embeddings)

        { Embeddings = embeddings
          Usage = usage }

    let embeddings value = value.Embeddings
    let usage value = value.Usage

type KnowledgeQueryEmbedder =
    { Profile: EmbeddingCapabilityDescriptor
      EmbedQueriesAsync:
          AuthorizationScope
              -> KnowledgeQueryEmbeddingRequest
              -> CancellationToken
              -> Task<KnowledgeQueryEmbeddingResult> }
