namespace Nao.Knowledge.Documents

open System
open System.Globalization
open System.IO
open System.Text
open System.Threading.Tasks
open Nao.Knowledge
open Nao.Documents

[<RequireQualifiedAccess>]
module internal DocumentParserRuntime =
    let private contentKind block =
        match block with
        | Heading _ -> ContentUnitKind.Heading
        | Paragraph _ when DocumentText.isStandaloneLink block -> ContentUnitKind.Link
        | Paragraph _ -> ContentUnitKind.Paragraph
        | ListBlock _ -> ContentUnitKind.List
        | Table _ -> ContentUnitKind.Table
        | CodeBlock _ -> ContentUnitKind.Code
        | _ -> ContentUnitKind.Other "document"

    let private structuralMetadata =
        function
        | Heading(level, _) -> Map.ofList [ "heading.level", string level ]
        | _ -> Map.empty

    let private sourceLocation path =
        function
        | DocumentSourceLocation.ExactText source ->
            SourceLocation.create
                source.ByteOffset
                source.ByteLength
                source.CharacterOffset
                source.CharacterLength
                source.Page
                (Some source.LineStart)
                (Some source.LineEnd)
                (Some path),
            "exact",
            Map.empty
        | DocumentSourceLocation.PackagePath source ->
            SourceLocation.create 0L 0L 0 0 source.Page None None (Some $"{source.Part}#{source.Path}"),
            "structural",
            Map.empty
        | DocumentSourceLocation.PageRegion source ->
            let bounds = source.Bounds

            let encodedBounds =
                [ bounds.X.Value; bounds.Y.Value; bounds.Width.Value; bounds.Height.Value ]
                |> List.map (fun value -> value.ToString("R", CultureInfo.InvariantCulture))
                |> String.concat ","

            SourceLocation.create 0L 0L 0 0 (Some source.Page) None None (Some path),
            "page-region",
            Map.ofList
                [ "source.bounds.points", encodedBounds
                  "source.coordinate-origin", "bottom-left" ]

    let private parsedUnit document (path, block) =
        match Document.trySource path document with
        | None ->
            Error
                { Kind = ParserRejectionKind.Suspicious
                  Message = "The document reader omitted required source provenance." }
        | Some source ->
            let location, precision, sourceMetadata = sourceLocation path source
            let links = DocumentText.blockLinks block |> List.distinct |> String.concat "\u001f"

            let baseMetadata =
                Map.ofList
                    [ "content.trust", "untrusted"
                      "document.path", path
                      "source.provenance", precision ]

            let metadata =
                Map.fold (fun values key value -> values |> Map.add key value) baseMetadata sourceMetadata
                |> fun values ->
                    Map.fold (fun state key value -> state |> Map.add key value) values (structuralMetadata block)
                |> fun values ->
                    if links = "" then
                        values
                    else
                        values |> Map.add "links" links

            Ok
                { Kind = contentKind block
                  Text = DocumentText.blockText block
                  Location = location
                  Metadata = metadata }

    let private parseDocument packageReadLimits (reader: IDocumentReader) content =
        use input = new MemoryStream(content, writable = false)
        let document = reader.Read(input, ConversionContext.InMemory packageReadLimits)
        let blocks = Document.toLocatedBlocks document

        let units =
            blocks
            |> List.map (parsedUnit document)
            |> List.filter (function
                | Ok unit -> not (String.IsNullOrWhiteSpace unit.Text)
                | Error _ -> true)

        match
            units
            |> List.tryPick (function
                | Error rejection -> Some rejection
                | Ok _ -> None)
        with
        | Some rejection -> Error rejection
        | None ->
            Ok
                { Units =
                    units
                    |> List.choose (function
                        | Ok unit -> Some unit
                        | Error _ -> None)
                  Language = document.Metadata.Language
                  RequiresOcr =
                    document.Metadata.Properties
                    |> Map.tryFind "document.requires-ocr"
                    |> Option.contains "true"
                  Trust = ParsedContentTrust.UntrustedSource }

    let private create packageReadLimits descriptor reader =
        { Descriptor = descriptor
          ParseAsync =
            fun _ content _ cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()

                try
                    parseDocument packageReadLimits reader content |> Task.FromResult
                with
                | PackageLimitExceededException message ->
                    Task.FromResult(
                        Error
                            { Kind = ParserRejectionKind.Suspicious
                              Message = message }
                    )
                | DocumentEncryptedException message ->
                    Task.FromResult(
                        Error
                            { Kind = ParserRejectionKind.Encrypted
                              Message = message }
                    )
                | DocumentMalformedException message ->
                    Task.FromResult(
                        Error
                            { Kind = ParserRejectionKind.Malformed
                              Message = message }
                    )
                | :? InvalidDataException as error ->
                    Task.FromResult(
                        Error
                            { Kind = ParserRejectionKind.Malformed
                              Message = error.Message }
                    )
                | :? DecoderFallbackException as error ->
                    Task.FromResult(
                        Error
                            { Kind = ParserRejectionKind.Malformed
                              Message = error.Message }
                    )
                | :? ArgumentException as error ->
                    Task.FromResult(
                        Error
                            { Kind = ParserRejectionKind.Malformed
                              Message = error.Message }
                    ) }

    let markdown packageReadLimits =
        create
            packageReadLimits
            { Parser = ComponentVersion.create "signedbyte.documents.markdown" "1"
              MediaTypes = Set.ofList [ "text/markdown"; "text/x-markdown" ]
              Signatures = [] }
            (Markdown.Reader())

    let html packageReadLimits =
        create
            packageReadLimits
            { Parser = ComponentVersion.create "signedbyte.documents.html" "1"
              MediaTypes = Set.ofList [ "text/html"; "application/xhtml+xml" ]
              Signatures = [] }
            (Html.Reader())

    let docx packageReadLimits =
        create
            packageReadLimits
            { Parser = ComponentVersion.create "signedbyte.documents.docx" "1"
              MediaTypes = Set.singleton Docx.MediaType
              Signatures = [] }
            (Docx.Reader())

    let odt packageReadLimits =
        create
            packageReadLimits
            { Parser = ComponentVersion.create "signedbyte.documents.odt" "1"
              MediaTypes = Set.singleton Odt.MediaType
              Signatures = [] }
            (Odt.Reader())

    let pdf packageReadLimits =
        create
            packageReadLimits
            { Parser = ComponentVersion.create "signedbyte.documents.pdf" "1"
              MediaTypes = Set.singleton Pdf.MediaType
              Signatures =
                [ { Offset = 0
                    Bytes = Encoding.ASCII.GetBytes "%PDF-" } ] }
            (Pdf.Reader())

    let structured packageReadLimits =
        [ markdown packageReadLimits
          html packageReadLimits
          docx packageReadLimits
          odt packageReadLimits
          pdf packageReadLimits ]
