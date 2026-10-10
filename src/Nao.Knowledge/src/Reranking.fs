namespace Nao.Knowledge

open System
open System.Diagnostics
open System.Globalization
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
type KnowledgeRerankerKind =
    | ScoreBased
    | CrossEncoder
    | LanguageModel

type KnowledgeRerankerDescriptor =
    { Component: ComponentVersion
      Kind: KnowledgeRerankerKind
      Model: ComponentVersion option
      ConfigurationHash: ContentHash
      Deterministic: bool }

type KnowledgeRerankBudget =
    private
        { MaximumDuration: TimeSpan
          MaximumModelCalls: int
          MaximumTokens: int
          MaximumCostUsd: decimal }

[<RequireQualifiedAccess>]
module KnowledgeRerankBudget =
    let create maximumDuration maximumModelCalls maximumTokens maximumCostUsd =
        if maximumDuration <= TimeSpan.Zero then
            invalidArg (nameof maximumDuration) "Reranking duration budget must be positive."

        if maximumModelCalls < 0 then
            invalidArg (nameof maximumModelCalls) "Reranking model-call budget cannot be negative."

        if maximumTokens < 0 then
            invalidArg (nameof maximumTokens) "Reranking token budget cannot be negative."

        if maximumCostUsd < 0m then
            invalidArg (nameof maximumCostUsd) "Reranking cost budget cannot be negative."

        { MaximumDuration = maximumDuration
          MaximumModelCalls = maximumModelCalls
          MaximumTokens = maximumTokens
          MaximumCostUsd = maximumCostUsd }

    let maximumDuration value = value.MaximumDuration
    let maximumModelCalls value = value.MaximumModelCalls
    let maximumTokens value = value.MaximumTokens
    let maximumCostUsd value = value.MaximumCostUsd

type KnowledgeRerankRequest =
    private
        { Candidates: KnowledgeRetrievalCandidates
          MaximumResults: int
          Budget: KnowledgeRerankBudget }

[<RequireQualifiedAccess>]
module KnowledgeRerankRequest =
    let create candidates maximumResults budget =
        if isNull (box candidates) then
            nullArg (nameof candidates)

        let candidateCount =
            candidates |> KnowledgeRetrievalCandidates.candidates |> List.length

        if maximumResults <= 0 || maximumResults > candidateCount then
            invalidArg (nameof maximumResults) "Reranking result limit must fit the candidate set."

        if isNull (box budget) then
            nullArg (nameof budget)

        { Candidates = candidates
          MaximumResults = maximumResults
          Budget = budget }

    let candidates value = value.Candidates

    let retrievalRequest value =
        value.Candidates |> KnowledgeRetrievalCandidates.request

    let maximumResults value = value.MaximumResults
    let budget value = value.Budget

type KnowledgeRerankScore =
    private
        { ChunkId: KnowledgeChunkId
          Score: float }

[<RequireQualifiedAccess>]
module KnowledgeRerankScore =
    let create chunkId score =
        if not (Double.IsFinite score) then
            invalidArg (nameof score) "Reranker scores must be finite."

        { ChunkId = chunkId; Score = score }

    let chunkId value = value.ChunkId
    let score value = value.Score

type KnowledgeRerankerUsage =
    private
        { ModelCalls: int
          InputTokens: int
          OutputTokens: int
          EstimatedCostUsd: decimal }

[<RequireQualifiedAccess>]
module KnowledgeRerankerUsage =
    let create modelCalls inputTokens outputTokens estimatedCostUsd =
        if modelCalls < 0 || inputTokens < 0 || outputTokens < 0 || estimatedCostUsd < 0m then
            invalidArg (nameof modelCalls) "Reranker usage values cannot be negative."

        { ModelCalls = modelCalls
          InputTokens = inputTokens
          OutputTokens = outputTokens
          EstimatedCostUsd = estimatedCostUsd }

    let zero = create 0 0 0 0m
    let modelCalls value = value.ModelCalls
    let inputTokens value = value.InputTokens
    let outputTokens value = value.OutputTokens

    let totalTokens value =
        int64 value.InputTokens + int64 value.OutputTokens

    let estimatedCostUsd value = value.EstimatedCostUsd

type KnowledgeRerankerOutput =
    private
        { Scores: KnowledgeRerankScore list
          Usage: KnowledgeRerankerUsage }

