namespace Nao.Knowledge

open System
open System.Collections.Generic

type KnowledgeParentCandidateEvidence =
    private
        { Expansion: KnowledgeParentExpansionEvidence
          SupportingCandidates: KnowledgeSelectedCandidate list
          Score: float }

[<RequireQualifiedAccess>]
module KnowledgeParentCandidateEvidence =
    let expansion value = value.Expansion
    let supportingCandidates value = value.SupportingCandidates
    let score value = value.Score

[<RequireQualifiedAccess>]
type KnowledgeCandidateStrategyEvidence =
    | ReciprocalRankFusion of KnowledgeSelectedCandidate
    | ParentChildExpansion of KnowledgeParentCandidateEvidence
    | GraphNeighborhoodExpansion of KnowledgeGraphNeighbor

[<RequireQualifiedAccess>]
module KnowledgeCandidateStrategyEvidence =
    let score =
        function
        | KnowledgeCandidateStrategyEvidence.ReciprocalRankFusion candidate ->
            candidate |> KnowledgeSelectedCandidate.score
        | KnowledgeCandidateStrategyEvidence.ParentChildExpansion evidence ->
            evidence |> KnowledgeParentCandidateEvidence.score
        | KnowledgeCandidateStrategyEvidence.GraphNeighborhoodExpansion neighbor ->
            neighbor |> KnowledgeGraphNeighbor.score

type KnowledgeScoredCandidate =
    private
        { Document: KnowledgeIndexDocument
          Rank: int
          Evidence: KnowledgeCandidateStrategyEvidence list }

[<RequireQualifiedAccess>]
module KnowledgeScoredCandidate =
    let document value = value.Document
    let rank value = value.Rank
    let evidence value = value.Evidence

    let primaryScore value =
        value.Evidence |> List.head |> KnowledgeCandidateStrategyEvidence.score

type KnowledgeRetrievalCandidates =
    private
        { Assembler: ComponentVersion
          Selection: KnowledgeRetrievalDiversitySelection
          ParentExpansion: KnowledgeParentChildExpansion option
          GraphExpansion: KnowledgeGraphNeighborhoodExpansionResult option
          Candidates: KnowledgeScoredCandidate list }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalCandidates =
    let assembler value = value.Assembler
    let selection value = value.Selection
    let parentExpansion value = value.ParentExpansion
    let graphExpansion value = value.GraphExpansion

    let request value =
        value.Selection |> KnowledgeRetrievalDiversitySelection.request

    let candidates value = value.Candidates

