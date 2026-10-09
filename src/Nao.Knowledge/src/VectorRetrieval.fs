namespace Nao.Knowledge

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

type KnowledgeVectorCandidate =
    private
        { Document: KnowledgeIndexDocument
          ProfileId: EmbeddingProfileId
          Score: float
          Rank: int }

[<RequireQualifiedAccess>]
module KnowledgeVectorCandidate =
    let document value = value.Document
    let profileId value = value.ProfileId
    let score value = value.Score
    let rank value = value.Rank

type KnowledgeVectorVariantEvidence =
    private
        { Variant: KnowledgeQueryVariant
          Candidates: KnowledgeVectorCandidate list
          Duration: TimeSpan }

[<RequireQualifiedAccess>]
module KnowledgeVectorVariantEvidence =
    let variant value = value.Variant
    let candidates value = value.Candidates
    let duration value = value.Duration

type KnowledgeVectorRetrieval =
    private
        { Executor: ComponentVersion
          Admission: KnowledgeRetrievalAdmission
          Plan: KnowledgeQueryTransformationPlan
          FilterEvidence: KnowledgeRetrievalFilterEvidence
          Profile: EmbeddingCapabilityDescriptor
          EmbeddingUsage: KnowledgeQueryEmbeddingUsage
          EmbeddingDuration: TimeSpan
          Variants: KnowledgeVectorVariantEvidence list
          QueryCount: int
          CandidateCount: int
          Duration: TimeSpan }

[<RequireQualifiedAccess>]
module KnowledgeVectorRetrieval =
    let executor value = value.Executor
    let admission value = value.Admission
    let plan value = value.Plan
    let filterEvidence value = value.FilterEvidence

    let request value =
        value.Plan |> KnowledgeQueryTransformationPlan.request

    let profile value = value.Profile
    let embeddingUsage value = value.EmbeddingUsage
    let embeddingDuration value = value.EmbeddingDuration
    let variants value = value.Variants
    let queryCount value = value.QueryCount
    let candidateCount value = value.CandidateCount
    let duration value = value.Duration

[<RequireQualifiedAccess>]
type KnowledgeVectorRetrievalResult =
    | Skipped of KnowledgeRetrievalAdmission
    | Retrieved of KnowledgeVectorRetrieval

