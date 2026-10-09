namespace Nao.Knowledge

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Nao.Agents

type KnowledgeGraphExpansionConfig =
    private
        { EdgeKinds: Set<string>
          MaximumDepth: int
          MaximumNeighbors: int }

[<RequireQualifiedAccess>]
module KnowledgeGraphExpansionConfig =
    let create edgeKinds maximumDepth maximumNeighbors =
        let edgeKinds = Set.ofSeq edgeKinds

        if
            edgeKinds
            |> Set.exists (fun kind -> String.IsNullOrWhiteSpace kind || kind <> kind.Trim())
        then
            invalidArg (nameof edgeKinds) "Graph expansion edge kinds must be non-blank and trimmed."

        if maximumDepth <= 0 then
            invalidArg (nameof maximumDepth) "Graph expansion depth must be positive."

        if maximumNeighbors < 0 then
            invalidArg (nameof maximumNeighbors) "Graph expansion neighbor limit cannot be negative."

        { EdgeKinds = edgeKinds
          MaximumDepth = maximumDepth
          MaximumNeighbors = maximumNeighbors }

    let edgeKinds value = value.EdgeKinds
    let maximumDepth value = value.MaximumDepth
    let maximumNeighbors value = value.MaximumNeighbors

type KnowledgeGraphDocumentResolutionRequest =
    private
        { Namespace: KnowledgeIndexNamespace
          Filter: KnowledgeIndexFilter
          ChunkIds: Set<KnowledgeChunkId>
          MaximumDocuments: int }

[<RequireQualifiedAccess>]
module KnowledgeGraphDocumentResolutionRequest =
    let create namespaceName filter chunkIds maximumDocuments =
        let chunkIds = Set.ofSeq chunkIds

        if chunkIds.IsEmpty then
            invalidArg (nameof chunkIds) "Graph document resolution requires at least one chunk identity."

        if maximumDocuments <= 0 || chunkIds.Count > maximumDocuments then
            invalidArg (nameof maximumDocuments) "Graph document identities exceed the declared limit."

        { Namespace = namespaceName
          Filter = filter
          ChunkIds = chunkIds
          MaximumDocuments = maximumDocuments }

    let namespaceName value = value.Namespace
    let filter value = value.Filter
    let chunkIds value = value.ChunkIds
    let maximumDocuments value = value.MaximumDocuments

type KnowledgeGraphDocumentResolver =
    { ResolveAsync:
        AuthorizationScope
            -> KnowledgeGraphDocumentResolutionRequest
            -> CancellationToken
            -> Task<KnowledgeIndexDocument list> }

[<RequireQualifiedAccess>]
type KnowledgeGraphExpansionOutcome =
    | Expanded of KnowledgeIndexDocument * rank: int
    | AlreadySelected
    | Unavailable

type KnowledgeGraphExpansionEvidence =
    private
        { Hit: GraphIndexHit
          Outcome: KnowledgeGraphExpansionOutcome }

[<RequireQualifiedAccess>]
module KnowledgeGraphExpansionEvidence =
    let hit value = value.Hit
    let outcome value = value.Outcome

type KnowledgeGraphNeighbor =
    private
        { Document: KnowledgeIndexDocument
          Hit: GraphIndexHit
          Rank: int }

[<RequireQualifiedAccess>]
module KnowledgeGraphNeighbor =
    let document value = value.Document
    let hit value = value.Hit
    let rank value = value.Rank
    let score value = value.Hit |> GraphIndexHit.score
    let depth value = value.Hit |> GraphIndexHit.depth
    let path value = value.Hit |> GraphIndexHit.path

type KnowledgeGraphNeighborhoodExpansion =
    private
        { Expander: ComponentVersion
          Config: KnowledgeGraphExpansionConfig
          Selection: KnowledgeRetrievalDiversitySelection
          Neighbors: KnowledgeGraphNeighbor list
          Evidence: KnowledgeGraphExpansionEvidence list
          Duration: TimeSpan }

