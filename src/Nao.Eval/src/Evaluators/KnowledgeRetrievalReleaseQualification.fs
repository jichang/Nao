namespace Nao.Eval

open System
open Nao.Knowledge

type KnowledgeRetrievalAdapterDescriptor =
    private
        { Component: ComponentVersion
          ConfigurationHash: ContentHash }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalAdapterDescriptor =
    let create adapterComponent configurationHash =
        if isNull (box adapterComponent) then
            nullArg (nameof adapterComponent)

        { Component = adapterComponent
          ConfigurationHash = configurationHash }

    let adapterComponent value = value.Component
    let configurationHash value = value.ConfigurationHash

type KnowledgeRetrievalReleaseProfile =
    private
        { Id: string
          Version: int
          AdversarialContentHash: ContentHash
          DatasetContentHash: ContentHash
          Evaluator: ComponentVersion
          GateConfig: KnowledgeQualityGateConfig }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalReleaseProfile =
    let currentVersion = 1

    let adversarialV1 =
        let adversarial = KnowledgeAdversarialDatasets.adversarialV1
        let dataset = KnowledgeAdversarialDataset.evaluationDataset adversarial

        let minimum metric value =
            KnowledgeMetricThreshold.create (KnowledgeQualityMetricKey.overall metric) value 0m

        { Id = "nao.rag-retrieval-release"
          Version = currentVersion
          AdversarialContentHash = KnowledgeAdversarialDataset.contentHash adversarial
          DatasetContentHash = KnowledgeEvaluationDataset.contentHash dataset
          Evaluator = KnowledgeHybridRetrievalEvaluator.descriptor
          GateConfig =
            KnowledgeQualityGateConfig.create
                5
                [ minimum KnowledgeQualityMetric.RetrievalRecall 0.90m
                  minimum KnowledgeQualityMetric.RetrievalPrecision 0.90m
                  minimum KnowledgeQualityMetric.MeanReciprocalRank 0.95m
                  minimum KnowledgeQualityMetric.NormalizedDiscountedCumulativeGain 0.95m
                  minimum KnowledgeQualityMetric.MeanLatencyMilliseconds 250m
                  minimum KnowledgeQualityMetric.P95LatencyMilliseconds 500m
                  minimum KnowledgeQualityMetric.TotalCostUsd 0.05m ] }

    let id value = value.Id
    let version value = value.Version
    let adversarialContentHash value = value.AdversarialContentHash
    let datasetContentHash value = value.DatasetContentHash
    let evaluator value = value.Evaluator
    let gateConfig value = value.GateConfig

type KnowledgeRetrievalReleaseViolation =
    private
        { CaseId: string
          ChunkId: KnowledgeChunkId }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalReleaseViolation =
    let caseId value = value.CaseId
    let chunkId value = value.ChunkId

[<RequireQualifiedAccess>]
type KnowledgeRetrievalReleaseFailure =
    | DatasetMismatch
    | EvaluatorMismatch
    | HybridComparisonFailed
    | QualityGateFailed
    | SafetyViolation of KnowledgeRetrievalReleaseViolation
    | IsolationViolation of KnowledgeRetrievalReleaseViolation

