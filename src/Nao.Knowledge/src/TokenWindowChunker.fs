namespace Nao.Knowledge

open System
open System.Globalization

type TokenWindowChunkerConfig =
    { MaximumTokens: int
      OverlapTokens: int
      Tokenizer: KnowledgeTokenizer }

[<RequireQualifiedAccess>]
module TokenWindowChunker =
    type private LocatedUnit =
        { Unit: ContentUnit
          Start: int
          EndExclusive: int }

    let private chunkerComponent =
        ComponentVersion.create "nao.chunker.token-window" "1"

    let private configurationHash config =
        String.concat
            ";"
            [ $"maximum-tokens={config.MaximumTokens.ToString(CultureInfo.InvariantCulture)}"
              $"overlap-tokens={config.OverlapTokens.ToString(CultureInfo.InvariantCulture)}"
              $"tokenizer={config.Tokenizer.Descriptor.Component.ComponentId}@{config.Tokenizer.Descriptor.Component.Version}"
              $"tokenizer-configuration={ContentHash.value config.Tokenizer.Descriptor.ConfigurationHash}" ]
        |> ContentHash.computeText

    let private canonicalText (units: ContentUnit list) =
        let text = Text.StringBuilder()

        let located =
            units
            |> List.map (fun unit ->
                if text.Length > 0 then
                    text.Append("\n\n") |> ignore

                let start = text.Length
                text.Append(unit.Text) |> ignore

                { Unit = unit
                  Start = start
                  EndExclusive = text.Length })

        text.ToString(), located

    let private validateTokens (text: string) (tokens: TokenSpan list) =
        if isNull (box tokens) then
            invalidOp "Tokenizer returned a null token collection."

        tokens
        |> List.iter (fun token ->
            if
                token.CharacterOffset < 0
                || token.CharacterLength <= 0
                || token.CharacterOffset > text.Length
                || token.CharacterLength > text.Length - token.CharacterOffset
            then
                invalidOp "Tokenizer returned a token outside the supplied text.")

        tokens
        |> List.pairwise
        |> List.iter (fun (left, right) ->
            if right.CharacterOffset < left.CharacterOffset + left.CharacterLength then
                invalidOp "Tokenizer returned overlapping or unordered token spans.")

    let private contributingUnits startOffset endExclusive (located: LocatedUnit list) =
        located
        |> List.choose (fun item ->
            if item.EndExclusive > startOffset && item.Start < endExclusive then
                Some item.Unit
            else
                None)

    let create (config: TokenWindowChunkerConfig) =
        if config.MaximumTokens <= 0 then
            invalidArg (nameof config.MaximumTokens) "Maximum tokens must be positive."

        if config.OverlapTokens < 0 || config.OverlapTokens >= config.MaximumTokens then
            invalidArg (nameof config.OverlapTokens) "Overlap tokens must be non-negative and smaller than the window."

        if isNull (box config.Tokenizer) then
            nullArg (nameof config.Tokenizer)

        let descriptor =
            { ContractVersion = ChunkerContractVersion.current
              Component = chunkerComponent
              ConfigurationHash = configurationHash config
              Deterministic = config.Tokenizer.Descriptor.Deterministic }

        { Descriptor = descriptor
          ChunkAsync =
            fun request cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()
                    ChunkingSupport.validateRequest request

                    let text, located = canonicalText request.Units
                    let tokens = config.Tokenizer.Tokenize text
                    validateTokens text tokens

                    let tokenArray = List.toArray tokens
                    let chunks = ResizeArray<KnowledgeChunk>()
                    let chunkTokenSizes = ResizeArray<int>()
                    let step = config.MaximumTokens - config.OverlapTokens
                    let mutable tokenStart = 0
                    let mutable sequence = 0

                    while tokenStart < tokenArray.Length do
                        cancellationToken.ThrowIfCancellationRequested()
                        let tokenEnd = min tokenArray.Length (tokenStart + config.MaximumTokens)
                        let first = tokenArray[tokenStart]
                        let last = tokenArray[tokenEnd - 1]
                        let endExclusive = last.CharacterOffset + last.CharacterLength
                        let units = contributingUnits first.CharacterOffset endExclusive located

                        if List.isEmpty units then
                            invalidOp "Tokenizer returned a token that does not map to a content unit."

                        let chunkText =
                            text.Substring(first.CharacterOffset, endExclusive - first.CharacterOffset)

                        let metadata =
                            Map.ofList
                                [ ("tokenizer.component", config.Tokenizer.Descriptor.Component.ComponentId)
                                  ("tokenizer.version", config.Tokenizer.Descriptor.Component.Version)
                                  ("tokenizer.configuration",
                                   ContentHash.value config.Tokenizer.Descriptor.ConfigurationHash)
                                  ("token.start", string tokenStart)
                                  ("token.count", string (tokenEnd - tokenStart))
                                  ("text.character-start", string first.CharacterOffset)
                                  ("text.character-length", string (endExclusive - first.CharacterOffset)) ]

                        chunks.Add(
                            ChunkingSupport.createChunk
                                descriptor
                                request
                                sequence
                                KnowledgeChunkRole.Leaf
                                None
                                units
                                chunkText
                                metadata
                        )

                        chunkTokenSizes.Add(tokenEnd - tokenStart)

                        sequence <- sequence + 1

                        tokenStart <-
                            if tokenEnd = tokenArray.Length then
                                tokenArray.Length
                            else
                                tokenStart + step

                    let values = List.ofSeq chunks

                    return
                        { Chunks = values
                          Metrics =
                            ChunkingMeasurement.measure request.Units values (Some(List.ofSeq chunkTokenSizes)) 0 0L }
                } }
