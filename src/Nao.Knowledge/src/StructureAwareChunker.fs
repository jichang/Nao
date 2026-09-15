namespace Nao.Knowledge

open System
open System.Globalization
open System.Text
open System.Threading.Tasks

type StructureAwareChunkerConfig = { MaximumParentCharacters: int }

[<RequireQualifiedAccess>]
module StructureAwareChunker =
    let private chunkerComponent = ComponentVersion.create "nao.chunker.structure" "1"

    let private configurationHash config =
        $"maximum-parent-characters={config.MaximumParentCharacters.ToString(CultureInfo.InvariantCulture)}"
        |> ContentHash.computeText

    let private validateRequest (request: ChunkingRequest) =
        if isNull (box request.Units) then
            nullArg (nameof request.Units)

        if
            request.Units
            |> List.exists (fun unit -> unit.DocumentVersionId <> request.DocumentVersionId)
        then
            invalidArg (nameof request) "Every content unit must belong to the requested document version."

        let distinctIds = request.Units |> List.map _.Id |> Set.ofList

        if distinctIds.Count <> request.Units.Length then
            invalidArg (nameof request) "Chunking input must not contain duplicate content units."

        request.Units
        |> List.pairwise
        |> List.iter (fun (left, right) ->
            if right.Sequence <= left.Sequence then
                invalidArg (nameof request) "Content units must be ordered by increasing sequence.")

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

    let private derivation (descriptor: ChunkerDescriptor) chunkedAt (units: ContentUnit list) text =
        DerivationStep.create
            DerivationStage.Chunking
            descriptor.Component
            (units |> List.map _.ContentHash)
            (ContentHash.computeText text)
            chunkedAt
            (Map.ofList
                [ "chunker.contract", string (ChunkerContractVersion.value descriptor.ContractVersion)
                  "chunker.configuration", ContentHash.value descriptor.ConfigurationHash ])

    let private createChunk
        (descriptor: ChunkerDescriptor)
        (request: ChunkingRequest)
        sequence
        role
        parent
        (units: ContentUnit list)
        =
        let text = chunkText units

        KnowledgeChunk.create
            request.DocumentVersionId
            sequence
            role
            parent
            (units |> List.map _.Id)
            text
            (units |> List.map _.Location |> List.distinct)
            descriptor.Component
            descriptor.ConfigurationHash
            [ derivation descriptor request.ChunkedAt units text ]
            Map.empty
            request.Lifecycle

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
                    validateRequest request

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
                          InputUnits = request.Units.Length
                          ParentChunks =
                            values
                            |> List.filter (fun chunk -> chunk.Role = KnowledgeChunkRole.Parent)
                            |> List.length
                          LeafChunks =
                            values
                            |> List.filter (fun chunk -> chunk.Role = KnowledgeChunkRole.Leaf)
                            |> List.length }
                } }
