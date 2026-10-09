namespace Nao.Knowledge

open System.Collections.Generic

type KnowledgeRetrievalDiversityConfig =
    private
        { MaximumCandidates: int
          MaximumPerSource: int option
          MaximumPerDocumentVersion: int option
          SuppressExactContentDuplicates: bool }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalDiversityConfig =
    let private validateOptionalLimit name maximumCandidates =
        function
        | Some value when value <= 0 -> invalidArg name "Retrieval diversity limits must be positive."
        | Some value when value > maximumCandidates ->
            invalidArg name "A retrieval diversity group limit cannot exceed the selected-candidate limit."
        | value -> value

    let create maximumCandidates maximumPerSource maximumPerDocumentVersion suppressExactContentDuplicates =
        if maximumCandidates <= 0 then
            invalidArg (nameof maximumCandidates) "Selected retrieval candidate limit must be positive."

        { MaximumCandidates = maximumCandidates
          MaximumPerSource = validateOptionalLimit (nameof maximumPerSource) maximumCandidates maximumPerSource
          MaximumPerDocumentVersion =
            validateOptionalLimit (nameof maximumPerDocumentVersion) maximumCandidates maximumPerDocumentVersion
          SuppressExactContentDuplicates = suppressExactContentDuplicates }

    let unrestricted maximumCandidates =
        create maximumCandidates None None false

    let maximumCandidates value = value.MaximumCandidates
    let maximumPerSource value = value.MaximumPerSource
    let maximumPerDocumentVersion value = value.MaximumPerDocumentVersion
    let suppressExactContentDuplicates value = value.SuppressExactContentDuplicates

[<RequireQualifiedAccess>]
type KnowledgeCandidateSuppressionReason =
    | ExactContentDuplicate of selectedChunkId: KnowledgeChunkId
    | SourceLimit of sourceId: KnowledgeSourceId
    | DocumentVersionLimit of documentVersionId: KnowledgeDocumentVersionId
    | CandidateLimit

[<RequireQualifiedAccess>]
type KnowledgeCandidateSelectionOutcome =
    | Selected of rank: int
    | Suppressed of KnowledgeCandidateSuppressionReason

type KnowledgeCandidateSelectionDecision =
    private
        { Candidate: KnowledgeFusedCandidate
          Outcome: KnowledgeCandidateSelectionOutcome }

[<RequireQualifiedAccess>]
module KnowledgeCandidateSelectionDecision =
    let candidate value = value.Candidate
    let outcome value = value.Outcome

type KnowledgeSelectedCandidate =
    private
        { Candidate: KnowledgeFusedCandidate
          Rank: int }

[<RequireQualifiedAccess>]
module KnowledgeSelectedCandidate =
    let candidate value = value.Candidate

    let document value =
        value.Candidate |> KnowledgeFusedCandidate.document

    let rank value = value.Rank

    let score value =
        value.Candidate |> KnowledgeFusedCandidate.score

type KnowledgeRetrievalDiversitySelection =
    private
        { Selector: ComponentVersion
          Config: KnowledgeRetrievalDiversityConfig
          Fusion: KnowledgeReciprocalRankFusion
          Candidates: KnowledgeSelectedCandidate list
          Decisions: KnowledgeCandidateSelectionDecision list }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalDiversitySelection =
    let selector value = value.Selector
    let config value = value.Config
    let fusion value = value.Fusion

    let request value =
        value.Fusion |> KnowledgeReciprocalRankFusion.request

    let candidates value = value.Candidates
    let decisions value = value.Decisions

