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
        (
            sourceMediaType: string,
            input: Stream,
            targetMediaType: string,
            output: Stream,
            ctx: ConversionContext
        ) =
        let doc = this.Read(sourceMediaType, input, ctx)
        this.Write(targetMediaType, doc, output, ctx)

/// Top-level conversion helpers over a `ConverterRegistry`.
[<RequireQualifiedAccess>]
module Converter =

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
        let ctx = ConversionContext.ForDirectory bundleRoot
        use inStream = File.OpenRead sourcePath
        use outStream = File.Create targetPath
        registry.Convert(sourceMediaType, inStream, targetMediaType, outStream, ctx)
