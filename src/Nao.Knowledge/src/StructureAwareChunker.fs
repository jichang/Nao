namespace Nao.Knowledge

open System
open System.Globalization
open System.Threading.Tasks

type StructureAwareChunkerConfig = { MaximumParentCharacters: int }

[<RequireQualifiedAccess>]
module StructureAwareChunker =
    let private chunkerComponent = ComponentVersion.create "nao.chunker.structure" "1"

    let private configurationHash config =
        $"maximum-parent-characters={config.MaximumParentCharacters.ToString(CultureInfo.InvariantCulture)}"
        |> ContentHash.computeText

    let private splitSections (units: ContentUnit list) =
        let finish (current: ContentUnit list) (sections: ContentUnit list list) =
            match List.rev current with
            | [] -> sections
            | section -> section :: sections

        let rec loop (current: ContentUnit list) (sections: ContentUnit list list) (remaining: ContentUnit list) =
            match remaining with
            | [] -> finish current sections |> List.rev
            | unit :: tail when unit.Kind = ContentUnitKind.Heading && not (List.isEmpty current) ->
                loop [ unit ] (finish current sections) tail
            | unit :: tail -> loop (unit :: current) sections tail

        loop [] [] units

    let private splitByMaximum maximumCharacters (units: ContentUnit list) =
        let unitCost (unit: ContentUnit) = unit.Text.Length

        let finish (current: ContentUnit list) (groups: ContentUnit list list) =
            match List.rev current with
            | [] -> groups
            | group -> group :: groups

        let rec loop
            (current: ContentUnit list)
            currentLength
            (groups: ContentUnit list list)
            (remaining: ContentUnit list)
            =
            match remaining with
            | [] -> finish current groups |> List.rev
            | unit :: tail ->
                let separatorLength = if List.isEmpty current then 0 else 2
                let nextLength = currentLength + separatorLength + unitCost unit

                if not (List.isEmpty current) && nextLength > maximumCharacters then
                    loop [ unit ] (unitCost unit) (finish current groups) tail
                else
                    loop (unit :: current) nextLength groups tail

        loop [] 0 [] units

    let private chunkText (units: ContentUnit list) =
        units |> List.map _.Text |> String.concat "\n\n"

    let private createChunk
        (descriptor: ChunkerDescriptor)
        (request: ChunkingRequest)
        sequence
        role
        parent
        (units: ContentUnit list)
        =
        let text = chunkText units

        ChunkingSupport.createChunk descriptor request sequence role parent units text Map.empty

    let create (config: StructureAwareChunkerConfig) =
        if config.MaximumParentCharacters <= 0 then
            invalidArg (nameof config.MaximumParentCharacters) "Maximum parent characters must be positive."

        let descriptor =
            { ContractVersion = ChunkerContractVersion.current
              Component = chunkerComponent
              ConfigurationHash = configurationHash config
              Deterministic = true }

        { Descriptor = descriptor
          ChunkAsync =
            fun request cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()
                    ChunkingSupport.validateRequest request

                    let groups =
                        request.Units
                        |> splitSections
                        |> List.collect (splitByMaximum config.MaximumParentCharacters)

                    let chunks = ResizeArray<KnowledgeChunk>()
                    let mutable sequence = 0

                    for (units: ContentUnit list) in groups do
                        cancellationToken.ThrowIfCancellationRequested()

                        let parent =
                            if units.Length > 1 then
                                let chunk =
                                    createChunk descriptor request sequence KnowledgeChunkRole.Parent None units

                                chunks.Add chunk
                                sequence <- sequence + 1
                                Some chunk.Id
                            else
                                None

                        for unit in units do
                            chunks.Add(createChunk descriptor request sequence KnowledgeChunkRole.Leaf parent [ unit ])

                            sequence <- sequence + 1

                    let values = List.ofSeq chunks

                    return
                        { Chunks = values
                          Metrics = ChunkingMeasurement.measure request.Units values None 0 0L }
                } }
