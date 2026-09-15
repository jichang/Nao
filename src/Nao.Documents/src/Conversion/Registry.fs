namespace Nao.Documents

open System
open System.Collections.Generic
open System.IO

/// A registry of format readers and writers, keyed by media type. This is the entry
/// point for "A → unified → B": register one `IDocumentReader` and one
/// `IDocumentWriter` per format, then call `Convert`.
type ConverterRegistry() =

    let readers = Dictionary<string, IDocumentReader>(StringComparer.OrdinalIgnoreCase)
    let writers = Dictionary<string, IDocumentWriter>(StringComparer.OrdinalIgnoreCase)

    /// Register a reader for all of its declared media types.
    member _.RegisterReader(reader: IDocumentReader) =
        for mt in reader.MediaTypes do
            readers.[mt] <- reader

    /// Register a writer for all of its declared media types.
    member _.RegisterWriter(writer: IDocumentWriter) =
        for mt in writer.MediaTypes do
            writers.[mt] <- writer

    /// Media types that can be read from.
    member _.ReadableTypes = readers.Keys |> Seq.toList

    /// Media types that can be written to.
    member _.WritableTypes = writers.Keys |> Seq.toList

    member _.TryGetReader(mediaType: string) =
        match readers.TryGetValue mediaType with
        | true, r -> Some r
        | _ -> None

    member _.TryGetWriter(mediaType: string) =
        match writers.TryGetValue mediaType with
        | true, w -> Some w
        | _ -> None

    /// Parse a source stream, returning a typed error instead of throwing.
    member this.TryRead(mediaType: string, input: Stream, ctx: ConversionContext) : Result<Document, ConversionError> =
        match this.TryGetReader mediaType with
        | None -> Error(UnsupportedMediaType mediaType)
        | Some reader ->
            try
                Ok(reader.Read(input, ctx))
            with
            | :? ArgumentException as ex -> Error(InvalidInput(mediaType, ex.Message))
            | :? InvalidDataException as ex -> Error(InvalidInput(mediaType, ex.Message))
            | ex -> Error(ConversionFailure(mediaType, ex.Message))

    /// Serialize a document, returning a typed error instead of throwing.
    member this.TryWrite
        (mediaType: string, doc: Document, output: Stream, ctx: ConversionContext)
        : Result<unit, ConversionError> =
        match this.TryGetWriter mediaType with
        | None -> Error(UnsupportedMediaType mediaType)
        | Some writer ->
            try
                writer.Write(doc, output, ctx)
                Ok()
            with
            | :? ArgumentException as ex -> Error(InvalidInput(mediaType, ex.Message))
            | :? InvalidDataException as ex -> Error(InvalidInput(mediaType, ex.Message))
            | ex -> Error(ConversionFailure(mediaType, ex.Message))

    /// Convert a stream using typed errors. The existing Convert method remains the
    /// exception-based convenience API.
    member this.TryConvert
        (sourceMediaType: string, input: Stream, targetMediaType: string, output: Stream, ctx: ConversionContext)
        : Result<unit, ConversionError> =
        match this.TryRead(sourceMediaType, input, ctx) with
        | Error error -> Error error
        | Ok doc -> this.TryWrite(targetMediaType, doc, output, ctx)

    /// Parse a source stream of the given media type into the unified model.
    member this.Read(mediaType: string, input: Stream, ctx: ConversionContext) : Document =
        match this.TryGetReader mediaType with
        | Some r -> r.Read(input, ctx)
        | None -> failwithf "No reader registered for media type '%s'" mediaType

    /// Serialize the unified model to a target media type.
    member this.Write(mediaType: string, doc: Document, output: Stream, ctx: ConversionContext) =
        match this.TryGetWriter mediaType with
        | Some w -> w.Write(doc, output, ctx)
        | None -> failwithf "No writer registered for media type '%s'" mediaType

    /// Convert in one step: read `sourceMediaType` from `input`, then write
    /// `targetMediaType` to `output`. This is the canonical A → unified → B path.
    member this.Convert
        (sourceMediaType: string, input: Stream, targetMediaType: string, output: Stream, ctx: ConversionContext)
        =
        let doc = this.Read(sourceMediaType, input, ctx)
        this.Write(targetMediaType, doc, output, ctx)

