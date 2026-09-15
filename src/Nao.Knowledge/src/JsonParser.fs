namespace Nao.Knowledge

open System
open System.Text.Json

module JsonParser =
    let private parser = ComponentVersion.create "nao.parser.json" "1"

    let create () =
        { Descriptor =
            { Parser = parser
              MediaTypes = Set.ofList [ "application/json"; "text/json" ]
              Signatures = [] }
          ParseAsync =
            fun _ content _ cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    try
                        let decoded = TextNormalization.decodeUtf8 content

                        use _document =
                            JsonDocument.Parse(
                                decoded.Text,
                                JsonDocumentOptions(
                                    AllowTrailingCommas = false,
                                    CommentHandling = JsonCommentHandling.Disallow,
                                    MaxDepth = 128
                                )
                            )

                        let text = TextNormalization.normalize decoded.Text

                        return
                            Ok
                                { Units =
                                    [ TextNormalization.unit
                                          decoded
                                          (ContentUnitKind.Other "json")
                                          0
                                          decoded.Text.Length
                                          text
                                          (Map.ofList [ "path", "$" ]) ]
                                  Language = None
                                  RequiresOcr = false
                                  Trust = ParsedContentTrust.UntrustedSource }
                    with
                    | :? ArgumentException as error ->
                        return
                            Error
                                { Kind = ParserRejectionKind.Malformed
                                  Message = error.Message }
                    | :? JsonException as error ->
                        return
                            Error
                                { Kind = ParserRejectionKind.Malformed
                                  Message = error.Message }
                } }
