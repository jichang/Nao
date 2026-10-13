namespace Nao.Eval

open System
open Nao.Knowledge

type KnowledgeGroundedReleaseProfile =
    private
        { Id: string
          Version: int
          DatasetName: string
          DatasetRevision: int
          DatasetContentHash: ContentHash
          Evaluator: ComponentVersion
          GateConfig: KnowledgeQualityGateConfig }

[<RequireQualifiedAccess>]
module KnowledgeGroundedReleaseProfile =
    let currentVersion = 1

    let adversarialV1 =
        let dataset =
            KnowledgeAdversarialDatasets.adversarialV1
            |> KnowledgeAdversarialDataset.evaluationDataset

        let threshold metric minimum =
            KnowledgeMetricThreshold.create (KnowledgeQualityMetricKey.overall metric) minimum 0m

        { Id = "nao.rag-grounded-release"
          Version = currentVersion
          DatasetName = KnowledgeEvaluationDataset.name dataset
          DatasetRevision = KnowledgeEvaluationDataset.revision dataset
          DatasetContentHash = KnowledgeEvaluationDataset.contentHash dataset
          Evaluator = KnowledgeGroundedGenerationEvaluator.descriptor
          GateConfig =
            KnowledgeQualityGateConfig.create
                5
                [ threshold KnowledgeQualityMetric.ContextRelevance 0.90m
                  threshold KnowledgeQualityMetric.AnswerFaithfulness 0.95m
                  threshold KnowledgeQualityMetric.CitationCorrectness 0.95m
                  threshold KnowledgeQualityMetric.AbstentionAccuracy 1.00m ] }

    let id value = value.Id
    let version value = value.Version
    let datasetName value = value.DatasetName
    let datasetRevision value = value.DatasetRevision
    let datasetContentHash value = value.DatasetContentHash
    let evaluator value = value.Evaluator
    let gateConfig value = value.GateConfig

[<RequireQualifiedAccess>]
type KnowledgeGroundedReleaseFailure =
    | DatasetMismatch
    | EvaluatorMismatch
    | InconsistentSupportEvaluator
    | UnresolvedResponses
    | QualityGateFailed

type KnowledgeGroundedReleaseEvidence =
    private
        { Profile: KnowledgeGroundedReleaseProfile
          GenerationAdapter: KnowledgeGenerationAdapterDescriptor
          SupportEvaluator: KnowledgeClaimSupportEvaluatorDescriptor
          Quality: KnowledgeGroundedGenerationQuality
          GateResult: KnowledgeQualityGateResult
          QualifiedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
module KnowledgeGroundedReleaseEvidence =
    let profile value = value.Profile
    let generationAdapter value = value.GenerationAdapter
    let supportEvaluator value = value.SupportEvaluator
    let quality value = value.Quality
    let gateResult value = value.GateResult
    let qualifiedAt value = value.QualifiedAt

[<RequireQualifiedAccess>]
type KnowledgeGroundedReleaseQualificationResult =
    | Qualified of KnowledgeGroundedReleaseEvidence
    | Rejected of KnowledgeGroundedReleaseFailure list * KnowledgeQualityGateResult

[<RequireQualifiedAccess>]
module KnowledgeGroundedReleaseQualificationResult =
    let passed =
        function
        | KnowledgeGroundedReleaseQualificationResult.Qualified _ -> true
        | KnowledgeGroundedReleaseQualificationResult.Rejected _ -> false

    let failures =
        function
        | KnowledgeGroundedReleaseQualificationResult.Qualified _ -> []
        | KnowledgeGroundedReleaseQualificationResult.Rejected(failures, _) -> failures

    let gateResult =
        function
        | KnowledgeGroundedReleaseQualificationResult.Qualified evidence ->
            KnowledgeGroundedReleaseEvidence.gateResult evidence
        | KnowledgeGroundedReleaseQualificationResult.Rejected(_, gateResult) -> gateResult

[<RequireQualifiedAccess>]
module KnowledgeGroundedReleaseQualification =
    let qualify profile generationAdapter qualifiedAt quality =
        if isNull (box profile) then
            nullArg (nameof profile)

        if isNull (box generationAdapter) then
            nullArg (nameof generationAdapter)

        if isNull (box quality) then
            nullArg (nameof quality)

        let dataset = KnowledgeGroundedGenerationQuality.dataset quality

        let datasetNameMatches =
            let actual = KnowledgeEvaluationDataset.name dataset
            let expected = KnowledgeGroundedReleaseProfile.datasetName profile
            actual = expected

        let datasetRevisionMatches =
            let actual = KnowledgeEvaluationDataset.revision dataset
            let expected = KnowledgeGroundedReleaseProfile.datasetRevision profile
            actual = expected

        let datasetContentMatches =
            let actual = KnowledgeEvaluationDataset.contentHash dataset
            let expected = KnowledgeGroundedReleaseProfile.datasetContentHash profile
            actual = expected

        let datasetMatches =
            datasetNameMatches && datasetRevisionMatches && datasetContentMatches

        let evaluatorMatches =
            let actual = KnowledgeGroundedGenerationQuality.evaluator quality
            let expected = KnowledgeGroundedReleaseProfile.evaluator profile
            actual = expected

        let supportEvaluators =
            quality
            |> KnowledgeGroundedGenerationQuality.cases
            |> List.map KnowledgeGenerationCaseMeasurement.supportEvaluator
            |> List.distinct

        let supportEvaluator =
            match supportEvaluators with
            | [ descriptor ] -> Some descriptor
            | _ -> None

        let snapshot = KnowledgeQualitySnapshot.ofGroundedGeneration qualifiedAt quality

        let gateResult =
            KnowledgeQualityGate.evaluate (KnowledgeGroundedReleaseProfile.gateConfig profile) None snapshot

        let failures =
            [ if not datasetMatches then
                  KnowledgeGroundedReleaseFailure.DatasetMismatch
              if not evaluatorMatches then
                  KnowledgeGroundedReleaseFailure.EvaluatorMismatch
              if supportEvaluator.IsNone then
                  KnowledgeGroundedReleaseFailure.InconsistentSupportEvaluator
              if KnowledgeGroundedGenerationQuality.unresolvedResponses quality <> 0 then
                  KnowledgeGroundedReleaseFailure.UnresolvedResponses
              if not (KnowledgeQualityGateResult.passed gateResult) then
                  KnowledgeGroundedReleaseFailure.QualityGateFailed ]

        match failures, supportEvaluator with
        | [], Some descriptor ->
            KnowledgeGroundedReleaseQualificationResult.Qualified
                { Profile = profile
                  GenerationAdapter = generationAdapter
                  SupportEvaluator = descriptor
                  Quality = quality
                  GateResult = gateResult
                  QualifiedAt = qualifiedAt }
        | _ -> KnowledgeGroundedReleaseQualificationResult.Rejected(failures, gateResult)
