namespace Nao.Knowledge

[<RequireQualifiedAccess>]
module internal EnrichmentSupport =
    let validateRequest (request: ChunkEnrichmentRequest) =
        if isNull (box request.Authorization) then
            nullArg (nameof request.Authorization)

        if isNull (box request.Units) || isNull (box request.Chunks) then
            nullArg (nameof request)

        if
            request.Units
            |> List.exists (fun unit -> unit.DocumentVersionId <> request.DocumentVersionId)
        then
            invalidArg (nameof request) "Every content unit must belong to the requested document version."

        if
            request.Chunks
            |> List.exists (fun chunk -> chunk.DocumentVersionId <> request.DocumentVersionId)
        then
            invalidArg (nameof request) "Every chunk must belong to the requested document version."

        let unitIds = request.Units |> List.map _.Id |> Set.ofList

        if unitIds.Count <> request.Units.Length then
            invalidArg (nameof request) "Enrichment input must not contain duplicate content units."

        request.Units
        |> List.pairwise
        |> List.iter (fun (left, right) ->
            if right.Sequence <= left.Sequence then
                invalidArg (nameof request) "Content units must be ordered by increasing sequence.")

        if
            request.Chunks
            |> List.collect _.ContentUnitIds
            |> List.exists (unitIds.Contains >> not)
        then
            invalidArg (nameof request) "A chunk refers to a content unit outside the supplied document version."

        let chunkIds =
            request.Chunks
            |> List.map (fun (chunk: KnowledgeChunk) -> chunk.Id)
            |> Set.ofList

        if chunkIds.Count <> request.Chunks.Length then
            invalidArg (nameof request) "Enrichment input must not contain duplicate chunks."

    let unitsById (request: ChunkEnrichmentRequest) =
        request.Units
        |> List.map (fun (unit: ContentUnit) -> unit.Id, unit)
        |> Map.ofList