[<RequireQualifiedAccess>]
module KnowledgeRerankerOutput =
    let create scores usage =
        if isNull (box scores) then
            nullArg (nameof scores)

        if isNull (box usage) then
            nullArg (nameof usage)

        { Scores = scores; Usage = usage }

    let scores value = value.Scores
    let usage value = value.Usage

type KnowledgeReranker =
    { Descriptor: KnowledgeRerankerDescriptor
      RerankAsync: AuthorizationScope -> KnowledgeRerankRequest -> CancellationToken -> Task<KnowledgeRerankerOutput> }

type KnowledgeRerankedCandidate =
    private
        { Candidate: KnowledgeScoredCandidate
          OriginalRank: int
          OriginalScore: float
          Rank: int
          Score: float }

[<RequireQualifiedAccess>]
module KnowledgeRerankedCandidate =
    let candidate value = value.Candidate

    let document value =
        value.Candidate |> KnowledgeScoredCandidate.document

    let originalRank value = value.OriginalRank
    let originalScore value = value.OriginalScore
    let rank value = value.Rank
    let score value = value.Score

type KnowledgeReranking =
    private
        { Reranker: KnowledgeRerankerDescriptor
          Request: KnowledgeRerankRequest
          Usage: KnowledgeRerankerUsage
          Candidates: KnowledgeRerankedCandidate list
          Duration: TimeSpan }

[<RequireQualifiedAccess>]
module KnowledgeReranking =
    let reranker value = value.Reranker
    let request value = value.Request
    let usage value = value.Usage
    let candidates value = value.Candidates
    let duration value = value.Duration

[<RequireQualifiedAccess>]
module KnowledgeRerankingExecutor =
    let private validateDescriptor descriptor =
        if isNull (box descriptor) then
            invalidOp "Reranker descriptor cannot be null."

        match descriptor.Kind, descriptor.Model with
        | KnowledgeRerankerKind.ScoreBased, Some _ ->
            invalidOp "A deterministic score-based reranker cannot declare a model."
        | KnowledgeRerankerKind.CrossEncoder, None
        | KnowledgeRerankerKind.LanguageModel, None -> invalidOp "Model-assisted rerankers must declare their model."
        | _ -> ()

        if
            descriptor.Kind = KnowledgeRerankerKind.LanguageModel
            && descriptor.Deterministic
        then
            invalidOp "Language-model rerankers cannot claim deterministic execution."

    let private validateUsage budget usage =
        if isNull (box usage) then
            invalidOp "Reranker returned null usage evidence."

        if KnowledgeRerankerUsage.modelCalls usage > KnowledgeRerankBudget.maximumModelCalls budget then
            invalidOp "Reranker exceeded its model-call budget."

        if KnowledgeRerankerUsage.totalTokens usage > int64 (KnowledgeRerankBudget.maximumTokens budget) then
            invalidOp "Reranker exceeded its token budget."

        if KnowledgeRerankerUsage.estimatedCostUsd usage > KnowledgeRerankBudget.maximumCostUsd budget then
            invalidOp "Reranker exceeded its cost budget."

    let executeAsync
        (reranker: KnowledgeReranker)
        (request: KnowledgeRerankRequest)
        (cancellationToken: CancellationToken)
        : Task<KnowledgeReranking> =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            if isNull (box reranker) || isNull (box reranker.RerankAsync) then
                nullArg (nameof reranker)

            if isNull (box request) then
                nullArg (nameof request)

            validateDescriptor reranker.Descriptor
            let budget = KnowledgeRerankRequest.budget request
            use timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            let timer = Stopwatch.StartNew()

            let invocation =
                reranker.RerankAsync
                    (request
                     |> KnowledgeRerankRequest.retrievalRequest
                     |> KnowledgeRetrievalRequest.authorization)
                    request
                    timeout.Token

            let! output =
                task {
                    try
                        return! invocation.WaitAsync(KnowledgeRerankBudget.maximumDuration budget, cancellationToken)
                    with :? TimeoutException as error ->
                        timeout.Cancel()
                        return raise (TimeoutException("Reranking exceeded its duration budget.", error))
                }

            timer.Stop()

            if isNull (box output) then
                invalidOp "Reranker returned a null output."

            let inputCandidates =
                request
                |> KnowledgeRerankRequest.candidates
                |> KnowledgeRetrievalCandidates.candidates

            let expected =
                inputCandidates
                |> List.map (KnowledgeScoredCandidate.document >> KnowledgeIndexDocument.chunkId)
                |> Set.ofList

            let scores = KnowledgeRerankerOutput.scores output

            let actual = scores |> List.map KnowledgeRerankScore.chunkId |> Set.ofList

            if
                scores.Length <> inputCandidates.Length
                || actual.Count <> scores.Length
                || actual <> expected
            then
                invalidOp "Reranker must return exactly one score for every candidate identity."

            let scoreById =
                scores
                |> List.map (fun score -> KnowledgeRerankScore.chunkId score, KnowledgeRerankScore.score score)
                |> Map.ofList

            let ranked =
                inputCandidates
                |> List.sortBy (fun candidate ->
                    let chunkId =
                        candidate |> KnowledgeScoredCandidate.document |> KnowledgeIndexDocument.chunkId

                    -scoreById[chunkId], KnowledgeScoredCandidate.rank candidate, string chunkId)
                |> List.truncate (KnowledgeRerankRequest.maximumResults request)
                |> List.mapi (fun index candidate ->
                    let chunkId =
                        candidate |> KnowledgeScoredCandidate.document |> KnowledgeIndexDocument.chunkId

                    { Candidate = candidate
                      OriginalRank = KnowledgeScoredCandidate.rank candidate
                      OriginalScore = KnowledgeScoredCandidate.primaryScore candidate
                      Rank = index + 1
                      Score = scoreById[chunkId] })

            let usage = KnowledgeRerankerOutput.usage output
            validateUsage budget usage

            return
                { Reranker = reranker.Descriptor
                  Request = request
                  Usage = usage
                  Candidates = ranked
                  Duration = timer.Elapsed }
        }