[<RequireQualifiedAccess>]
module KnowledgeRetrievalDiversitySelector =
    let descriptor = ComponentVersion.create "nao-retrieval-diversity" "1"

    let select config fusion =
        if isNull (box config) then
            nullArg (nameof config)

        if isNull (box fusion) then
            nullArg (nameof fusion)

        let requestMaximum =
            fusion
            |> KnowledgeReciprocalRankFusion.request
            |> KnowledgeRetrievalRequest.limits
            |> KnowledgeRetrievalLimits.maximumCandidates

        let maximumCandidates = KnowledgeRetrievalDiversityConfig.maximumCandidates config

        if maximumCandidates > requestMaximum then
            invalidArg
                (nameof config)
                "Retrieval diversity selected-candidate limit cannot exceed the request candidate limit."

        let selected = ResizeArray<KnowledgeSelectedCandidate>()
        let decisions = ResizeArray<KnowledgeCandidateSelectionDecision>()
        let selectedContent = Dictionary<ContentHash, KnowledgeChunkId>()
        let sourceCounts = Dictionary<KnowledgeSourceId, int>()
        let documentVersionCounts = Dictionary<KnowledgeDocumentVersionId, int>()

        let count key (counts: Dictionary<'Key, int>) =
            match counts.TryGetValue key with
            | true, value -> value
            | false, _ -> 0

        let increment key (counts: Dictionary<'Key, int>) = counts[key] <- count key counts + 1

        let decide candidate =
            let document = KnowledgeFusedCandidate.document candidate
            let sourceId = KnowledgeIndexDocument.sourceId document
            let documentVersionId = KnowledgeIndexDocument.documentVersionId document
            let contentHash = KnowledgeIndexDocument.contentHash document

            if selected.Count >= maximumCandidates then
                KnowledgeCandidateSelectionOutcome.Suppressed KnowledgeCandidateSuppressionReason.CandidateLimit
            elif KnowledgeRetrievalDiversityConfig.suppressExactContentDuplicates config then
                match selectedContent.TryGetValue contentHash with
                | true, selectedChunkId ->
                    KnowledgeCandidateSelectionOutcome.Suppressed(
                        KnowledgeCandidateSuppressionReason.ExactContentDuplicate selectedChunkId
                    )
                | false, _ ->
                    match KnowledgeRetrievalDiversityConfig.maximumPerSource config with
                    | Some limit when count sourceId sourceCounts >= limit ->
                        KnowledgeCandidateSelectionOutcome.Suppressed(
                            KnowledgeCandidateSuppressionReason.SourceLimit sourceId
                        )
                    | _ ->
                        match KnowledgeRetrievalDiversityConfig.maximumPerDocumentVersion config with
                        | Some limit when count documentVersionId documentVersionCounts >= limit ->
                            KnowledgeCandidateSelectionOutcome.Suppressed(
                                KnowledgeCandidateSuppressionReason.DocumentVersionLimit documentVersionId
                            )
                        | _ -> KnowledgeCandidateSelectionOutcome.Selected(selected.Count + 1)
            else
                match KnowledgeRetrievalDiversityConfig.maximumPerSource config with
                | Some limit when count sourceId sourceCounts >= limit ->
                    KnowledgeCandidateSelectionOutcome.Suppressed(
                        KnowledgeCandidateSuppressionReason.SourceLimit sourceId
                    )
                | _ ->
                    match KnowledgeRetrievalDiversityConfig.maximumPerDocumentVersion config with
                    | Some limit when count documentVersionId documentVersionCounts >= limit ->
                        KnowledgeCandidateSelectionOutcome.Suppressed(
                            KnowledgeCandidateSuppressionReason.DocumentVersionLimit documentVersionId
                        )
                    | _ -> KnowledgeCandidateSelectionOutcome.Selected(selected.Count + 1)

        KnowledgeReciprocalRankFusion.candidates fusion
        |> List.iter (fun candidate ->
            let outcome = decide candidate

            match outcome with
            | KnowledgeCandidateSelectionOutcome.Suppressed _ -> ()
            | KnowledgeCandidateSelectionOutcome.Selected rank ->
                let document = KnowledgeFusedCandidate.document candidate
                let contentHash = KnowledgeIndexDocument.contentHash document
                let chunkId = KnowledgeIndexDocument.chunkId document
                selectedContent.TryAdd(contentHash, chunkId) |> ignore
                increment (KnowledgeIndexDocument.sourceId document) sourceCounts
                increment (KnowledgeIndexDocument.documentVersionId document) documentVersionCounts
                selected.Add { Candidate = candidate; Rank = rank }

            decisions.Add
                { Candidate = candidate
                  Outcome = outcome })

        { Selector = descriptor
          Config = config
          Fusion = fusion
          Candidates = List.ofSeq selected
          Decisions = List.ofSeq decisions }