[<RequireQualifiedAccess>]
module KnowledgeVectorRetrievalExecutor =
    let descriptor = ComponentVersion.create "nao-vector-retrieval" "1"

    let private validateStrategy request =
        match KnowledgeRetrievalRequest.strategy request with
        | KnowledgeRetrievalStrategy.Vector
        | KnowledgeRetrievalStrategy.Hybrid
        | KnowledgeRetrievalStrategy.HybridWithGraph -> ()
        | KnowledgeRetrievalStrategy.Lexical ->
            invalidOp "A lexical-only retrieval request cannot execute the vector retrieval leg."

    let private validateEmbeddingResult profile limits inputs result =
        if isNull (box result) then
            invalidOp "Query embedder returned a null result."

        let embeddings = KnowledgeQueryEmbeddingResult.embeddings result

        if isNull (box embeddings) then
            invalidOp "Query embedder returned a null embedding collection."

        let expected =
            inputs
            |> List.map (fun input -> KnowledgeQueryEmbeddingInput.contentHash input)
            |> Set.ofList

        let actual =
            embeddings |> List.map KnowledgeQueryEmbedding.contentHash |> Set.ofList

        if
            embeddings.Length <> inputs.Length
            || actual.Count <> embeddings.Length
            || actual <> expected
        then
            invalidOp "Query embedder must return exactly one vector for every requested query hash."

        embeddings
        |> List.iter (fun embedding ->
            if (KnowledgeQueryEmbedding.values embedding).Length <> profile.Dimensions then
                invalidOp "Query embedder returned a vector with dimensions outside its declared profile.")

        let usage = KnowledgeQueryEmbeddingResult.usage result

        if isNull (box usage) then
            invalidOp "Query embedder returned null usage evidence."

        if
            KnowledgeQueryEmbeddingUsage.providerCalls usage > KnowledgeQueryEmbeddingLimits.maximumProviderCalls limits
        then
            invalidOp "Query embedding exceeded its provider-call budget."

        if KnowledgeQueryEmbeddingUsage.inputTokens usage > KnowledgeQueryEmbeddingLimits.maximumTokens limits then
            invalidOp "Query embedding exceeded its token budget."

        if
            KnowledgeQueryEmbeddingUsage.estimatedCostUsd usage > KnowledgeQueryEmbeddingLimits.maximumCostUsd limits
        then
            invalidOp "Query embedding exceeded its cost budget."

        embeddings, usage

    let private validateHit request profileId hit =
        if VectorIndexHit.profileId hit <> profileId then
            invalidOp "Vector index returned a hit from a different embedding profile."

        KnowledgeRetrievalBoundary.validateDocument request (VectorIndexHit.document hit)

    let executeAsync
        (embedder: KnowledgeQueryEmbedder)
        (index: KnowledgeVectorIndex)
        (understanding: KnowledgeQueryUnderstandingResult)
        (cancellationToken: CancellationToken)
        : Task<KnowledgeVectorRetrievalResult> =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            if isNull (box understanding) then
                nullArg (nameof understanding)

            match understanding with
            | KnowledgeQueryUnderstandingResult.RetrievalSkipped admission ->
                return KnowledgeVectorRetrievalResult.Skipped admission
            | KnowledgeQueryUnderstandingResult.RetrievalPlanned(admission, plan) ->
                if isNull (box embedder) || isNull (box embedder.EmbedQueriesAsync) then
                    nullArg (nameof embedder)

                if isNull (box index) || isNull (box index.QueryAsync) then
                    nullArg (nameof index)

                let profile = EmbeddingCapabilityDescriptor.validate embedder.Profile
                let request = KnowledgeQueryTransformationPlan.request plan
                validateStrategy request

                let variants = KnowledgeQueryTransformationPlan.variants plan

                if List.isEmpty variants then
                    invalidOp "Vector retrieval requires at least one query variant."

                if
                    variants
                    |> List.exists (fun variant ->
                        (KnowledgeQueryVariant.text variant).Length > profile.Limits.MaximumInputCharactersPerItem)
                then
                    invalidOp "A retrieval query exceeds the query embedder's character limit."

                let budget = KnowledgeRetrievalRequest.budget request
                let transformationUsage = KnowledgeQueryTransformationPlan.usage plan

                let transformationTokens =
                    int64 transformationUsage.InputTokens + int64 transformationUsage.OutputTokens

                let remainingTokens =
                    int64 (KnowledgeRetrievalBudget.maximumTokens budget) - transformationTokens

                let remainingCost =
                    KnowledgeRetrievalBudget.maximumCostUsd budget
                    - transformationUsage.EstimatedCostUsd

                if remainingTokens < 0 || remainingCost < 0m then
                    invalidOp "Query transformation consumed more than the retrieval token or cost budget."

                let embeddingLimits =
                    KnowledgeQueryEmbeddingLimits.create
                        (KnowledgeRetrievalBudget.maximumEmbeddingCalls budget)
                        (int remainingTokens)
                        remainingCost

                let embeddingInputs = variants |> List.map KnowledgeQueryEmbeddingInput.ofVariant

                let embeddingRequest =
                    KnowledgeQueryEmbeddingRequest.create embeddingInputs embeddingLimits

                let limits = KnowledgeRetrievalRequest.limits request
                let perQueryLimit = KnowledgeRetrievalLimits.maximumCandidatesPerQuery limits
                let totalLimit = KnowledgeRetrievalLimits.maximumCandidates limits
                let authorization = KnowledgeRetrievalRequest.authorization request
                let namespaceName = KnowledgeRetrievalRequest.namespaceName request
                let filter = KnowledgeRetrievalRequest.filter request
                use timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                let totalTimer = Stopwatch.StartNew()

                let work =
                    task {
                        let embeddingTimer = Stopwatch.StartNew()

                        let! embeddingResult = embedder.EmbedQueriesAsync authorization embeddingRequest timeout.Token

                        embeddingTimer.Stop()

                        let embeddings, embeddingUsage =
                            validateEmbeddingResult profile embeddingLimits embeddingInputs embeddingResult

                        let byHash =
                            embeddings
                            |> List.map (fun embedding -> KnowledgeQueryEmbedding.contentHash embedding, embedding)
                            |> Map.ofList

                        let! evidence =
                            variants
                            |> List.map (fun variant ->
                                task {
                                    let embedding = byHash[KnowledgeQueryVariant.contentHash variant]

                                    let query =
                                        VectorIndexQuery.create
                                            namespaceName
                                            profile
                                            (KnowledgeQueryEmbedding.values embedding)
                                            filter
                                            perQueryLimit

                                    let timer = Stopwatch.StartNew()
                                    let! hits = index.QueryAsync authorization query timeout.Token
                                    timer.Stop()

                                    if isNull (box hits) then
                                        invalidOp "Vector index returned a null hit collection."

                                    if hits.Length > perQueryLimit then
                                        invalidOp "Vector index exceeded the per-query candidate limit."

                                    hits |> List.iter (validateHit request profile.ProfileId)

                                    let candidates =
                                        hits
                                        |> List.mapi (fun index hit ->
                                            { Document = VectorIndexHit.document hit
                                              ProfileId = VectorIndexHit.profileId hit
                                              Score = VectorIndexHit.score hit
                                              Rank = index + 1 })

                                    return
                                        { Variant = variant
                                          Candidates = candidates
                                          Duration = timer.Elapsed }
                                })
                            |> Task.WhenAll

                        return embeddingUsage, embeddingTimer.Elapsed, List.ofArray evidence
                    }

                let! (embeddingUsage, embeddingDuration, evidence) =
                    task {
                        try
                            return! work.WaitAsync(KnowledgeRetrievalBudget.maximumDuration budget, cancellationToken)
                        with :? TimeoutException as error ->
                            timeout.Cancel()
                            return raise (TimeoutException("Vector retrieval exceeded its duration budget.", error))
                    }

                totalTimer.Stop()

                let candidateCount =
                    evidence |> List.sumBy (fun item -> int64 item.Candidates.Length)

                if candidateCount > int64 totalLimit then
                    invalidOp "Vector retrieval exceeded the total candidate limit."

                return
                    KnowledgeVectorRetrievalResult.Retrieved
                        { Executor = descriptor
                          Admission = admission
                          Plan = plan
                          FilterEvidence = KnowledgeRetrievalBoundary.evidence request (int candidateCount)
                          Profile = profile
                          EmbeddingUsage = embeddingUsage
                          EmbeddingDuration = embeddingDuration
                          Variants = evidence
                          QueryCount = evidence.Length
                          CandidateCount = int candidateCount
                          Duration = totalTimer.Elapsed }
        }
