namespace Nao.Knowledge

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Nao.Agents

type KnowledgeParentExpansionConfig = private { MaximumParents: int }

[<RequireQualifiedAccess>]
module KnowledgeParentExpansionConfig =
    let create maximumParents =
        if maximumParents <= 0 then
            invalidArg (nameof maximumParents) "Parent expansion limit must be positive."

        { MaximumParents = maximumParents }

    let maximumParents value = value.MaximumParents

type KnowledgeParentResolutionRequest =
    private
        { Namespace: KnowledgeIndexNamespace
          Filter: KnowledgeIndexFilter
          ParentChunkIds: Set<KnowledgeChunkId>
          MaximumParents: int }

[<RequireQualifiedAccess>]
module KnowledgeParentResolutionRequest =
    let create namespaceName filter parentChunkIds maximumParents =
        let parentChunkIds = Set.ofSeq parentChunkIds

        if parentChunkIds.IsEmpty then
            invalidArg (nameof parentChunkIds) "Parent resolution requires at least one parent identity."

        if maximumParents <= 0 || parentChunkIds.Count > maximumParents then
            invalidArg (nameof maximumParents) "Parent resolution identities exceed the declared limit."

        { Namespace = namespaceName
          Filter = filter
          ParentChunkIds = parentChunkIds
          MaximumParents = maximumParents }

    let namespaceName value = value.Namespace
    let filter value = value.Filter
    let parentChunkIds value = value.ParentChunkIds
    let maximumParents value = value.MaximumParents

type KnowledgeParentResolver =
    { ResolveAsync:
        AuthorizationScope -> KnowledgeParentResolutionRequest -> CancellationToken -> Task<KnowledgeIndexDocument list> }

[<RequireQualifiedAccess>]
type KnowledgeParentExpansionOutcome =
    | Expanded of KnowledgeIndexDocument
    | Unavailable
    | ParentLimit

type KnowledgeParentExpansionEvidence =
    private
        { ParentChunkId: KnowledgeChunkId
          ChildChunkIds: KnowledgeChunkId list
          Outcome: KnowledgeParentExpansionOutcome }

[<RequireQualifiedAccess>]
module KnowledgeParentExpansionEvidence =
    let parentChunkId value = value.ParentChunkId
    let childChunkIds value = value.ChildChunkIds
    let outcome value = value.Outcome

type KnowledgeParentChildExpansion =
    private
        { Expander: ComponentVersion
          Config: KnowledgeParentExpansionConfig
          Selection: KnowledgeRetrievalDiversitySelection
          Parents: KnowledgeIndexDocument list
          Evidence: KnowledgeParentExpansionEvidence list
          Duration: TimeSpan }

[<RequireQualifiedAccess>]
module KnowledgeParentChildExpansion =
    let expander value = value.Expander
    let config value = value.Config
    let selection value = value.Selection

    let request value =
        value.Selection |> KnowledgeRetrievalDiversitySelection.request

    let parents value = value.Parents
    let evidence value = value.Evidence
    let duration value = value.Duration

