namespace Nao.Knowledge

open System

[<RequireQualifiedAccess>]
module ChunkingMeasurement =
    let private distribution sizes =
        match sizes |> List.sort with
        | [] ->
            { Count = 0
              Minimum = 0
              Maximum = 0
              Mean = 0.0
              P50 = 0
              P95 = 0 }
        | sorted ->
            let percentile value =
                let index = int (Math.Ceiling(value * float sorted.Length)) - 1
                sorted[max 0 index]

            { Count = sorted.Length
              Minimum = List.head sorted
              Maximum = List.last sorted
              Mean = sorted |> List.averageBy float
              P50 = percentile 0.50
              P95 = percentile 0.95 }

    let measure
        (inputUnits: ContentUnit list)
        (chunks: KnowledgeChunk list)
        (tokenSizes: int list option)
        (truncatedUnits: int)
        (truncatedCharacters: int64)
        =
        if isNull (box inputUnits) then
            nullArg (nameof inputUnits)

        if isNull (box chunks) then
            nullArg (nameof chunks)

        if truncatedUnits < 0 || truncatedUnits > inputUnits.Length then
            invalidArg (nameof truncatedUnits) "Truncated units must be within the input-unit count."

        let inputCharacters = inputUnits |> List.sumBy (fun unit -> int64 unit.Text.Length)

        if truncatedCharacters < 0L || truncatedCharacters > inputCharacters then
            invalidArg (nameof truncatedCharacters) "Truncated characters must be within the input character count."

        let tokenDistribution =
            tokenSizes
            |> Option.map (fun sizes ->
                if
                    isNull (box sizes)
                    || sizes.Length <> chunks.Length
                    || sizes |> List.exists (fun size -> size < 0)
                then
                    invalidArg (nameof tokenSizes) "Token sizes must contain one non-negative value per chunk."

                distribution sizes)

        let rate numerator denominator =
            if denominator = 0L then
                0.0
            else
                float numerator / float denominator

        { InputUnits = inputUnits.Length
          OutputChunks = chunks.Length
          ParentChunks =
            chunks
            |> List.filter (fun chunk -> chunk.Role = KnowledgeChunkRole.Parent)
            |> List.length
          LeafChunks =
            chunks
            |> List.filter (fun chunk -> chunk.Role = KnowledgeChunkRole.Leaf)
            |> List.length
          CharacterSizes = chunks |> List.map (fun chunk -> chunk.Text.Length) |> distribution
          TokenSizes = tokenDistribution
          InputCharacters = inputCharacters
          TruncatedUnits = truncatedUnits
          TruncatedCharacters = truncatedCharacters
          TruncatedUnitRate = rate (int64 truncatedUnits) (int64 inputUnits.Length)
          TruncatedCharacterRate = rate truncatedCharacters inputCharacters }