[<RequireQualifiedAccess>]
module KnowledgeGraphNeighborhoodExpansion =
    let expander value = value.Expander
    let config value = value.Config
    let selection value = value.Selection

    let request value =
        value.Selection |> KnowledgeRetrievalDiversitySelection.request

    let neighbors value = value.Neighbors
    let evidence value = value.Evidence
    let duration value = value.Duration

[<RequireQualifiedAccess>]
type KnowledgeGraphNeighborhoodExpansionResult =
    | NotRequested of KnowledgeRetrievalDiversitySelection
    | Expanded of KnowledgeGraphNeighborhoodExpansion

[<RequireQualifiedAccess>]
module KnowledgeGraphNeighborhoodExpansionExecutor =
    let descriptor = ComponentVersion.create "nao-graph-neighborhood-expansion" "1"

    let private validateHit maximumDepth (selectedChunkIds: Set<KnowledgeChunkId>) hit =
        let path = GraphIndexHit.path hit

        if GraphIndexHit.depth hit > maximumDepth then
            invalidOp "Graph index returned a hit beyond the requested depth."

        if path |> List.head |> selectedChunkIds.Contains |> not then
            invalidOp "Graph index returned a path that does not begin at a selected candidate."

        if (path |> Set.ofList).Count <> path.Length then
            invalidOp "Graph index returned a cyclic path."

    let expandAsync
        (index: KnowledgeGraphIndex)
        (resolver: KnowledgeGraphDocumentResolver)
        (config: KnowledgeGraphExpansionConfig)
        (selection: KnowledgeRetrievalDiversitySelection)
        (cancellationToken: CancellationToken)
        : Task<KnowledgeGraphNeighborhoodExpansionResult> =
        cancellationToken.ThrowIfCancellationRequested()

        if isNull (box config) then
            nullArg (nameof config)

        if isNull (box selection) then
            nullArg (nameof selection)

        let request = KnowledgeRetrievalDiversitySelection.request selection

        match KnowledgeRetrievalRequest.strategy request with
        | KnowledgeRetrievalStrategy.Lexical
        | KnowledgeRetrievalStrategy.Vector
        | KnowledgeRetrievalStrategy.Hybrid ->
            Task.FromResult(KnowledgeGraphNeighborhoodExpansionResult.NotRequested selection)
        | KnowledgeRetrievalStrategy.HybridWithGraph ->
            task {
                let selected = KnowledgeRetrievalDiversitySelection.candidates selection

                let selectedChunkIds =
                    selected
                    |> Seq.map (KnowledgeSelectedCandidate.document >> KnowledgeIndexDocument.chunkId)
                    |> Set.ofSeq

                let requestMaximum =
                    request
                    |> KnowledgeRetrievalRequest.limits
                    |> KnowledgeRetrievalLimits.maximumCandidates

                let maximumNeighbors =
                    min
                        (KnowledgeGraphExpansionConfig.maximumNeighbors config)
                        (max 0 (requestMaximum - selected.Length))

                if selectedChunkIds.IsEmpty || maximumNeighbors = 0 then
                    return
                        KnowledgeGraphNeighborhoodExpansionResult.Expanded
                            { Expander = descriptor
                              Config = config
                              Selection = selection
                              Neighbors = []
                              Evidence = []
                              Duration = TimeSpan.Zero }
                else
                    if isNull (box index) || isNull (box index.QueryAsync) then
                        nullArg (nameof index)

                    if isNull (box resolver) || isNull (box resolver.ResolveAsync) then
                        nullArg (nameof resolver)

                    use timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    let timer = Stopwatch.StartNew()

                    let work =
                        task {
                            let graphQuery =
                                GraphIndexQuery.create
                                    (KnowledgeRetrievalRequest.namespaceName request)
                                    selectedChunkIds
                                    (KnowledgeGraphExpansionConfig.edgeKinds config)
                                    (KnowledgeGraphExpansionConfig.maximumDepth config)
                                    maximumNeighbors

                            let! hits =
                                index.QueryAsync
                                    (KnowledgeRetrievalRequest.authorization request)
                                    graphQuery
                                    timeout.Token

                            if isNull (box hits) then
                                invalidOp "Graph index returned a null hit collection."

                            if hits.Length > maximumNeighbors then
                                invalidOp "Graph index exceeded the graph neighbor limit."

                            let seenHits = HashSet<KnowledgeChunkId>()

                            hits
                            |> List.iter (fun hit ->
                                validateHit (KnowledgeGraphExpansionConfig.maximumDepth config) selectedChunkIds hit

                                if not (seenHits.Add(GraphIndexHit.chunkId hit)) then
                                    invalidOp "Graph index returned the same neighbor more than once.")

                            let unresolvedIds =
                                hits
                                |> Seq.map GraphIndexHit.chunkId
                                |> Seq.filter (selectedChunkIds.Contains >> not)
                                |> Set.ofSeq

                            let! documents =
                                if unresolvedIds.IsEmpty then
                                    Task.FromResult []
                                else
                                    resolver.ResolveAsync
                                        (KnowledgeRetrievalRequest.authorization request)
                                        (KnowledgeGraphDocumentResolutionRequest.create
                                            (KnowledgeRetrievalRequest.namespaceName request)
                                            (KnowledgeRetrievalRequest.filter request)
                                            unresolvedIds
                                            maximumNeighbors)
                                        timeout.Token

                            if isNull (box documents) then
                                invalidOp "Graph document resolver returned a null document collection."

                            if documents.Length > unresolvedIds.Count then
                                invalidOp "Graph document resolver exceeded the requested document limit."

                            let resolvedById = Dictionary<KnowledgeChunkId, KnowledgeIndexDocument>()

                            documents
                            |> List.iter (fun document ->
                                KnowledgeRetrievalBoundary.validateDocument request document
                                let chunkId = KnowledgeIndexDocument.chunkId document

                                if not (unresolvedIds.Contains chunkId) then
                                    invalidOp "Graph document resolver returned an unrequested chunk."

                                if not (resolvedById.TryAdd(chunkId, document)) then
                                    invalidOp "Graph document resolver returned a duplicate chunk.")

                            let neighbors = ResizeArray<KnowledgeGraphNeighbor>()

                            let evidence =
                                hits
                                |> List.map (fun hit ->
                                    let chunkId = GraphIndexHit.chunkId hit

                                    let outcome =
                                        if selectedChunkIds.Contains chunkId then
                                            KnowledgeGraphExpansionOutcome.AlreadySelected
                                        else
                                            match resolvedById.TryGetValue chunkId with
                                            | true, document ->
                                                let rank = neighbors.Count + 1

                                                neighbors.Add
                                                    { Document = document
                                                      Hit = hit
                                                      Rank = rank }

                                                KnowledgeGraphExpansionOutcome.Expanded(document, rank)
                                            | false, _ -> KnowledgeGraphExpansionOutcome.Unavailable

                                    { Hit = hit; Outcome = outcome })

                            return List.ofSeq neighbors, evidence
                        }

                    let! neighbors, evidence =
                        task {
                            try
                                return!
                                    work.WaitAsync(
                                        KnowledgeRetrievalBudget.maximumDuration (
                                            KnowledgeRetrievalRequest.budget request
                                        ),
                                        cancellationToken
                                    )
                            with :? TimeoutException as error ->
                                timeout.Cancel()

                                return
                                    raise (
                                        TimeoutException(
                                            "Graph-neighborhood expansion exceeded its duration budget.",
                                            error
                                        )
                                    )
                        }

                    timer.Stop()

                    return
                        KnowledgeGraphNeighborhoodExpansionResult.Expanded
                            { Expander = descriptor
                              Config = config
                              Selection = selection
                              Neighbors = neighbors
                              Evidence = evidence
                              Duration = timer.Elapsed }
            }
