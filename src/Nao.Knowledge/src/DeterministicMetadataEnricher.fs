namespace Nao.Knowledge

open System
open System.Globalization

[<RequireQualifiedAccess>]
module DeterministicMetadataEnricher =
    let private enricherComponent = ComponentVersion.create "nao.enricher.metadata" "1"

    let private configurationHash =
        ContentHash.computeText
            "derived.content-kinds;derived.heading-path;derived.links;derived.source-pages;derived.source-paths;derived.unit-count"

    let private descriptor =
        { Component = enricherComponent
          ConfigurationHash = configurationHash
          Deterministic = true }

    let private distinct values =
        let _, result =
            values
            |> List.fold
                (fun (seen, collected) value ->
                    if Set.contains value seen then
                        seen, collected
                    else
                        Set.add value seen, value :: collected)
                (Set.empty, [])

        List.rev result

    let private kindName =
        function
        | ContentUnitKind.Heading -> "heading"
        | ContentUnitKind.Paragraph -> "paragraph"
        | ContentUnitKind.Table -> "table"
        | ContentUnitKind.Code -> "code"
        | ContentUnitKind.List -> "list"
        | ContentUnitKind.Link -> "link"
        | ContentUnitKind.Other name -> "other:" + name

    let private headingLevel (unit: ContentUnit) =
        unit.Metadata
        |> Map.tryFind "heading.level"
        |> Option.bind (fun value ->
            match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, level when level >= 1 && level <= 6 -> Some level
            | _ -> None)
        |> Option.defaultValue 1

    let private headingContexts (units: ContentUnit list) =
        let _, contexts =
            units
            |> List.fold
                (fun (headings, contexts) unit ->
                    let nextHeadings =
                        if unit.Kind = ContentUnitKind.Heading then
                            let level = headingLevel unit

                            (headings |> List.filter (fun (existingLevel, _) -> existingLevel < level))
                            @ [ level, unit.Text ]
                        else
                            headings

                    nextHeadings, contexts |> Map.add unit.Id (nextHeadings |> List.map snd))
                ([], Map.empty)

        contexts

    let private splitValues (value: string) =
        value.Split('\u001f', StringSplitOptions.RemoveEmptyEntries) |> Array.toList

    let private extractedMetadata headingContext (units: ContentUnit list) =
        let addJoined (key: string) (values: string list) (metadata: Map<string, string>) =
            match values |> distinct with
            | [] -> metadata
            | values -> metadata |> Map.add key (String.concat "\u001f" values)

        let pages =
            units
            |> List.choose (_.Location.Page)
            |> List.distinct
            |> List.sort
            |> List.map (fun value -> value.ToString(CultureInfo.InvariantCulture))

        let paths = units |> List.choose (_.Location.Path)

        let links =
            units
            |> List.collect (fun unit ->
                unit.Metadata
                |> Map.tryFind "links"
                |> Option.map splitValues
                |> Option.defaultValue [])

        Map.ofList
            [ "derived.content-kinds", units |> List.map (_.Kind >> kindName) |> distinct |> String.concat "\u001f"
              "derived.unit-count", units.Length.ToString(CultureInfo.InvariantCulture) ]
        |> addJoined "derived.heading-path" headingContext
        |> addJoined "derived.links" links
        |> addJoined "derived.source-pages" pages
        |> addJoined "derived.source-paths" paths

    let private enrichChunk
        (request: ChunkEnrichmentRequest)
        (unitsById: Map<ContentUnitId, ContentUnit>)
        (headingContextByUnit: Map<ContentUnitId, string list>)
        (chunk: KnowledgeChunk)
        =
        let units =
            chunk.ContentUnitIds |> List.map (fun unitId -> Map.find unitId unitsById)

        let headingContext =
            units
            |> List.minBy _.Sequence
            |> _.Id
            |> fun unitId -> Map.find unitId headingContextByUnit

        let extracted = extractedMetadata headingContext units

        let reservedMetadata =
            chunk.Metadata
            |> Map.filter (fun key _ -> key.StartsWith("derived.", StringComparison.Ordinal))

        let priorExtraction =
            chunk.Derivation
            |> List.exists (fun step ->
                step.Stage = DerivationStage.Enrichment
                && step.Component = descriptor.Component
                && step.Metadata
                   |> Map.tryFind "enricher.configuration"
                   |> Option.contains (ContentHash.value descriptor.ConfigurationHash))

        if priorExtraction then
            if reservedMetadata = extracted then
                chunk
            else
                invalidOp "Existing deterministic metadata does not match its recorded enrichment lineage."
        elif not reservedMetadata.IsEmpty then
            invalidOp "Chunk metadata uses the reserved deterministic-extraction namespace."
        else
            let derivation =
                DerivationStep.create
                    DerivationStage.Enrichment
                    descriptor.Component
                    (chunk.ContentHash :: (units |> List.map _.ContentHash))
                    chunk.ContentHash
                    request.EnrichedAt
                    (Map.ofList [ "enricher.configuration", ContentHash.value descriptor.ConfigurationHash ])

            { chunk with
                Metadata =
                    extracted
                    |> Map.fold (fun metadata key value -> metadata |> Map.add key value) chunk.Metadata
                Derivation = chunk.Derivation @ [ derivation ] }

    let create () =
        { Descriptor = descriptor
          EnrichAsync =
            fun request cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()
                    EnrichmentSupport.validateRequest request

                    let unitsById = EnrichmentSupport.unitsById request

                    let headingContextByUnit = headingContexts request.Units

                    let chunks =
                        request.Chunks
                        |> List.map (fun chunk ->
                            cancellationToken.ThrowIfCancellationRequested()
                            enrichChunk request unitsById headingContextByUnit chunk)

                    return
                        { Chunks = chunks
                          EnrichedChunks = chunks.Length }
                } }
