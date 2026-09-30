namespace Nao.Knowledge

open System
open Nao.Agents

[<RequireQualifiedAccess>]
type KnowledgeRetrievalStrategy =
    | Lexical
    | Vector
    | Hybrid
    | HybridWithGraph

[<RequireQualifiedAccess>]
type KnowledgeQueryTransformationStrategy =
    | None
    | Rewrite
    | Decompose
    | HypotheticalDocument
    | MultiQuery

[<RequireQualifiedAccess>]
type KnowledgeTaskIntent =
    | Unspecified
    | KnowledgeSeeking
    | NonKnowledge

type KnowledgeRetrievalLimits =
    private
        { MaximumQueryCharacters: int
          MaximumGeneratedTextCharacters: int
          MaximumQueryVariants: int
          MaximumCandidatesPerQuery: int
          MaximumCandidates: int }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalLimits =
    let create
        maximumQueryCharacters
        maximumGeneratedTextCharacters
        maximumQueryVariants
        maximumCandidatesPerQuery
        maximumCandidates
        =
        if maximumQueryCharacters <= 0 then
            invalidArg (nameof maximumQueryCharacters) "Maximum retrieval-query characters must be positive."

        if maximumGeneratedTextCharacters <= 0 then
            invalidArg
                (nameof maximumGeneratedTextCharacters)
                "Maximum generated retrieval text characters must be positive."

        if maximumQueryVariants <= 0 then
            invalidArg (nameof maximumQueryVariants) "Maximum retrieval-query variants must be positive."

        if maximumCandidatesPerQuery <= 0 then
            invalidArg (nameof maximumCandidatesPerQuery) "Maximum candidates per retrieval query must be positive."

        if maximumCandidates <= 0 then
            invalidArg (nameof maximumCandidates) "Maximum retrieval candidates must be positive."

        if maximumCandidatesPerQuery > maximumCandidates then
            invalidArg
                (nameof maximumCandidatesPerQuery)
                "Per-query retrieval candidates cannot exceed the total candidate limit."

        { MaximumQueryCharacters = maximumQueryCharacters
          MaximumGeneratedTextCharacters = maximumGeneratedTextCharacters
          MaximumQueryVariants = maximumQueryVariants
          MaximumCandidatesPerQuery = maximumCandidatesPerQuery
          MaximumCandidates = maximumCandidates }

    let defaultLimits = create 4096 8192 8 50 200
    let maximumQueryCharacters value = value.MaximumQueryCharacters
    let maximumGeneratedTextCharacters value = value.MaximumGeneratedTextCharacters
    let maximumQueryVariants value = value.MaximumQueryVariants
    let maximumCandidatesPerQuery value = value.MaximumCandidatesPerQuery
    let maximumCandidates value = value.MaximumCandidates

type KnowledgeRetrievalBudget =
    private
        { MaximumDuration: TimeSpan
          MaximumModelCalls: int
          MaximumEmbeddingCalls: int
          MaximumTokens: int
          MaximumCostUsd: decimal }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalBudget =
    let create maximumDuration maximumModelCalls maximumEmbeddingCalls maximumTokens maximumCostUsd =
        if maximumDuration <= TimeSpan.Zero then
            invalidArg (nameof maximumDuration) "Retrieval duration budget must be positive."

        if maximumModelCalls < 0 then
            invalidArg (nameof maximumModelCalls) "Retrieval model-call budget cannot be negative."

        if maximumEmbeddingCalls < 0 then
            invalidArg (nameof maximumEmbeddingCalls) "Retrieval embedding-call budget cannot be negative."

        if maximumTokens < 0 then
            invalidArg (nameof maximumTokens) "Retrieval token budget cannot be negative."

        if maximumCostUsd < 0m then
            invalidArg (nameof maximumCostUsd) "Retrieval cost budget cannot be negative."

        { MaximumDuration = maximumDuration
          MaximumModelCalls = maximumModelCalls
          MaximumEmbeddingCalls = maximumEmbeddingCalls
          MaximumTokens = maximumTokens
          MaximumCostUsd = maximumCostUsd }

    let deterministic = create (TimeSpan.FromSeconds 5.0) 0 0 0 0m

    let maximumDuration value = value.MaximumDuration
    let maximumModelCalls value = value.MaximumModelCalls
    let maximumEmbeddingCalls value = value.MaximumEmbeddingCalls
    let maximumTokens value = value.MaximumTokens
    let maximumCostUsd value = value.MaximumCostUsd

type KnowledgeRetrievalRequest =
    private
        { Query: string
          Authorization: AuthorizationScope
          Namespace: KnowledgeIndexNamespace
          Filter: KnowledgeIndexFilter
          Intent: KnowledgeTaskIntent
          Strategy: KnowledgeRetrievalStrategy
          Transformation: KnowledgeQueryTransformationStrategy
          Limits: KnowledgeRetrievalLimits
          Budget: KnowledgeRetrievalBudget }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalRequest =
    let create query authorization namespaceName filter intent strategy transformation limits budget =
        if String.IsNullOrWhiteSpace query then
            invalidArg (nameof query) "Retrieval query must be non-blank."

        if query.Length > KnowledgeRetrievalLimits.maximumQueryCharacters limits then
            invalidArg (nameof query) "Retrieval query exceeds its declared character limit."

        { Query = query
          Authorization = authorization
          Namespace = namespaceName
          Filter = filter
          Intent = intent
          Strategy = strategy
          Transformation = transformation
          Limits = limits
          Budget = budget }

    let query value = value.Query
    let authorization value = value.Authorization

    let identity value =
        value.Authorization |> AuthorizationScope.identity

    let namespaceName value = value.Namespace
    let filter value = value.Filter
    let intent value = value.Intent
    let strategy value = value.Strategy
    let transformation value = value.Transformation
    let limits value = value.Limits
    let budget value = value.Budget
