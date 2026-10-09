namespace Nao.Knowledge

type KnowledgeRetrievalFilterEvidence =
    private
        { Boundary: ComponentVersion
          Filter: KnowledgeIndexFilter
          ValidatedCandidates: int }

[<RequireQualifiedAccess>]
module KnowledgeRetrievalFilterEvidence =
    let boundary value = value.Boundary
    let filter value = value.Filter
    let validatedCandidates value = value.ValidatedCandidates

[<RequireQualifiedAccess>]
module internal KnowledgeRetrievalBoundary =
    let descriptor = ComponentVersion.create "nao-retrieval-filter-boundary" "1"

    let private matchesFilter filter document =
        let sourceIds = KnowledgeIndexFilter.sourceIds filter
        let documentVersionIds = KnowledgeIndexFilter.documentVersionIds filter
        let metadataEquals = KnowledgeIndexFilter.metadataEquals filter
        let sourceModifiedAt = KnowledgeIndexDocument.sourceModifiedAt document
        let ingestedAt = KnowledgeIndexDocument.ingestedAt document

        (sourceIds.IsEmpty
         || sourceIds.Contains(KnowledgeIndexDocument.sourceId document))
        && (documentVersionIds.IsEmpty
            || documentVersionIds.Contains(KnowledgeIndexDocument.documentVersionId document))
        && (metadataEquals
            |> Map.forall (fun key expected ->
                KnowledgeIndexDocument.metadata document
                |> Map.tryFind key
                |> Option.exists ((=) expected)))
        && (KnowledgeIndexFilter.sourceModifiedFrom filter
            |> Option.forall (fun lower -> sourceModifiedAt |> Option.exists ((<=) lower)))
        && (KnowledgeIndexFilter.sourceModifiedUntil filter
            |> Option.forall (fun upper -> sourceModifiedAt |> Option.exists ((>=) upper)))
        && (KnowledgeIndexFilter.ingestedFrom filter
            |> Option.forall (fun lower -> ingestedAt >= lower))
        && (KnowledgeIndexFilter.ingestedUntil filter
            |> Option.forall (fun upper -> ingestedAt <= upper))

    let validateDocument request document =
        let authorization = KnowledgeRetrievalRequest.authorization request

        if
            KnowledgeIndexDocument.namespaceName document
            <> KnowledgeRetrievalRequest.namespaceName request
        then
            invalidOp "Retrieval index returned a document outside the requested namespace."

        if
            not (
                KnowledgeIndexAccess.allows
                    authorization
                    (KnowledgeIndexDocument.owner document)
                    (KnowledgeIndexDocument.access document)
            )
        then
            invalidOp "Retrieval index returned a document outside the request authorization boundary."

        if not (matchesFilter (KnowledgeRetrievalRequest.filter request) document) then
            invalidOp "Retrieval index returned a document outside the requested typed filter."

    let evidence request validatedCandidates =
        if validatedCandidates < 0 then
            invalidArg (nameof validatedCandidates) "Validated retrieval candidate count cannot be negative."

        { Boundary = descriptor
          Filter = KnowledgeRetrievalRequest.filter request
          ValidatedCandidates = validatedCandidates }
