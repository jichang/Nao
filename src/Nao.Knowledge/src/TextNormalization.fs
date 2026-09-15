namespace Nao.Knowledge

open System
open System.Text

module internal TextNormalization =
    type DecodedText =
        { Text: string
          Encoding: Encoding
          PreambleBytes: int }

    let decodeUtf8 (content: byte array) =
        if isNull content then
            nullArg (nameof content)

        let preamble = Encoding.UTF8.Preamble

        let preambleBytes =
            if content.AsSpan().StartsWith preamble then
                preamble.Length
            else
                0

        try
            { Text = UTF8Encoding(false, true).GetString(content, preambleBytes, content.Length - preambleBytes)
              Encoding = Encoding.UTF8
              PreambleBytes = preambleBytes }
        with :? DecoderFallbackException ->
            invalidArg (nameof content) "Content is not valid UTF-8."

    let normalize (value: string) =
        value.Replace("\r\n", "\n").Replace('\r', '\n').Normalize(NormalizationForm.FormC)

    let byteRange decoded characterOffset characterLength =
        let prefixBytes =
            decoded.Encoding.GetByteCount(decoded.Text.AsSpan(0, characterOffset))

        let contentBytes =
            decoded.Encoding.GetByteCount(decoded.Text.AsSpan(characterOffset, characterLength))

        int64 (decoded.PreambleBytes + prefixBytes), int64 contentBytes

    let lineNumber (text: string) offset =
        1
        + (text.AsSpan(0, offset).ToArray()
           |> Array.sumBy (fun character -> if character = '\n' then 1 else 0))

    let unit decoded kind offset length text metadata =
        let byteOffset, byteLength = byteRange decoded offset length

        { Kind = kind
          Text = normalize text
          Location =
            SourceLocation.create
                byteOffset
                byteLength
                offset
                length
                None
                (Some(lineNumber decoded.Text offset))
                (Some(lineNumber decoded.Text (offset + length)))
                None
          Metadata = metadata |> Map.add "content.trust" "untrusted" }