[<RequireQualifiedAccess>]
module KnowledgeRetrievalCandidateAssembler =
    let descriptor = ComponentVersion.create "nao-retrieval-candidate-assembler" "1"

    type private CandidateAccumulator =
        { Document: KnowledgeIndexDocument
          Evidence: ResizeArray<KnowledgeCandidateStrategyEvidence> }

    let private requireSelection expected actual message =
        if not (obj.ReferenceEquals(expected, actual)) then
            invalidOp message

    let assemble
        (selection: KnowledgeRetrievalDiversitySelection)
        (parentExpansion: KnowledgeParentChildExpansion option)
        (graphExpansion: KnowledgeGraphNeighborhoodExpansionResult option)
        =
        if isNull (box selection) then
            nullArg (nameof selection)

        parentExpansion
        |> Option.iter (fun expansion ->
            if isNull (box expansion) then
                nullArg (nameof parentExpansion)

            requireSelection
                selection
                (KnowledgeParentChildExpansion.selection expansion)
                "Candidate assembly requires parent expansion from the exact same diversity selection.")

        graphExpansion
        |> Option.iter (fun expansion ->
            if isNull (box expansion) then
                nullArg (nameof graphExpansion)

            let graphSelection =
                match expansion with
                | KnowledgeGraphNeighborhoodExpansionResult.NotRequested value -> value
                | KnowledgeGraphNeighborhoodExpansionResult.Expanded value ->
                    KnowledgeGraphNeighborhoodExpansion.selection value

            requireSelection
                selection
                graphSelection
                "Candidate assembly requires graph expansion from the exact same diversity selection.")

        let accumulators = Dictionary<KnowledgeChunkId, CandidateAccumulator>()
        let order = ResizeArray<KnowledgeChunkId>()

        let add document evidence =
            let chunkId = KnowledgeIndexDocument.chunkId document

            match accumulators.TryGetValue chunkId with
            | true, accumulator ->
                if accumulator.Document <> document then
                    invalidOp "The same candidate identity resolved to inconsistent documents."

                accumulator.Evidence.Add evidence
            | false, _ ->
                accumulators.Add(
                    chunkId,
                    { Document = document
                      Evidence = ResizeArray [ evidence ] }
                )

                order.Add chunkId

        let selected = KnowledgeRetrievalDiversitySelection.candidates selection

        selected
        |> List.iter (fun candidate ->
            add
                (KnowledgeSelectedCandidate.document candidate)
                (KnowledgeCandidateStrategyEvidence.ReciprocalRankFusion candidate))

        let selectedById =
            selected
            |> Seq.map (fun candidate ->
                KnowledgeIndexDocument.chunkId (KnowledgeSelectedCandidate.document candidate), candidate)
            |> Map.ofSeq

        parentExpansion
        |> Option.iter (fun expansion ->
            let expandedEvidence =
                KnowledgeParentChildExpansion.evidence expansion
                |> Seq.choose (fun evidence ->
                    match KnowledgeParentExpansionEvidence.outcome evidence with
                    | KnowledgeParentExpansionOutcome.Expanded parent ->
                        Some(KnowledgeIndexDocument.chunkId parent, evidence)
                    | KnowledgeParentExpansionOutcome.Unavailable
                    | KnowledgeParentExpansionOutcome.ParentLimit -> None)
                |> Map.ofSeq

            KnowledgeParentChildExpansion.parents expansion
            |> List.iter (fun parent ->
                let parentId = KnowledgeIndexDocument.chunkId parent

                let evidence =
                    expandedEvidence
                    |> Map.tryFind parentId
                    |> Option.defaultWith (fun () ->
                        invalidOp "Parent expansion omitted evidence for an expanded parent.")

                let supporting =
                    KnowledgeParentExpansionEvidence.childChunkIds evidence
                    |> List.map (fun childId ->
                        selectedById
                        |> Map.tryFind childId
                        |> Option.defaultWith (fun () ->
                            invalidOp "Parent expansion referenced a child outside the selected candidates."))

                if supporting.IsEmpty then
                    invalidOp "Parent expansion evidence requires at least one supporting selected child."

                let parentEvidence =
                    { Expansion = evidence
                      SupportingCandidates = supporting
                      Score = supporting |> List.map KnowledgeSelectedCandidate.score |> List.max }

                add parent (KnowledgeCandidateStrategyEvidence.ParentChildExpansion parentEvidence)))

        graphExpansion
        |> Option.iter (function
            | KnowledgeGraphNeighborhoodExpansionResult.NotRequested _ -> ()
            | KnowledgeGraphNeighborhoodExpansionResult.Expanded expansion ->
                KnowledgeGraphNeighborhoodExpansion.neighbors expansion
                |> List.iter (fun neighbor ->
                    add
                        (KnowledgeGraphNeighbor.document neighbor)
                        (KnowledgeCandidateStrategyEvidence.GraphNeighborhoodExpansion neighbor)))

        let maximumCandidates =
            selection
            |> KnowledgeRetrievalDiversitySelection.request
            |> KnowledgeRetrievalRequest.limits
            |> KnowledgeRetrievalLimits.maximumCandidates

        if order.Count > maximumCandidates then
            invalidOp "Assembled retrieval candidates exceed the request candidate limit."

        let candidates =
            order
            |> Seq.mapi (fun index chunkId ->
                let accumulator = accumulators[chunkId]

                { Document = accumulator.Document
                  Rank = index + 1
                  Evidence = List.ofSeq accumulator.Evidence })
            |> List.ofSeq

        { Assembler = descriptor
          Selection = selection
          ParentExpansion = parentExpansion
          GraphExpansion = graphExpansion
          Candidates = candidates }
