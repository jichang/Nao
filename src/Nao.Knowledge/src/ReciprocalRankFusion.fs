namespace Nao.Knowledge

open System
open System.Collections.Generic

[<RequireQualifiedAccess>]
type KnowledgeRetrievalLeg =
    | Lexical
    | Vector

type KnowledgeReciprocalRankFusionConfig =
    private
        { RankConstant: int
          LexicalWeight: float
          VectorWeight: float }

[<RequireQualifiedAccess>]
module KnowledgeReciprocalRankFusionConfig =
    let create rankConstant lexicalWeight vectorWeight =
        if rankConstant <= 0 then
            invalidArg (nameof rankConstant) "Reciprocal-rank fusion constant must be positive."

        if not (Double.IsFinite lexicalWeight) || lexicalWeight <= 0.0 then
            invalidArg (nameof lexicalWeight) "Lexical fusion weight must be finite and positive."

        if not (Double.IsFinite vectorWeight) || vectorWeight <= 0.0 then
            invalidArg (nameof vectorWeight) "Vector fusion weight must be finite and positive."

        { RankConstant = rankConstant
          LexicalWeight = lexicalWeight
          VectorWeight = vectorWeight }

    let defaultConfig = create 60 1.0 1.0
    let rankConstant value = value.RankConstant
    let lexicalWeight value = value.LexicalWeight
    let vectorWeight value = value.VectorWeight

type KnowledgeRankFusionContribution =
    private
        { Leg: KnowledgeRetrievalLeg
          Variant: KnowledgeQueryVariant
          Rank: int
          RawScore: float
          Weight: float
          Contribution: float }

[<RequireQualifiedAccess>]
module KnowledgeRankFusionContribution =
    let leg value = value.Leg
    let variant value = value.Variant
    let rank value = value.Rank
    let rawScore value = value.RawScore
    let weight value = value.Weight
    let contribution value = value.Contribution

type KnowledgeFusedCandidate =
    private
        { Document: KnowledgeIndexDocument
          Rank: int
          Score: float
          Contributions: KnowledgeRankFusionContribution list }

[<RequireQualifiedAccess>]
module KnowledgeFusedCandidate =
    let document value = value.Document
    let rank value = value.Rank
    let score value = value.Score
    let contributions value = value.Contributions

type KnowledgeReciprocalRankFusion =
    private
        { Fusion: ComponentVersion
          Config: KnowledgeReciprocalRankFusionConfig
          Lexical: KnowledgeLexicalRetrieval
          Vector: KnowledgeVectorRetrieval
          InputLists: int
          InputCandidates: int
          Candidates: KnowledgeFusedCandidate list }

[<RequireQualifiedAccess>]
module KnowledgeReciprocalRankFusion =
    let fusion value = value.Fusion
    let config value = value.Config
    let lexical value = value.Lexical
    let vector value = value.Vector

    let request value =
        value.Lexical |> KnowledgeLexicalRetrieval.request

    let inputLists value = value.InputLists
    let inputCandidates value = value.InputCandidates
    let candidates value = value.Candidates