[<RequireQualifiedAccess>]
module KnowledgeParentChildExpansionExecutor =
    let descriptor = ComponentVersion.create "nao-parent-child-expansion" "1"

    let private validateParent request children parent =
        KnowledgeRetrievalBoundary.validateDocument request parent

        if KnowledgeIndexDocument.role parent <> KnowledgeChunkRole.Parent then
            invalidOp "Parent resolver returned a document that is not a parent chunk."

        if KnowledgeIndexDocument.parentChunkId parent |> Option.isSome then
            invalidOp "Parent resolver returned a parent chunk that references another parent."

        let parentChunkId = KnowledgeIndexDocument.chunkId parent

        children
        |> List.iter (fun child ->
            if KnowledgeIndexDocument.parentChunkId child <> Some parentChunkId then
                invalidOp "Resolved parent does not match its selected child relationship."

            if
                KnowledgeIndexDocument.sourceId child <> KnowledgeIndexDocument.sourceId parent
                || KnowledgeIndexDocument.documentVersionId child
                   <> KnowledgeIndexDocument.documentVersionId parent
            then
                invalidOp "A parent and child chunk must belong to the same source and document version.")

    let expandAsync
        (resolver: KnowledgeParentResolver)
        (config: KnowledgeParentExpansionConfig)
        (selection: KnowledgeRetrievalDiversitySelection)
        (cancellationToken: CancellationToken)
        : Task<KnowledgeParentChildExpansion> =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            if isNull (box config) then
                nullArg (nameof config)

            if isNull (box selection) then
                nullArg (nameof selection)

            let request = KnowledgeRetrievalDiversitySelection.request selection
            let selected = KnowledgeRetrievalDiversitySelection.candidates selection

            let childrenByParent =
                Dictionary<KnowledgeChunkId, ResizeArray<KnowledgeIndexDocument>>()

            let parentOrder = ResizeArray<KnowledgeChunkId>()

            selected
            |> List.iter (fun candidate ->
                let document = KnowledgeSelectedCandidate.document candidate

                match KnowledgeIndexDocument.role document, KnowledgeIndexDocument.parentChunkId document with
                | KnowledgeChunkRole.Parent, Some _ ->
                    invalidOp "A selected parent chunk cannot itself reference a parent."
                | KnowledgeChunkRole.Parent, None
                | KnowledgeChunkRole.Leaf, None -> ()
                | KnowledgeChunkRole.Leaf, Some parentChunkId ->
                    match childrenByParent.TryGetValue parentChunkId with
                    | true, children -> children.Add document
                    | false, _ ->
                        childrenByParent.Add(parentChunkId, ResizeArray [ document ])
                        parentOrder.Add parentChunkId)

            let requestMaximum =
                request
                |> KnowledgeRetrievalRequest.limits
                |> KnowledgeRetrievalLimits.maximumCandidates

            let remainingCandidates = max 0 (requestMaximum - selected.Length)

            let maximumParents =
                min (KnowledgeParentExpansionConfig.maximumParents config) remainingCandidates

            let resolvable = parentOrder |> Seq.truncate maximumParents |> Set.ofSeq

            let limited =
                parentOrder |> Seq.skip (min maximumParents parentOrder.Count) |> Set.ofSeq

            let timer = Stopwatch.StartNew()

            let! resolved =
                if resolvable.IsEmpty then
                    Task.FromResult []
                else
                    task {
                        if isNull (box resolver) || isNull (box resolver.ResolveAsync) then
                            nullArg (nameof resolver)

                        let resolutionRequest =
                            KnowledgeParentResolutionRequest.create
                                (KnowledgeRetrievalRequest.namespaceName request)
                                (KnowledgeRetrievalRequest.filter request)
                                resolvable
                                maximumParents

                        use timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)

                        let invocation =
                            resolver.ResolveAsync
                                (KnowledgeRetrievalRequest.authorization request)
                                resolutionRequest
                                timeout.Token

                        try
                            return!
                                invocation.WaitAsync(
                                    KnowledgeRetrievalBudget.maximumDuration (KnowledgeRetrievalRequest.budget request),
                                    cancellationToken
                                )
                        with :? TimeoutException as error ->
                            timeout.Cancel()

                            return
                                raise (TimeoutException("Parent-child expansion exceeded its duration budget.", error))
                    }

            timer.Stop()

            if isNull (box resolved) then
                invalidOp "Parent resolver returned a null document collection."

            if resolved.Length > maximumParents then
                invalidOp "Parent resolver exceeded the parent expansion limit."

            let resolvedById = Dictionary<KnowledgeChunkId, KnowledgeIndexDocument>()

            resolved
            |> List.iter (fun parent ->
                let parentChunkId = KnowledgeIndexDocument.chunkId parent

                if not (resolvable.Contains parentChunkId) then
                    invalidOp "Parent resolver returned an unrequested parent chunk."

                if not (resolvedById.TryAdd(parentChunkId, parent)) then
                    invalidOp "Parent resolver returned a duplicate parent chunk."

                validateParent request (List.ofSeq childrenByParent[parentChunkId]) parent)

            let evidence =
                parentOrder
                |> Seq.map (fun parentChunkId ->
                    let outcome =
                        if limited.Contains parentChunkId then
                            KnowledgeParentExpansionOutcome.ParentLimit
                        else
                            match resolvedById.TryGetValue parentChunkId with
                            | true, parent -> KnowledgeParentExpansionOutcome.Expanded parent
                            | false, _ -> KnowledgeParentExpansionOutcome.Unavailable

                    { ParentChunkId = parentChunkId
                      ChildChunkIds =
                        childrenByParent[parentChunkId]
                        |> Seq.map KnowledgeIndexDocument.chunkId
                        |> List.ofSeq
                      Outcome = outcome })
                |> List.ofSeq

            let parents =
                parentOrder
                |> Seq.choose (fun parentChunkId ->
                    match resolvedById.TryGetValue parentChunkId with
                    | true, parent -> Some parent
                    | false, _ -> None)
                |> List.ofSeq

            return
                { Expander = descriptor
                  Config = config
                  Selection = selection
                  Parents = parents
                  Evidence = evidence
                  Duration = timer.Elapsed }
        }