/// Top-level conversion helpers over a `ConverterRegistry`.
[<RequireQualifiedAccess>]
module Converter =

    let private imageWarningTargets =
        [ "text/plain"; "text/csv"; "application/vnd.oasis.opendocument.text" ]
        |> Set.ofList

    let rec private hasInlineImage (inlines: Inline list) =
        inlines
        |> List.exists (function
            | InlineImage _ -> true
            | Link(children, _) -> hasInlineImage children
            | _ -> false)

    let rec private blockHasImage (block: Block) =
        match block with
        | Paragraph(inlines, _)
        | Heading(_, inlines) -> hasInlineImage inlines
        | ListBlock spec ->
            spec.Items
            |> List.exists (fun item -> item.Content |> List.exists blockHasImage)
        | Quote blocks -> blocks |> List.exists blockHasImage
        | Table spec ->
            let rows = (spec.Header |> Option.toList) @ spec.Rows

            rows
            |> List.exists (fun row -> row.Cells |> List.exists (fun cell -> cell.Content |> List.exists blockHasImage))
        | Container spec -> spec.Children |> List.exists (fun child -> blockHasImage child.Content)
        | _ -> false

    let private warningsFor (targetMediaType: string) (doc: Document) =
        if
            Set.contains targetMediaType imageWarningTargets
            && Document.toBlocks doc |> List.exists blockHasImage
        then
            [ UnsupportedBlock(targetMediaType, "image") ]
        else
            []

    /// Convert raw bytes while returning conversion warnings and a structured error.
    let tryConvertBytesWithWarnings
        (registry: ConverterRegistry)
        (sourceMediaType: string)
        (targetMediaType: string)
        (ctx: ConversionContext)
        (input: byte[])
        : Result<byte[] * ConversionWarning list, ConversionError> =
        use inStream = new MemoryStream(input)
        use outStream = new MemoryStream()

        match registry.TryRead(sourceMediaType, inStream, ctx) with
        | Error error -> Error error
        | Ok doc ->
            match registry.TryWrite(targetMediaType, doc, outStream, ctx) with
            | Error error -> Error error
            | Ok() -> Ok(outStream.ToArray(), warningsFor targetMediaType doc)

    /// Convert raw bytes while returning a structured error instead of throwing.
    let tryConvertBytes
        (registry: ConverterRegistry)
        (sourceMediaType: string)
        (targetMediaType: string)
        (ctx: ConversionContext)
        (input: byte[])
        : Result<byte[], ConversionError> =
        tryConvertBytesWithWarnings registry sourceMediaType targetMediaType ctx input
        |> Result.map fst

    /// Convert raw bytes from one media type to another, returning the produced bytes.
    /// Resources are extracted/resolved through `ctx`.
    let convertBytes
        (registry: ConverterRegistry)
        (sourceMediaType: string)
        (targetMediaType: string)
        (ctx: ConversionContext)
        (input: byte[])
        : byte[] =
        use inStream = new MemoryStream(input)
        use outStream = new MemoryStream()
        registry.Convert(sourceMediaType, inStream, targetMediaType, outStream, ctx)
        outStream.ToArray()

    /// Convert a file on disk to another format on disk, extracting media into a
    /// bundle directory alongside the output (defaults to the output's folder).
    let convertFile
        (registry: ConverterRegistry)
        (sourceMediaType: string)
        (targetMediaType: string)
        (sourcePath: string)
        (targetPath: string)
        : unit =
        let bundleRoot =
            Path.GetDirectoryName(Path.GetFullPath targetPath)
            |> fun d -> if String.IsNullOrEmpty d then "." else d

        let ctx =
            ConversionContext.ForDirectory(bundleRoot, PackageReadLimits.create 4096 67108864L 268435456L 200.0)

        use inStream = File.OpenRead sourcePath
        use outStream = File.Create targetPath
        registry.Convert(sourceMediaType, inStream, targetMediaType, outStream, ctx)
