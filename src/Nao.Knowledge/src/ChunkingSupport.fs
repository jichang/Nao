namespace Nao.Knowledge

[<RequireQualifiedAccess>]
module internal ChunkingSupport =
    let validateRequest (request: ChunkingRequest) =
        if isNull (box request.Authorization) then
            nullArg (nameof request.Authorization)

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

    let configurationMetadata (descriptor: ChunkerDescriptor) =
        Map.ofList
            [ "chunker.contract", string (ChunkerContractVersion.value descriptor.ContractVersion)
              "chunker.configuration", ContentHash.value descriptor.ConfigurationHash ]

    let createChunk
        (descriptor: ChunkerDescriptor)
        (request: ChunkingRequest)
        sequence
        role
        parent
        (units: ContentUnit list)
        text
        metadata
        =
        let outputHash = ContentHash.computeText text

        let derivation =
            DerivationStep.create
                DerivationStage.Chunking
                descriptor.Component
                (units |> List.map _.ContentHash)
                outputHash
                request.ChunkedAt
                (configurationMetadata descriptor)

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
            [ derivation ]
            metadata
            request.Lifecycle
