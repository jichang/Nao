namespace Nao.Knowledge

open System
open System.Globalization

[<RequireQualifiedAccess>]
module SemanticChunker =
    let private chunkerComponent = ComponentVersion.create "nao.chunker.semantic" "1"

    let private invariantFloat (value: float) =
        value.ToString("R", CultureInfo.InvariantCulture)

    let private configurationHash (config: SemanticChunkerConfig) =
        String.concat
            ";"
            [ $"boundary-similarity={invariantFloat config.BoundarySimilarityThreshold}"
              $"maximum-units={config.MaximumUnitsPerChunk.ToString(CultureInfo.InvariantCulture)}"
              $"model={config.Model.Descriptor.Model.ComponentId}@{config.Model.Descriptor.Model.Version}"
              $"model-configuration={ContentHash.value config.Model.Descriptor.ConfigurationHash}"
              $"dimensions={config.Model.Descriptor.Dimensions.ToString(CultureInfo.InvariantCulture)}" ]
        |> ContentHash.computeText

    let private validateEmbeddings dimensions expectedCount (embeddings: float array list) =
        if isNull (box embeddings) || embeddings.Length <> expectedCount then
            invalidOp "Semantic boundary model returned an unexpected number of embeddings."

        embeddings
        |> List.iter (fun embedding ->
            if
                isNull embedding
                || embedding.Length <> dimensions
                || embedding |> Array.exists (Double.IsFinite >> not)
            then
                invalidOp "Semantic boundary model returned an invalid embedding.")

    let private cosineSimilarity (left: float array) (right: float array) =
        let mutable dot = 0.0
        let mutable leftNorm = 0.0
        let mutable rightNorm = 0.0

        for index in 0 .. left.Length - 1 do
            dot <- dot + left[index] * right[index]
            leftNorm <- leftNorm + left[index] * left[index]
            rightNorm <- rightNorm + right[index] * right[index]

        if leftNorm = 0.0 || rightNorm = 0.0 then
            0.0
        else
            dot / (sqrt leftNorm * sqrt rightNorm)

    let private groupUnits threshold maximumUnits (units: ContentUnit list) (embeddings: float array list) =
        let pairs = List.zip units embeddings

        let finish (current: ContentUnit list) (groups: ContentUnit list list) =
            match List.rev current with
            | [] -> groups
            | group -> group :: groups

        let rec loop
            (current: ContentUnit list)
            (previousEmbedding: float array option)
            (groups: ContentUnit list list)
            (remaining: (ContentUnit * float array) list)
            =
            match remaining with
            | [] -> finish current groups |> List.rev
            | (unit, embedding) :: tail ->
                let boundary =
                    match previousEmbedding with
                    | None -> false
                    | Some previous ->
                        current.Length >= maximumUnits
                        || cosineSimilarity previous embedding < threshold

                if boundary then
                    loop [ unit ] (Some embedding) (finish current groups) tail
                else
                    loop (unit :: current) (Some embedding) groups tail

        loop [] None [] pairs

    let create (config: SemanticChunkerConfig) =
        if
            not (Double.IsFinite config.BoundarySimilarityThreshold)
            || config.BoundarySimilarityThreshold < -1.0
            || config.BoundarySimilarityThreshold > 1.0
        then
            invalidArg
                (nameof config.BoundarySimilarityThreshold)
                "Boundary similarity must be finite and between negative one and one."

        if config.MaximumUnitsPerChunk <= 0 then
            invalidArg (nameof config.MaximumUnitsPerChunk) "Maximum units per chunk must be positive."

        if isNull (box config.Model) then
            nullArg (nameof config.Model)

        if config.Model.Descriptor.Dimensions <= 0 then
            invalidArg (nameof config.Model.Descriptor.Dimensions) "Model dimensions must be positive."

        let descriptor =
            { ContractVersion = ChunkerContractVersion.current
              Component = chunkerComponent
              ConfigurationHash = configurationHash config
              Deterministic = config.Model.Descriptor.Deterministic }

        { Descriptor = descriptor
          ChunkAsync =
            fun request cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()
                    ChunkingSupport.validateRequest request

                    if List.isEmpty request.Units then
                        return
                            { Chunks = []
                              Metrics = ChunkingMeasurement.measure request.Units [] None 0 0L }
                    else
                        let! embeddings =
                            config.Model.EmbedAsync
                                request.Authorization
                                (request.Units |> List.map _.Text)
                                cancellationToken

                        cancellationToken.ThrowIfCancellationRequested()
                        validateEmbeddings config.Model.Descriptor.Dimensions request.Units.Length embeddings

                        let groups =
                            groupUnits
                                config.BoundarySimilarityThreshold
                                config.MaximumUnitsPerChunk
                                request.Units
                                embeddings

                        let chunks =
                            groups
                            |> List.mapi (fun sequence units ->
                                let metadata =
                                    Map.ofList
                                        [ ("semantic.model", config.Model.Descriptor.Model.ComponentId)
                                          ("semantic.model-version", config.Model.Descriptor.Model.Version)
                                          ("semantic.model-configuration",
                                           ContentHash.value config.Model.Descriptor.ConfigurationHash)
                                          ("semantic.unit-count", string units.Length)
                                          ("semantic.boundary-threshold",
                                           invariantFloat config.BoundarySimilarityThreshold) ]

                                ChunkingSupport.createChunk
                                    descriptor
                                    request
                                    sequence
                                    KnowledgeChunkRole.Leaf
                                    None
                                    units
                                    (units |> List.map _.Text |> String.concat "\n\n")
                                    metadata)

                        return
                            { Chunks = chunks
                              Metrics = ChunkingMeasurement.measure request.Units chunks None 0 0L }
                } }
