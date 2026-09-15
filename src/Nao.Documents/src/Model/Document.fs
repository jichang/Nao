namespace Nao.Documents

open System
open System.Text

/// Exact coordinates of a document node in a decoded text source.
type ExactTextSourceLocation =
    { ByteOffset: int64
      ByteLength: int64
      CharacterOffset: int
      CharacterLength: int
      LineStart: int
      LineEnd: int
      Page: int option }

/// A node address inside a structured package whose serialized byte range is not stable.
type PackageSourceLocation =
    { Part: string
      Path: string
      Page: int option }

/// Coordinate origin for a region on a fixed page.
[<RequireQualifiedAccess>]
type PageCoordinateOrigin = | BottomLeft

/// A geometric source region on a fixed page, expressed in typographic points.
type PageSourceLocation =
    { Page: int
      Bounds: Rect
      Origin: PageCoordinateOrigin }

/// Honest source provenance: exact ranges are distinct from structural package addresses.
[<RequireQualifiedAccess>]
type DocumentSourceLocation =
    | ExactText of ExactTextSourceLocation
    | PackagePath of PackageSourceLocation
    | PageRegion of PageSourceLocation

[<RequireQualifiedAccess>]
module SourcePath =
    let bodyBlock index = $"body/{index}"
    let pageHeader page index = $"pages/{page}/header/{index}"
    let pageContent page index = $"pages/{page}/content/{index}"
    let pageFooter page index = $"pages/{page}/footer/{index}"

[<RequireQualifiedAccess>]
module SourceText =
    type Decoded = { Text: string; PreambleBytes: int }

    let readUtf8 (input: IO.Stream) =
        use buffer = new IO.MemoryStream()
        input.CopyTo buffer
        let bytes = buffer.ToArray()
        let preamble = Encoding.UTF8.Preamble

        let preambleBytes =
            if bytes.AsSpan().StartsWith preamble then
                preamble.Length
            else
                0

        { Text = UTF8Encoding(false, true).GetString(bytes, preambleBytes, bytes.Length - preambleBytes)
          PreambleBytes = preambleBytes }

    let location (decoded: Decoded) offset length =
        let lineAt position =
            1
            + (decoded.Text.AsSpan(0, position).ToArray()
               |> Array.sumBy (fun character -> if character = '\n' then 1 else 0))

        DocumentSourceLocation.ExactText
            { ByteOffset =
                int64 (
                    decoded.PreambleBytes
                    + Encoding.UTF8.GetByteCount(decoded.Text.AsSpan(0, offset))
                )
              ByteLength = int64 (Encoding.UTF8.GetByteCount(decoded.Text.AsSpan(offset, length)))
              CharacterOffset = offset
              CharacterLength = length
              LineStart = lineAt offset
              LineEnd = lineAt (offset + length)
              Page = None }

/// Page orientation for paginated formats.
type PageOrientation =
    | Portrait
    | Landscape

/// Physical page setup used by paginated formats (pdf / word / ppt).
type PageSetup =
    { Width: Length
      Height: Length
      Orientation: PageOrientation
      Margins: Edges }

    /// ISO A4 portrait, in points (210 × 297 mm).
    static member A4 =
        { Width = Length.Pt 595.0
          Height = Length.Pt 842.0
          Orientation = Portrait
          Margins = Edges.All(Length.Pt 72.0) }

    /// US Letter portrait, in points (8.5 × 11 in).
    static member Letter =
        { Width = Length.Pt 612.0
          Height = Length.Pt 792.0
          Orientation = Portrait
          Margins = Edges.All(Length.Pt 72.0) }

    /// A 16:9 presentation slide, in points (13.33 × 7.5 in).
    static member Slide16x9 =
        { Width = Length.Pt 960.0
          Height = Length.Pt 540.0
          Orientation = Landscape
          Margins = Edges.Zero }

/// A single fixed page (or slide) in a paginated document.
type Page =
    {
        Setup: PageSetup
        /// Repeating header content (running head).
        Header: Block list
        /// Repeating footer content (page numbers, etc.).
        Footer: Block list
        /// The page body. Typically a single `Container` for precise layout, but any
        /// list of blocks is allowed.
        Content: Block list
    }

    static member Of(setup: PageSetup, content: Block list) =
        { Setup = setup
          Header = []
          Footer = []
          Content = content }

