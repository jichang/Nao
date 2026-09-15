namespace Nao.Knowledge

open System

module ParserRegistry =
    let private canonicalMediaType (value: string) =
        if String.IsNullOrWhiteSpace value then
            invalidArg (nameof value) "Parser media type must be non-blank."

        value.Split(';', 2, StringSplitOptions.TrimEntries).[0].ToLowerInvariant()

    let private signatureMatches (content: byte array) signature =
        not (isNull signature.Bytes)
        && signature.Bytes.Length > 0
        && signature.Offset >= 0
        && signature.Offset + signature.Bytes.Length <= content.Length
        && content.AsSpan(signature.Offset, signature.Bytes.Length).SequenceEqual(signature.Bytes)

    let create parsers =
        if List.isEmpty parsers then
            invalidArg (nameof parsers) "Parser registry requires at least one parser."

        for parser in parsers do
            if Set.isEmpty parser.Descriptor.MediaTypes then
                invalidArg (nameof parsers) "Every parser must declare at least one media type."

            for signature in parser.Descriptor.Signatures do
                if isNull signature.Bytes || signature.Bytes.Length = 0 || signature.Offset < 0 then
                    invalidArg (nameof parsers) "Parser signatures must be non-empty and have a non-negative offset."

        { Discover =
            fun mediaType content ->
                if isNull content then
                    nullArg (nameof content)

                let mediaType = canonicalMediaType mediaType

                let signatureMatch =
                    parsers
                    |> List.tryFind (fun parser ->
                        parser.Descriptor.Signatures |> List.exists (signatureMatches content))

                signatureMatch
                |> Option.orElseWith (fun () ->
                    parsers
                    |> List.tryFind (fun parser ->
                        parser.Descriptor.MediaTypes
                        |> Set.map canonicalMediaType
                        |> Set.contains mediaType)) }
