namespace Nao.Knowledge

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

type KnowledgeParentExpansionStage =
    private
        { Resolver: KnowledgeParentResolver
          Config: KnowledgeParentExpansionConfig }

[<RequireQualifiedAccess>]
module KnowledgeParentExpansionStage =
    let create (resolver: KnowledgeParentResolver) config =
        if isNull (box resolver) || isNull (box resolver.ResolveAsync) then
            nullArg (nameof resolver)

        if isNull (box config) then
            nullArg (nameof config)

        { Resolver = resolver; Config = config }

type KnowledgeGraphExpansionStage =
    private
        { Index: KnowledgeGraphIndex
          Resolver: KnowledgeGraphDocumentResolver
          Config: KnowledgeGraphExpansionConfig }

[<RequireQualifiedAccess>]
module KnowledgeGraphExpansionStage =
    let create (index: KnowledgeGraphIndex) (resolver: KnowledgeGraphDocumentResolver) config =
        if isNull (box index) || isNull (box index.QueryAsync) then
            nullArg (nameof index)

        if isNull (box resolver) || isNull (box resolver.ResolveAsync) then
            nullArg (nameof resolver)

        if isNull (box config) then
            nullArg (nameof config)

        { Index = index
          Resolver = resolver
          Config = config }

type KnowledgeHybridRetrievalConfig =
    private
        { Fusion: KnowledgeReciprocalRankFusionConfig
          Diversity: KnowledgeRetrievalDiversityConfig
          ParentExpansion: KnowledgeParentExpansionStage option
          GraphExpansion: KnowledgeGraphExpansionStage option }

[<RequireQualifiedAccess>]
module KnowledgeHybridRetrievalConfig =
    let create fusion diversity parentExpansion graphExpansion =
        if isNull (box fusion) then
            nullArg (nameof fusion)

        if isNull (box diversity) then
            nullArg (nameof diversity)

        { Fusion = fusion
          Diversity = diversity
          ParentExpansion = parentExpansion
          GraphExpansion = graphExpansion }

type KnowledgeHybridRetrievalUsage =
    private
        { ModelCalls: int
          EmbeddingCalls: int
          InputTokens: int
          OutputTokens: int
          EstimatedCostUsd: decimal
          LexicalCandidates: int
          VectorCandidates: int
          FinalCandidates: int
          Duration: TimeSpan }

[<RequireQualifiedAccess>]
module KnowledgeHybridRetrievalUsage =
    let modelCalls value = value.ModelCalls
    let embeddingCalls value = value.EmbeddingCalls
    let inputTokens value = value.InputTokens
    let outputTokens value = value.OutputTokens

    let totalTokens value =
        int64 value.InputTokens + int64 value.OutputTokens

    let estimatedCostUsd value = value.EstimatedCostUsd
    let lexicalCandidates value = value.LexicalCandidates
    let vectorCandidates value = value.VectorCandidates
    let finalCandidates value = value.FinalCandidates
    let duration value = value.Duration

type KnowledgeHybridRetrieval =
    private
        { Executor: ComponentVersion
          Understanding: KnowledgeQueryUnderstandingResult
          Lexical: KnowledgeLexicalRetrieval
          Vector: KnowledgeVectorRetrieval
          Fusion: KnowledgeReciprocalRankFusion
          Selection: KnowledgeRetrievalDiversitySelection
          ParentExpansion: KnowledgeParentChildExpansion option
          GraphExpansion: KnowledgeGraphNeighborhoodExpansionResult option
          Candidates: KnowledgeRetrievalCandidates
          Usage: KnowledgeHybridRetrievalUsage }

[<RequireQualifiedAccess>]
module KnowledgeHybridRetrieval =
    let executor value = value.Executor
    let understanding value = value.Understanding
    let lexical value = value.Lexical
    let vector value = value.Vector
    let fusion value = value.Fusion
    let selection value = value.Selection
    let parentExpansion value = value.ParentExpansion
    let graphExpansion value = value.GraphExpansion
    let candidates value = value.Candidates
    let usage value = value.Usage

    let request value =
        value.Candidates |> KnowledgeRetrievalCandidates.request

[<RequireQualifiedAccess>]
type KnowledgeHybridRetrievalResult =
    | Skipped of KnowledgeRetrievalAdmission
    | Retrieved of KnowledgeHybridRetrieval

