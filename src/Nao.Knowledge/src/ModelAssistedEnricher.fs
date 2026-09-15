namespace Nao.Knowledge

open System
open System.Globalization

[<RequireQualifiedAccess>]
module ModelAssistedEnricher =
    let private enricherComponent =
        ComponentVersion.create "nao.enricher.model-assisted" "1"

    let private invariantFloat (value: float) =
        value.ToString("R", CultureInfo.InvariantCulture)

    let private configurationHash (config: ModelAssistedEnricherConfig) =
        String.concat
            ";"
            [ $"model={config.Model.Descriptor.Model.ComponentId}@{config.Model.Descriptor.Model.Version}"
              $"model-configuration={ContentHash.value config.Model.Descriptor.ConfigurationHash}"
              $"minimum-confidence={invariantFloat (Confidence.value config.MinimumConfidence)}"
              $"maximum-chunks={config.MaximumChunksPerRequest.ToString(CultureInfo.InvariantCulture)}"
              $"maximum-enrichments={config.MaximumEnrichmentsPerChunk.ToString(CultureInfo.InvariantCulture)}"
              $"maximum-value-characters={config.MaximumValueCharacters.ToString(CultureInfo.InvariantCulture)}"
              $"maximum-evidence={config.MaximumEvidenceItems.ToString(CultureInfo.InvariantCulture)}" ]
        |> ContentHash.computeText

    let private modelInput (unitsById: Map<ContentUnitId, ContentUnit>) (chunk: KnowledgeChunk) =
        { ChunkId = chunk.Id
          Units =
            chunk.ContentUnitIds
            |> List.map (fun unitId ->
                let unit = Map.find unitId unitsById

                { Id = unit.Id; Text = unit.Text }) }

    let private validateOutputs (requestedIds: Set<KnowledgeChunkId>) (outputs: ModelEnrichmentOutput list) =
        if isNull (box outputs) then
            invalidOp "Knowledge enrichment model returned a null output collection."

        let outputIds =
            outputs |> List.map (fun (output: ModelEnrichmentOutput) -> output.ChunkId)

        if
            outputIds.Length <> requestedIds.Count
            || (outputIds |> Set.ofList) <> requestedIds
        then
            invalidOp "Knowledge enrichment model output does not match the requested chunks."

        outputs
        |> List.iter (fun (output: ModelEnrichmentOutput) ->
            if isNull (box output) || isNull (box output.Enrichments) then
                invalidOp "Knowledge enrichment model returned a null enrichment collection.")

    let private evidenceFor
        (unitsById: Map<ContentUnitId, ContentUnit>)
        (chunkUnitIds: Set<ContentUnitId>)
        maximumEvidence
        (proposal: ProposedKnowledgeEnrichment)
        =
        if isNull (box proposal.Evidence) || List.isEmpty proposal.Evidence then
            invalidOp "Model-generated enrichment requires source evidence."

        if proposal.Evidence.Length > maximumEvidence then
            invalidOp "Model-generated enrichment exceeds the configured evidence limit."

        let evidenceIds =
            proposal.Evidence
            |> List.map (fun (evidence: ProposedEnrichmentEvidence) -> evidence.ContentUnitId)
            |> Set.ofList

        if evidenceIds.Count <> proposal.Evidence.Length then
            invalidOp "Model-generated enrichment contains duplicate source evidence."

        proposal.Evidence
        |> List.map (fun (evidence: ProposedEnrichmentEvidence) ->
            if isNull (box evidence) then
                invalidOp "Model-generated enrichment contains null source evidence."

            match unitsById |> Map.tryFind evidence.ContentUnitId with
            | None -> invalidOp "Model-generated enrichment refers to an unknown content unit."
            | Some unit ->
                if not (chunkUnitIds |> Set.contains unit.Id) then
                    invalidOp "Model-generated enrichment evidence falls outside its chunk."

                if
                    String.IsNullOrWhiteSpace evidence.Quote
                    || evidence.Quote <> evidence.Quote.Trim()
                    || not (unit.Text.Contains(evidence.Quote, StringComparison.Ordinal))
                then
                    invalidOp "Model-generated enrichment quote is not an exact content-unit segment."

                KnowledgeEnrichmentEvidence.create unit.Id unit.Location evidence.Quote)

    let private enrichmentsFor
        (config: ModelAssistedEnricherConfig)
        (descriptor: ChunkEnricherDescriptor)
        generatedAt
        unitsById
        (chunk: KnowledgeChunk)
        (output: ModelEnrichmentOutput)
        =
        if output.Enrichments.Length > config.MaximumEnrichmentsPerChunk then
            invalidOp "Knowledge enrichment model exceeded the configured enrichment limit."

        let chunkUnitIds = chunk.ContentUnitIds |> Set.ofList

        let enrichments =
            output.Enrichments
            |> List.choose (fun (proposal: ProposedKnowledgeEnrichment) ->
                if isNull (box proposal) then
                    invalidOp "Knowledge enrichment model returned a null enrichment."

                if
                    not (Double.IsFinite proposal.Confidence)
                    || proposal.Confidence < 0.0
                    || proposal.Confidence > 1.0
                then
                    invalidOp "Model-generated enrichment confidence must be finite and between zero and one."

                if
                    isNull proposal.Value
                    || proposal.Value.Length > config.MaximumValueCharacters
                    || String.IsNullOrWhiteSpace proposal.Value
                    || proposal.Value <> proposal.Value.Trim()
                then
                    invalidOp "Model-generated enrichment value is invalid or exceeds its configured limit."

                let evidence =
                    evidenceFor unitsById chunkUnitIds config.MaximumEvidenceItems proposal

                let confidence = Confidence.create proposal.Confidence

                if confidence < config.MinimumConfidence then
                    None
                else
                    Some(
                        KnowledgeEnrichment.create
                            chunk.Id
                            proposal.Kind
                            proposal.Value
                            confidence
                            evidence
                            config.Model.Descriptor.Model
                            config.Model.Descriptor.ConfigurationHash
                            generatedAt
                    ))

        let ids =
            enrichments
            |> List.map (fun (enrichment: KnowledgeEnrichment) -> enrichment.Id)
            |> Set.ofList

        if ids.Count <> enrichments.Length then
            invalidOp "Knowledge enrichment model returned duplicate enrichments."

        let outputHash =
            enrichments
            |> List.map (KnowledgeEnrichmentId.value << _.Id)
            |> String.concat "\u001f"
            |> ContentHash.computeText

        let derivation =
            DerivationStep.create
                DerivationStage.Enrichment
                descriptor.Component
                [ chunk.ContentHash ]
                outputHash
                generatedAt
                (Map.ofList
                    [ "enricher.configuration", ContentHash.value descriptor.ConfigurationHash
                      "model.component", config.Model.Descriptor.Model.ComponentId
                      "model.version", config.Model.Descriptor.Model.Version
                      "model.configuration", ContentHash.value config.Model.Descriptor.ConfigurationHash
                      "enrichment.count", enrichments.Length.ToString(CultureInfo.InvariantCulture) ])

        { chunk with
            Enrichments = chunk.Enrichments @ enrichments
            Derivation = chunk.Derivation @ [ derivation ] }

    let create (config: ModelAssistedEnricherConfig) : KnowledgeChunkEnricher =
        if isNull (box config.Model) then
            nullArg (nameof config.Model)

        if
            config.MaximumChunksPerRequest <= 0
            || config.MaximumEnrichmentsPerChunk <= 0
            || config.MaximumValueCharacters <= 0
            || config.MaximumEvidenceItems <= 0
        then
            invalidArg (nameof config) "Model enrichment limits must be positive."

        let descriptor =
            { Component = enricherComponent
              ConfigurationHash = configurationHash config
              Deterministic = config.Model.Descriptor.Deterministic }

        { Descriptor = descriptor
          EnrichAsync =
            fun request cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()
                    EnrichmentSupport.validateRequest request

                    if request.Chunks.Length > config.MaximumChunksPerRequest then
                        invalidArg (nameof request) "Chunk count exceeds the configured model request limit."

                    let priorByChunk =
                        request.Chunks
                        |> List.map (fun chunk ->
                            chunk,
                            chunk.Derivation
                            |> List.exists (fun step ->
                                step.Stage = DerivationStage.Enrichment
                                && step.Component = descriptor.Component
                                && step.Metadata
                                   |> Map.tryFind "enricher.configuration"
                                   |> Option.contains (ContentHash.value descriptor.ConfigurationHash)))

                    if priorByChunk |> List.forall snd then
                        return
                            { Chunks = request.Chunks
                              EnrichedChunks = request.Chunks.Length }
                    elif priorByChunk |> List.exists snd then
                        return invalidOp "Model enrichment request contains partially enriched chunks."
                    else
                        let hasUntrackedModelOutput =
                            request.Chunks
                            |> List.collect _.Enrichments
                            |> List.exists (fun (enrichment: KnowledgeEnrichment) ->
                                enrichment.Generator = config.Model.Descriptor.Model
                                && enrichment.GeneratorConfigurationHash = config.Model.Descriptor.ConfigurationHash)

                        if hasUntrackedModelOutput then
                            invalidOp "Chunk contains model enrichments without matching derivation lineage."

                        let unitsById = EnrichmentSupport.unitsById request

                        let inputs = request.Chunks |> List.map (modelInput unitsById)

                        let! outputs = config.Model.EnrichAsync request.Authorization inputs cancellationToken

                        cancellationToken.ThrowIfCancellationRequested()
                        let requestedIds = request.Chunks |> List.map _.Id |> Set.ofList
                        validateOutputs requestedIds outputs

                        let outputsById =
                            outputs |> List.map (fun output -> output.ChunkId, output) |> Map.ofList

                        let chunks =
                            request.Chunks
                            |> List.map (fun chunk ->
                                enrichmentsFor
                                    config
                                    descriptor
                                    request.EnrichedAt
                                    unitsById
                                    chunk
                                    outputsById[chunk.Id])

                        return
                            { Chunks = chunks
                              EnrichedChunks = chunks.Length }
                } }
