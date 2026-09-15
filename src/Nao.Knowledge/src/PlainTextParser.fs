namespace Nao.Knowledge

open System
open System.Threading.Tasks

module PlainTextParser =
    let private parser = ComponentVersion.create "nao.parser.text" "1"

    let private lineNumber (text: string) offset =
        1
        + (text.AsSpan(0, offset).ToArray()
           |> Array.sumBy (fun character -> if character = '\n' then 1 else 0))

    let private paragraphs (text: string) =
        let units = ResizeArray<int * int>()
        let mutable start = 0
        let mutable index = 0

        let add ending =
            let mutable first = start
            let mutable last = ending

            while first < last && Char.IsWhiteSpace text[first] do
                first <- first + 1

            while last > first && Char.IsWhiteSpace text[last - 1] do
                last <- last - 1

            if last > first then
                units.Add(first, last - first)

        while index < text.Length do
            let separatorLength =
                if index + 3 < text.Length && text.AsSpan(index, 4).SequenceEqual("\r\n\r\n") then
                    4
                elif
                    index + 1 < text.Length
                    && ((text[index] = '\n' && text[index + 1] = '\n')
                        || (text[index] = '\r' && text[index + 1] = '\r'))
                then
                    2
                else
                    0

            if separatorLength > 0 then
                add index
                index <- index + separatorLength
                start <- index
            else
                index <- index + 1

        add text.Length
        List.ofSeq units

    let create () =
        { Descriptor =
            { Parser = parser
              MediaTypes = Set.ofList [ "text/plain" ]
              Signatures = [] }
          ParseAsync =
            fun _ content _ cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    try
                        let decoded = TextNormalization.decodeUtf8 content

                        let units =
                            paragraphs decoded.Text
                            |> List.map (fun (offset, length) ->
                                cancellationToken.ThrowIfCancellationRequested()
                                let sourceText = decoded.Text.Substring(offset, length)
                                let normalized = TextNormalization.normalize sourceText
                                let byteOffset, byteLength = TextNormalization.byteRange decoded offset length
                                let firstLine = lineNumber decoded.Text offset
                                let lastLine = lineNumber decoded.Text (offset + length)

                                { Kind = ContentUnitKind.Paragraph
                                  Text = normalized
                                  Location =
                                    SourceLocation.create
                                        byteOffset
                                        byteLength
                                        offset
                                        length
                                        None
                                        (Some firstLine)
                                        (Some lastLine)
                                        None
                                  Metadata =
                                    Map.ofList [ "content.trust", "untrusted"; "normalization", "utf8-lf-nfc" ] })

                        return
                            Ok
                                { Units = units
                                  Language = None
                                  RequiresOcr = false
                                  Trust = ParsedContentTrust.UntrustedSource }
                    with :? ArgumentException as error ->
                        return
                            Error
                                { Kind = ParserRejectionKind.Malformed
                                  Message = error.Message }
                } }