/// The document body: the single switch between the two fundamental document shapes.
///
/// * `Fluid`  — reflowable content (text / markdown / html). Paginated *targets*
///   auto-break it into pages using `Document.DefaultPage`.
/// * `Paged`  — fixed pages / slides (pdf / word-as-laid-out / ppt). Fluid *targets*
///   flatten the pages back into a single content stream.
type Body =
    | Fluid of Block list
    | Paged of Page list

/// Descriptive metadata about a document.
type DocumentMetadata =
    {
        Title: string option
        Authors: string list
        /// BCP-47 language tag, e.g. "en", "zh-CN".
        Language: string option
        Created: DateTimeOffset option
        Modified: DateTimeOffset option
        /// Free-form extra properties (subject, keywords, producer, source app, ...).
        Properties: Map<string, string>
    }

    static member Empty =
        { Title = None
          Authors = []
          Language = None
          Created = None
          Modified = None
          Properties = Map.empty }

/// The unified intermediate representation (the "middleware" format).
///
/// Every concrete file format converts to and from this single model, so the
/// converter only needs an A→`Document` reader and a `Document`→B writer for each
/// format, and any A→B conversion is `read >> write`.
type Document =
    {
        /// Schema/version marker so consumers can evolve the model safely.
        Schema: string
        Metadata: DocumentMetadata
        /// Extracted media / binary assets, referenced by id from the body.
        Resources: Resource list
        /// The content, either fluid or paginated.
        Body: Body
        /// Source locations keyed by canonical structural path.
        Sources: Map<string, DocumentSourceLocation>
        /// Page setup a paginated *target* should use when the source was fluid.
        DefaultPage: PageSetup option
    }

    /// Current schema identifier.
    static member CurrentSchema = "signedbyte-doc/1"

    /// An empty fluid document.
    static member Empty =
        { Schema = Document.CurrentSchema
          Metadata = DocumentMetadata.Empty
          Resources = []
          Body = Fluid []
          Sources = Map.empty
          DefaultPage = None }

    /// A fluid document from a list of blocks.
    static member OfBlocks(blocks: Block list) =
        { Document.Empty with
            Body = Fluid blocks }

    /// A paginated document from a list of pages.
    static member OfPages(pages: Page list) =
        { Document.Empty with
            Body = Paged pages }

[<RequireQualifiedAccess>]
module Document =

    /// All top-level blocks of a document as a single stream, flattening pages
    /// (and their headers/footers) in order. Useful for fluid writers.
    let toBlocks (doc: Document) : Block list =
        doc
        |> fun document ->
            match document.Body with
            | Fluid blocks -> blocks
            | Paged pages -> pages |> List.collect (fun p -> p.Header @ p.Content @ p.Footer)

    /// Top-level blocks paired with stable paths in fluid or paginated documents.
    let toLocatedBlocks (doc: Document) : (string * Block) list =
        match doc.Body with
        | Fluid blocks -> blocks |> List.mapi (fun index block -> SourcePath.bodyBlock index, block)
        | Paged pages ->
            pages
            |> List.mapi (fun pageIndex page ->
                let pageNumber = pageIndex + 1

                [ yield!
                      page.Header
                      |> List.mapi (fun index block -> SourcePath.pageHeader pageNumber index, block)
                  yield!
                      page.Content
                      |> List.mapi (fun index block -> SourcePath.pageContent pageNumber index, block)
                  yield!
                      page.Footer
                      |> List.mapi (fun index block -> SourcePath.pageFooter pageNumber index, block) ])
            |> List.collect id

    /// Look up a resource by id.
    let tryResource (id: ResourceId) (doc: Document) : Resource option =
        doc.Resources |> List.tryFind (fun r -> r.Id = id)

    /// Find the source location of a node by canonical structural path.
    let trySource path (doc: Document) = doc.Sources |> Map.tryFind path