[<RequireQualifiedAccess>]
module KnowledgeReciprocalRankFusionExecutor =
    let descriptor = ComponentVersion.create "nao-reciprocal-rank-fusion" "1"

    type private CandidateAccumulator =
        { Document: KnowledgeIndexDocument
          Contributions: ResizeArray<KnowledgeRankFusionContribution> }

    let private validateCompatibility lexical vector =
        let lexicalPlan = KnowledgeLexicalRetrieval.plan lexical
        let vectorPlan = KnowledgeVectorRetrieval.plan vector

        if not (obj.ReferenceEquals(lexicalPlan, vectorPlan)) then
            invalidOp "Hybrid fusion requires lexical and vector evidence from the exact same query plan."

        if
            not (
                obj.ReferenceEquals(
                    KnowledgeLexicalRetrieval.admission lexical,
                    KnowledgeVectorRetrieval.admission vector
                )
            )
        then
            invalidOp "Hybrid fusion requires lexical and vector evidence from the same admission decision."

        let request = KnowledgeQueryTransformationPlan.request lexicalPlan

        match KnowledgeRetrievalRequest.strategy request with
        | KnowledgeRetrievalStrategy.Hybrid
        | KnowledgeRetrievalStrategy.HybridWithGraph -> request
        | KnowledgeRetrievalStrategy.Lexical
        | KnowledgeRetrievalStrategy.Vector -> invalidOp "Reciprocal-rank fusion requires a hybrid retrieval request."

    let fuse config lexical vector =
        if isNull (box config) then
            nullArg (nameof config)

        if isNull (box lexical) then
            nullArg (nameof lexical)

        if isNull (box vector) then
            nullArg (nameof vector)

        let request = validateCompatibility lexical vector
        let rankConstant = KnowledgeReciprocalRankFusionConfig.rankConstant config
        let accumulators = Dictionary<KnowledgeChunkId, CandidateAccumulator>()
        let mutable inputLists = 0
        let mutable inputCandidates = 0L

        let addList leg weight variant candidates document rank rawScore =
            inputLists <- inputLists + 1
            let seen = HashSet<KnowledgeChunkId>()

            candidates
            |> List.iter (fun candidate ->
                let candidateDocument = document candidate
                let chunkId = KnowledgeIndexDocument.chunkId candidateDocument

                if not (seen.Add chunkId) then
                    invalidOp "A retrieval ranking list cannot contain the same chunk more than once."

                let candidateRank = rank candidate
                let candidateRawScore = rawScore candidate

                if candidateRank <= 0 || not (Double.IsFinite candidateRawScore) then
                    invalidOp "Retrieval candidate rank and raw score must be valid before fusion."

                let contribution = weight / (float rankConstant + float candidateRank)

                let evidence =
                    { Leg = leg
                      Variant = variant
                      Rank = candidateRank
                      RawScore = candidateRawScore
                      Weight = weight
                      Contribution = contribution }

                match accumulators.TryGetValue chunkId with
                | true, accumulator ->
                    if accumulator.Document <> candidateDocument then
                        invalidOp "The same chunk identity resolved to inconsistent documents during fusion."

                    accumulator.Contributions.Add evidence
                | false, _ ->
                    accumulators.Add(
                        chunkId,
                        { Document = candidateDocument
                          Contributions = ResizeArray [ evidence ] }
                    )

                inputCandidates <- inputCandidates + 1L)

        KnowledgeLexicalRetrieval.variants lexical
        |> List.iter (fun evidence ->
            addList
                KnowledgeRetrievalLeg.Lexical
                (KnowledgeReciprocalRankFusionConfig.lexicalWeight config)
                (KnowledgeLexicalVariantEvidence.variant evidence)
                (KnowledgeLexicalVariantEvidence.candidates evidence)
                KnowledgeLexicalCandidate.document
                KnowledgeLexicalCandidate.rank
                KnowledgeLexicalCandidate.score)

        KnowledgeVectorRetrieval.variants vector
        |> List.iter (fun evidence ->
            addList
                KnowledgeRetrievalLeg.Vector
                (KnowledgeReciprocalRankFusionConfig.vectorWeight config)
                (KnowledgeVectorVariantEvidence.variant evidence)
                (KnowledgeVectorVariantEvidence.candidates evidence)
                KnowledgeVectorCandidate.document
                KnowledgeVectorCandidate.rank
                KnowledgeVectorCandidate.score)

        let maximumCandidates =
            request
            |> KnowledgeRetrievalRequest.limits
            |> KnowledgeRetrievalLimits.maximumCandidates

        let candidates =
            accumulators
            |> Seq.map (fun pair ->
                let contributions = List.ofSeq pair.Value.Contributions

                pair.Key,
                pair.Value.Document,
                contributions,
                (contributions |> List.sumBy KnowledgeRankFusionContribution.contribution),
                (contributions
                 |> List.minBy KnowledgeRankFusionContribution.rank
                 |> KnowledgeRankFusionContribution.rank))
            |> Seq.sortWith (fun (leftId, _, _, leftScore, leftBestRank) (rightId, _, _, rightScore, rightBestRank) ->
                let scoreOrder = compare rightScore leftScore

                if scoreOrder <> 0 then
                    scoreOrder
                else
                    let rankOrder = compare leftBestRank rightBestRank

                    if rankOrder <> 0 then
                        rankOrder
                    else
                        compare (KnowledgeChunkId.value leftId) (KnowledgeChunkId.value rightId))
            |> Seq.truncate maximumCandidates
            |> Seq.mapi (fun index (_, document, contributions, score, _) ->
                { Document = document
                  Rank = index + 1
                  Score = score
                  Contributions = contributions })
            |> List.ofSeq

        if inputCandidates > int64 Int32.MaxValue then
            invalidOp "Fusion input candidate evidence exceeds the supported count."

        { Fusion = descriptor
          Config = config
          Lexical = lexical
          Vector = vector
          InputLists = inputLists
          InputCandidates = int inputCandidates
          Candidates = candidates }
