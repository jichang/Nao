namespace Nao.Knowledge

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks

type KnowledgeLexicalCandidate =
    private
        { Document: KnowledgeIndexDocument
          Score: float
          Rank: int }

[<RequireQualifiedAccess>]
module KnowledgeLexicalCandidate =
    let document value = value.Document
    let score value = value.Score
    let rank value = value.Rank

type KnowledgeLexicalVariantEvidence =
    private
        { Variant: KnowledgeQueryVariant
          Candidates: KnowledgeLexicalCandidate list
          Duration: TimeSpan }

[<RequireQualifiedAccess>]
module KnowledgeLexicalVariantEvidence =
    let variant value = value.Variant
    let candidates value = value.Candidates
    let duration value = value.Duration

type KnowledgeLexicalRetrieval =
    private
        { Executor: ComponentVersion
          Admission: KnowledgeRetrievalAdmission
          Plan: KnowledgeQueryTransformationPlan
          FilterEvidence: KnowledgeRetrievalFilterEvidence
          Variants: KnowledgeLexicalVariantEvidence list
          QueryCount: int
          CandidateCount: int
          Duration: TimeSpan
          EstimatedCostUsd: decimal }

[<RequireQualifiedAccess>]
module KnowledgeLexicalRetrieval =
    let executor value = value.Executor
    let admission value = value.Admission
    let plan value = value.Plan
    let filterEvidence value = value.FilterEvidence

    let request value =
        value.Plan |> KnowledgeQueryTransformationPlan.request

    let variants value = value.Variants
    let queryCount value = value.QueryCount
    let candidateCount value = value.CandidateCount
    let duration value = value.Duration
    let estimatedCostUsd value = value.EstimatedCostUsd

[<RequireQualifiedAccess>]
type KnowledgeLexicalRetrievalResult =
    | Skipped of KnowledgeRetrievalAdmission
    | Retrieved of KnowledgeLexicalRetrieval

[<RequireQualifiedAccess>]
module KnowledgeLexicalRetrievalExecutor =
    let descriptor = ComponentVersion.create "nao-lexical-retrieval" "1"

    let private validateStrategy request =
        match KnowledgeRetrievalRequest.strategy request with
        | KnowledgeRetrievalStrategy.Lexical
        | KnowledgeRetrievalStrategy.Hybrid
        | KnowledgeRetrievalStrategy.HybridWithGraph -> ()
        | KnowledgeRetrievalStrategy.Vector ->
            invalidOp "A vector-only retrieval request cannot execute the lexical retrieval leg."

    let private validateHit request hit =
        let document = LexicalIndexHit.document hit
        KnowledgeRetrievalBoundary.validateDocument request document

    let executeAsync
        (index: KnowledgeLexicalIndex)
        (understanding: KnowledgeQueryUnderstandingResult)
        (cancellationToken: CancellationToken)
        : Task<KnowledgeLexicalRetrievalResult> =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            if isNull (box understanding) then
                nullArg (nameof understanding)

            match understanding with
            | KnowledgeQueryUnderstandingResult.RetrievalSkipped admission ->
                return KnowledgeLexicalRetrievalResult.Skipped admission
            | KnowledgeQueryUnderstandingResult.RetrievalPlanned(admission, plan) ->
                if isNull (box index) || isNull (box index.QueryAsync) then
                    nullArg (nameof index)

                let request = KnowledgeQueryTransformationPlan.request plan
                validateStrategy request

                let variants = KnowledgeQueryTransformationPlan.variants plan

                if List.isEmpty variants then
                    invalidOp "Lexical retrieval requires at least one query variant."

                let limits = KnowledgeRetrievalRequest.limits request
                let perQueryLimit = KnowledgeRetrievalLimits.maximumCandidatesPerQuery limits
                let totalLimit = KnowledgeRetrievalLimits.maximumCandidates limits
                let authorization = KnowledgeRetrievalRequest.authorization request
                let namespaceName = KnowledgeRetrievalRequest.namespaceName request
                let filter = KnowledgeRetrievalRequest.filter request
                let budget = KnowledgeRetrievalRequest.budget request
                use timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                let totalTimer = Stopwatch.StartNew()

                let retrieval =
                    variants
                    |> List.map (fun variant ->
                        task {
                            let query =
                                LexicalIndexQuery.create
                                    namespaceName
                                    (KnowledgeQueryVariant.text variant)
                                    filter
                                    perQueryLimit

                            let timer = Stopwatch.StartNew()
                            let! hits = index.QueryAsync authorization query timeout.Token
                            timer.Stop()

                            if isNull (box hits) then
                                invalidOp "Lexical index returned a null hit collection."

                            if hits.Length > perQueryLimit then
                                invalidOp "Lexical index exceeded the per-query candidate limit."

                            hits |> List.iter (validateHit request)

                            let candidates =
                                hits
                                |> List.mapi (fun index hit ->
                                    { Document = LexicalIndexHit.document hit
                                      Score = LexicalIndexHit.score hit
                                      Rank = index + 1 })

                            return
                                { Variant = variant
                                  Candidates = candidates
                                  Duration = timer.Elapsed }
                        })
                    |> Task.WhenAll

                let! evidence =
                    task {
                        try
                            return!
                                retrieval.WaitAsync(KnowledgeRetrievalBudget.maximumDuration budget, cancellationToken)
                        with :? TimeoutException as error ->
                            timeout.Cancel()
                            return raise (TimeoutException("Lexical retrieval exceeded its duration budget.", error))
                    }

                totalTimer.Stop()
                let evidence = List.ofArray evidence

                let candidateCount =
                    evidence |> List.sumBy (fun item -> int64 item.Candidates.Length)

                if candidateCount > int64 totalLimit then
                    invalidOp "Lexical retrieval exceeded the total candidate limit."

                return
                    KnowledgeLexicalRetrievalResult.Retrieved
                        { Executor = descriptor
                          Admission = admission
                          Plan = plan
                          FilterEvidence = KnowledgeRetrievalBoundary.evidence request (int candidateCount)
                          Variants = evidence
                          QueryCount = evidence.Length
                          CandidateCount = int candidateCount
                          Duration = totalTimer.Elapsed
                          EstimatedCostUsd = 0m }
        }