type KnowledgeRetrievalReleaseEvidence =
    private
        { Profile: KnowledgeRetrievalReleaseProfile
          Adapter: KnowledgeRetrievalAdapterDescriptor
          Evaluation: KnowledgeHybridRetrievalEvaluation
          GateResult: KnowledgeQualityGateResult
          QualifiedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalReleaseEvidence =
    let profile value = value.Profile
    let adapter value = value.Adapter
    let evaluation value = value.Evaluation
    let gateResult value = value.GateResult
    let qualifiedAt value = value.QualifiedAt

[<RequireQualifiedAccess>]
type KnowledgeRetrievalReleaseQualificationResult =
    | Qualified of KnowledgeRetrievalReleaseEvidence
    | Rejected of KnowledgeRetrievalReleaseFailure list * KnowledgeQualityGateResult

[<RequireQualifiedAccess>]
module KnowledgeRetrievalReleaseQualificationResult =
    let passed =
        function
        | KnowledgeRetrievalReleaseQualificationResult.Qualified _ -> true
        | KnowledgeRetrievalReleaseQualificationResult.Rejected _ -> false

    let failures =
        function
        | KnowledgeRetrievalReleaseQualificationResult.Qualified _ -> []
        | KnowledgeRetrievalReleaseQualificationResult.Rejected(failures, _) -> failures

    let gateResult =
        function
        | KnowledgeRetrievalReleaseQualificationResult.Qualified evidence ->
            KnowledgeRetrievalReleaseEvidence.gateResult evidence
        | KnowledgeRetrievalReleaseQualificationResult.Rejected(_, gateResult) -> gateResult

[<RequireQualifiedAccess>]
module KnowledgeRetrievalReleaseQualification =
    let private prohibitedChunkIds adversarial case =
        let corpus = KnowledgeAdversarialDataset.corpus adversarial

        let chunksByDocument =
            corpus |> List.groupBy KnowledgeAdversarialCorpusChunk.documentId |> Map.ofList

        case
        |> KnowledgeEvaluationCase.prohibitedResults
        |> List.collect (fun prohibited ->
            match KnowledgeProhibitedResult.target prohibited with
            | KnowledgeProhibitedResultTarget.Chunk chunkId -> [ chunkId ]
            | KnowledgeProhibitedResultTarget.Document documentId ->
                chunksByDocument
                |> Map.find documentId
                |> List.map KnowledgeAdversarialCorpusChunk.chunkId)
        |> Set.ofList

    let qualify profile adapter qualifiedAt evaluation =
        if isNull (box profile) then
            nullArg (nameof profile)

        if isNull (box adapter) then
            nullArg (nameof adapter)

        if isNull (box evaluation) then
            nullArg (nameof evaluation)

        let adversarial = KnowledgeAdversarialDatasets.adversarialV1
        let dataset = KnowledgeHybridRetrievalEvaluation.dataset evaluation

        let datasetMatches =
            let actualAdversarialHash = KnowledgeAdversarialDataset.contentHash adversarial

            let expectedAdversarialHash =
                KnowledgeRetrievalReleaseProfile.adversarialContentHash profile

            let actualDatasetHash = KnowledgeEvaluationDataset.contentHash dataset

            let expectedDatasetHash =
                KnowledgeRetrievalReleaseProfile.datasetContentHash profile

            actualAdversarialHash = expectedAdversarialHash
            && actualDatasetHash = expectedDatasetHash

        let evaluatorMatches =
            let actualEvaluator = KnowledgeHybridRetrievalEvaluation.evaluator evaluation
            let expectedEvaluator = KnowledgeRetrievalReleaseProfile.evaluator profile
            actualEvaluator = expectedEvaluator

        let unauthorizedChunkIds =
            adversarial
            |> KnowledgeAdversarialDataset.corpus
            |> List.filter (fun chunk ->
                let access = KnowledgeAdversarialCorpusChunk.access chunk
                access = KnowledgeAdversarialAccess.Unauthorized)
            |> List.map KnowledgeAdversarialCorpusChunk.chunkId
            |> Set.ofList

        let safetyViolations, isolationViolations =
            evaluation
            |> KnowledgeHybridRetrievalEvaluation.hybrid
            |> KnowledgeRetrievalStrategyQuality.cases
            |> List.collect (fun measurement ->
                let case = KnowledgeRetrievalCaseMeasurement.case measurement
                let caseId = KnowledgeEvaluationCase.id case
                let prohibited = prohibitedChunkIds adversarial case

                measurement
                |> KnowledgeRetrievalCaseMeasurement.retrieved
                |> List.map (fun chunkId ->
                    let violation = { CaseId = caseId; ChunkId = chunkId }

                    (if prohibited.Contains chunkId then Some violation else None),
                    (if unauthorizedChunkIds.Contains chunkId then
                         Some violation
                     else
                         None)))
            |> List.fold
                (fun (safety, isolation) (safetyViolation, isolationViolation) ->
                    safetyViolation |> Option.toList |> List.append safety,
                    isolationViolation |> Option.toList |> List.append isolation)
                ([], [])

        let snapshot = KnowledgeQualitySnapshot.ofHybridRetrieval qualifiedAt evaluation

        let gateResult =
            KnowledgeQualityGate.evaluate (KnowledgeRetrievalReleaseProfile.gateConfig profile) None snapshot

        let failures =
            [ if not datasetMatches then
                  KnowledgeRetrievalReleaseFailure.DatasetMismatch
              if not evaluatorMatches then
                  KnowledgeRetrievalReleaseFailure.EvaluatorMismatch
              if not (KnowledgeHybridRetrievalEvaluation.accepted evaluation) then
                  KnowledgeRetrievalReleaseFailure.HybridComparisonFailed
              if not (KnowledgeQualityGateResult.passed gateResult) then
                  KnowledgeRetrievalReleaseFailure.QualityGateFailed
              yield! safetyViolations |> List.map KnowledgeRetrievalReleaseFailure.SafetyViolation
              yield!
                  isolationViolations
                  |> List.map KnowledgeRetrievalReleaseFailure.IsolationViolation ]

        match failures with
        | [] ->
            KnowledgeRetrievalReleaseQualificationResult.Qualified
                { Profile = profile
                  Adapter = adapter
                  Evaluation = evaluation
                  GateResult = gateResult
                  QualifiedAt = qualifiedAt }
        | _ -> KnowledgeRetrievalReleaseQualificationResult.Rejected(failures, gateResult)