type KnowledgeScoreBasedRerankerConfig =
    private
        { RankConstant: int
          RankWeight: float
          EvidenceBreadthWeight: float }

[<RequireQualifiedAccess>]
module KnowledgeScoreBasedRerankerConfig =
    let create rankConstant rankWeight evidenceBreadthWeight =
        if rankConstant <= 0 then
            invalidArg (nameof rankConstant) "Score reranker rank constant must be positive."

        if
            not (Double.IsFinite rankWeight)
            || rankWeight < 0.0
            || not (Double.IsFinite evidenceBreadthWeight)
            || evidenceBreadthWeight < 0.0
            || (rankWeight = 0.0 && evidenceBreadthWeight = 0.0)
        then
            invalidArg (nameof rankWeight) "Score reranker weights must be finite, non-negative, and not both zero."

        { RankConstant = rankConstant
          RankWeight = rankWeight
          EvidenceBreadthWeight = evidenceBreadthWeight }

    let defaultConfig = create 60 1.0 0.0
    let rankConstant value = value.RankConstant
    let rankWeight value = value.RankWeight
    let evidenceBreadthWeight value = value.EvidenceBreadthWeight

[<RequireQualifiedAccess>]
module KnowledgeScoreBasedReranker =
    let create config =
        if isNull (box config) then
            nullArg (nameof config)

        let rankWeight = config.RankWeight.ToString("R", CultureInfo.InvariantCulture)

        let evidenceBreadthWeight =
            config.EvidenceBreadthWeight.ToString("R", CultureInfo.InvariantCulture)

        let configuration =
            String.concat
                "|"
                [ $"rank-constant={config.RankConstant.ToString(CultureInfo.InvariantCulture)}"
                  $"rank-weight={rankWeight}"
                  $"evidence-breadth-weight={evidenceBreadthWeight}" ]

        { Descriptor =
            { Component = ComponentVersion.create "nao-score-based-reranker" "1"
              Kind = KnowledgeRerankerKind.ScoreBased
              Model = None
              ConfigurationHash = ContentHash.computeText configuration
              Deterministic = true }
          RerankAsync =
            fun _ request cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()

                let scores =
                    request
                    |> KnowledgeRerankRequest.candidates
                    |> KnowledgeRetrievalCandidates.candidates
                    |> List.map (fun candidate ->
                        let rankScore =
                            config.RankWeight
                            / float (config.RankConstant + KnowledgeScoredCandidate.rank candidate)

                        let evidenceScore =
                            config.EvidenceBreadthWeight
                            * float (candidate |> KnowledgeScoredCandidate.evidence |> List.length)

                        KnowledgeRerankScore.create
                            (candidate |> KnowledgeScoredCandidate.document |> KnowledgeIndexDocument.chunkId)
                            (rankScore + evidenceScore))

                Task.FromResult(KnowledgeRerankerOutput.create scores KnowledgeRerankerUsage.zero) }