[<RequireQualifiedAccess>]
module KnowledgeHybridRetrievalPipeline =
    let descriptor = ComponentVersion.create "nao-hybrid-retrieval-pipeline" "1"

    let private validateStrategy request (config: KnowledgeHybridRetrievalConfig) =
        match KnowledgeRetrievalRequest.strategy request with
        | KnowledgeRetrievalStrategy.Hybrid -> ()
        | KnowledgeRetrievalStrategy.HybridWithGraph ->
            if config.GraphExpansion.IsNone then
                invalidOp "Hybrid-with-graph retrieval requires a graph expansion stage."
        | KnowledgeRetrievalStrategy.Lexical
        | KnowledgeRetrievalStrategy.Vector ->
            invalidOp "The hybrid retrieval pipeline requires a hybrid retrieval strategy."

    let private validateUsage request usage =
        let budget = KnowledgeRetrievalRequest.budget request

        if usage.ModelCalls > KnowledgeRetrievalBudget.maximumModelCalls budget then
            invalidOp "Hybrid retrieval exceeded the aggregate model-call budget."

        if usage.EmbeddingCalls > KnowledgeRetrievalBudget.maximumEmbeddingCalls budget then
            invalidOp "Hybrid retrieval exceeded the aggregate embedding-call budget."

        if KnowledgeHybridRetrievalUsage.totalTokens usage > int64 (KnowledgeRetrievalBudget.maximumTokens budget) then
            invalidOp "Hybrid retrieval exceeded the aggregate token budget."

        if usage.EstimatedCostUsd > KnowledgeRetrievalBudget.maximumCostUsd budget then
            invalidOp "Hybrid retrieval exceeded the aggregate cost budget."

        if usage.Duration > KnowledgeRetrievalBudget.maximumDuration budget then
            invalidOp "Hybrid retrieval exceeded the aggregate duration budget."

        if
            usage.FinalCandidates > (KnowledgeRetrievalRequest.limits request
                                     |> KnowledgeRetrievalLimits.maximumCandidates)
        then
            invalidOp "Hybrid retrieval exceeded the aggregate candidate limit."

    let executeAsync
        (transformationModel: KnowledgeQueryTransformationModel option)
        (lexicalIndex: KnowledgeLexicalIndex)
        (embedder: KnowledgeQueryEmbedder)
        (vectorIndex: KnowledgeVectorIndex)
        (config: KnowledgeHybridRetrievalConfig)
        (request: KnowledgeRetrievalRequest)
        (cancellationToken: CancellationToken)
        : Task<KnowledgeHybridRetrievalResult> =
        cancellationToken.ThrowIfCancellationRequested()

        if isNull (box config) then
            nullArg (nameof config)

        if isNull (box request) then
            nullArg (nameof request)

        validateStrategy request config

        task {
            use timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            timeout.CancelAfter(KnowledgeRetrievalBudget.maximumDuration (KnowledgeRetrievalRequest.budget request))
            let timer = Stopwatch.StartNew()

            try
                let normalized = KnowledgeQueryNormalizer.normalize request

                let! understanding = KnowledgeQueryUnderstanding.planAsync transformationModel normalized timeout.Token

                match understanding with
                | KnowledgeQueryUnderstandingResult.RetrievalSkipped admission ->
                    return KnowledgeHybridRetrievalResult.Skipped admission
                | KnowledgeQueryUnderstandingResult.RetrievalPlanned(_, plan) ->
                    let lexicalTask =
                        KnowledgeLexicalRetrievalExecutor.executeAsync lexicalIndex understanding timeout.Token

                    let vectorTask =
                        KnowledgeVectorRetrievalExecutor.executeAsync embedder vectorIndex understanding timeout.Token

                    let! lexicalResult = lexicalTask
                    let! vectorResult = vectorTask

                    let lexical =
                        match lexicalResult with
                        | KnowledgeLexicalRetrievalResult.Retrieved value -> value
                        | KnowledgeLexicalRetrievalResult.Skipped _ ->
                            invalidOp "Planned hybrid retrieval cannot skip its lexical leg."

                    let vector =
                        match vectorResult with
                        | KnowledgeVectorRetrievalResult.Retrieved value -> value
                        | KnowledgeVectorRetrievalResult.Skipped _ ->
                            invalidOp "Planned hybrid retrieval cannot skip its vector leg."

                    let fusion = KnowledgeReciprocalRankFusionExecutor.fuse config.Fusion lexical vector

                    let selection = KnowledgeRetrievalDiversitySelector.select config.Diversity fusion

                    let! parentExpansion =
                        match config.ParentExpansion with
                        | None -> Task.FromResult None
                        | Some stage ->
                            task {
                                let! expansion =
                                    KnowledgeParentChildExpansionExecutor.expandAsync
                                        stage.Resolver
                                        stage.Config
                                        selection
                                        timeout.Token

                                return Some expansion
                            }

                    let selectedCount =
                        selection |> KnowledgeRetrievalDiversitySelection.candidates |> List.length

                    let parentCount =
                        parentExpansion
                        |> Option.map (KnowledgeParentChildExpansion.parents >> List.length)
                        |> Option.defaultValue 0

                    let requestMaximum =
                        request
                        |> KnowledgeRetrievalRequest.limits
                        |> KnowledgeRetrievalLimits.maximumCandidates

                    let remainingGraphCapacity = max 0 (requestMaximum - selectedCount - parentCount)

                    let! graphExpansion =
                        match KnowledgeRetrievalRequest.strategy request, config.GraphExpansion with
                        | KnowledgeRetrievalStrategy.Hybrid, _ -> Task.FromResult None
                        | KnowledgeRetrievalStrategy.HybridWithGraph, Some stage ->
                            task {
                                let effectiveConfig =
                                    KnowledgeGraphExpansionConfig.create
                                        (KnowledgeGraphExpansionConfig.edgeKinds stage.Config)
                                        (KnowledgeGraphExpansionConfig.maximumDepth stage.Config)
                                        (min
                                            remainingGraphCapacity
                                            (KnowledgeGraphExpansionConfig.maximumNeighbors stage.Config))

                                let! expansion =
                                    KnowledgeGraphNeighborhoodExpansionExecutor.expandAsync
                                        stage.Index
                                        stage.Resolver
                                        effectiveConfig
                                        selection
                                        timeout.Token

                                return Some expansion
                            }
                        | KnowledgeRetrievalStrategy.HybridWithGraph, None ->
                            invalidOp "Hybrid-with-graph retrieval requires a graph expansion stage."
                        | KnowledgeRetrievalStrategy.Lexical, _
                        | KnowledgeRetrievalStrategy.Vector, _ ->
                            invalidOp "The hybrid retrieval pipeline requires a hybrid strategy."

                    let candidates =
                        KnowledgeRetrievalCandidateAssembler.assemble selection parentExpansion graphExpansion

                    timer.Stop()
                    let transformationUsage = KnowledgeQueryTransformationPlan.usage plan
                    let embeddingUsage = KnowledgeVectorRetrieval.embeddingUsage vector

                    let usage =
                        { ModelCalls = transformationUsage.ModelCalls
                          EmbeddingCalls = KnowledgeQueryEmbeddingUsage.providerCalls embeddingUsage
                          InputTokens =
                            transformationUsage.InputTokens
                            + KnowledgeQueryEmbeddingUsage.inputTokens embeddingUsage
                          OutputTokens = transformationUsage.OutputTokens
                          EstimatedCostUsd =
                            transformationUsage.EstimatedCostUsd
                            + KnowledgeQueryEmbeddingUsage.estimatedCostUsd embeddingUsage
                          LexicalCandidates = KnowledgeLexicalRetrieval.candidateCount lexical
                          VectorCandidates = KnowledgeVectorRetrieval.candidateCount vector
                          FinalCandidates = candidates |> KnowledgeRetrievalCandidates.candidates |> List.length
                          Duration = timer.Elapsed }

                    validateUsage request usage

                    return
                        KnowledgeHybridRetrievalResult.Retrieved
                            { Executor = descriptor
                              Understanding = understanding
                              Lexical = lexical
                              Vector = vector
                              Fusion = fusion
                              Selection = selection
                              ParentExpansion = parentExpansion
                              GraphExpansion = graphExpansion
                              Candidates = candidates
                              Usage = usage }
            with :? OperationCanceledException as error when
                timeout.IsCancellationRequested && not cancellationToken.IsCancellationRequested ->
                return raise (TimeoutException("Hybrid retrieval exceeded its aggregate duration budget.", error))
        }
