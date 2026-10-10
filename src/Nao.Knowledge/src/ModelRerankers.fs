namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

type KnowledgeModelRerankCandidate =
    private
        { ChunkId: KnowledgeChunkId
          Text: string }

[<RequireQualifiedAccess>]
module KnowledgeModelRerankCandidate =
    let chunkId value = value.ChunkId
    let text value = value.Text

type KnowledgeModelRerankRequest =
    private
        { Query: string
          Candidates: KnowledgeModelRerankCandidate list
          MaximumModelCalls: int
          MaximumTokens: int
          MaximumCostUsd: decimal }

[<RequireQualifiedAccess>]
module KnowledgeModelRerankRequest =
    let query value = value.Query
    let candidates value = value.Candidates
    let maximumModelCalls value = value.MaximumModelCalls
    let maximumTokens value = value.MaximumTokens
    let maximumCostUsd value = value.MaximumCostUsd

type KnowledgeCrossEncoderScorer =
    { ScoreAsync:
        AuthorizationScope -> KnowledgeModelRerankRequest -> CancellationToken -> Task<KnowledgeRerankerOutput> }

type KnowledgeLanguageModelReranker =
    { ScoreAsync:
        AuthorizationScope -> KnowledgeModelRerankRequest -> CancellationToken -> Task<KnowledgeRerankerOutput> }

type KnowledgeModelRerankerConfig =
    private
        { Model: ComponentVersion
          ConfigurationHash: ContentHash
          MaximumQueryCharacters: int
          MaximumCandidateCharacters: int }

[<RequireQualifiedAccess>]
module KnowledgeModelRerankerConfig =
    let create model configurationHash maximumQueryCharacters maximumCandidateCharacters =
        if isNull (box model) then
            nullArg (nameof model)

        if maximumQueryCharacters <= 0 then
            invalidArg (nameof maximumQueryCharacters) "Reranker query character limit must be positive."

        if maximumCandidateCharacters <= 0 then
            invalidArg (nameof maximumCandidateCharacters) "Reranker candidate character limit must be positive."

        { Model = model
          ConfigurationHash = configurationHash
          MaximumQueryCharacters = maximumQueryCharacters
          MaximumCandidateCharacters = maximumCandidateCharacters }

    let model value = value.Model
    let configurationHash value = value.ConfigurationHash
    let maximumQueryCharacters value = value.MaximumQueryCharacters
    let maximumCandidateCharacters value = value.MaximumCandidateCharacters

[<RequireQualifiedAccess>]
module private KnowledgeModelReranker =
    let request config request =
        let retrievalRequest = KnowledgeRerankRequest.retrievalRequest request
        let query = KnowledgeRetrievalRequest.query retrievalRequest

        if query.Length > KnowledgeModelRerankerConfig.maximumQueryCharacters config then
            invalidOp "Retrieval query exceeds the model reranker's character limit."

        let candidates =
            request
            |> KnowledgeRerankRequest.candidates
            |> KnowledgeRetrievalCandidates.candidates
            |> List.map (fun candidate ->
                let document = KnowledgeScoredCandidate.document candidate
                let text = KnowledgeIndexDocument.text document

                if text.Length > KnowledgeModelRerankerConfig.maximumCandidateCharacters config then
                    invalidOp "A retrieval candidate exceeds the model reranker's character limit."

                { ChunkId = KnowledgeIndexDocument.chunkId document
                  Text = text })

        let budget = KnowledgeRerankRequest.budget request
        let maximumModelCalls = KnowledgeRerankBudget.maximumModelCalls budget

        if maximumModelCalls <= 0 then
            invalidOp "Model-assisted reranking requires a positive model-call budget."

        { Query = query
          Candidates = candidates
          MaximumModelCalls = maximumModelCalls
          MaximumTokens = KnowledgeRerankBudget.maximumTokens budget
          MaximumCostUsd = KnowledgeRerankBudget.maximumCostUsd budget }

    let validateUsage output =
        if isNull (box output) then
            invalidOp "Model-assisted reranker returned a null output."

        if
            output
            |> KnowledgeRerankerOutput.usage
            |> KnowledgeRerankerUsage.modelCalls
            |> (=) 0
        then
            invalidOp "Model-assisted reranker usage must record at least one model call."

[<RequireQualifiedAccess>]
module KnowledgeCrossEncoderReranker =
    let create config deterministic (scorer: KnowledgeCrossEncoderScorer) =
        if isNull (box config) then
            nullArg (nameof config)

        if isNull (box scorer) || isNull (box scorer.ScoreAsync) then
            nullArg (nameof scorer)

        { Descriptor =
            { Component = ComponentVersion.create "nao-cross-encoder-reranker" "1"
              Kind = KnowledgeRerankerKind.CrossEncoder
              Model = Some(KnowledgeModelRerankerConfig.model config)
              ConfigurationHash = KnowledgeModelRerankerConfig.configurationHash config
              Deterministic = deterministic }
          RerankAsync =
            fun authorization request cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()
                    let modelRequest = KnowledgeModelReranker.request config request
                    let! output = scorer.ScoreAsync authorization modelRequest cancellationToken
                    KnowledgeModelReranker.validateUsage output
                    return output
                } }

[<RequireQualifiedAccess>]
module KnowledgeLanguageModelRerankerAdapter =
    let create config (model: KnowledgeLanguageModelReranker) =
        if isNull (box config) then
            nullArg (nameof config)

        if isNull (box model) || isNull (box model.ScoreAsync) then
            nullArg (nameof model)

        { Descriptor =
            { Component = ComponentVersion.create "nao-language-model-reranker" "1"
              Kind = KnowledgeRerankerKind.LanguageModel
              Model = Some(KnowledgeModelRerankerConfig.model config)
              ConfigurationHash = KnowledgeModelRerankerConfig.configurationHash config
              Deterministic = false }
          RerankAsync =
            fun authorization request cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()
                    let modelRequest = KnowledgeModelReranker.request config request
                    let! output = model.ScoreAsync authorization modelRequest cancellationToken
                    KnowledgeModelReranker.validateUsage output
                    return output
                } }
