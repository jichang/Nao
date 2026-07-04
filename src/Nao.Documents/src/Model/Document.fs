namespace Nao.Documents

open System

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
    { Setup: PageSetup
      /// Repeating header content (running head).
      Header: Block list
      /// Repeating footer content (page numbers, etc.).
      Footer: Block list
      /// The page body. Typically a single `Container` for precise layout, but any
      /// list of blocks is allowed.
      Content: Block list }

    static member Of (setup: PageSetup, content: Block list) =
        { Setup = setup; Header = []; Footer = []; Content = content }

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
    { Title: string option
      Authors: string list
      /// BCP-47 language tag, e.g. "en", "zh-CN".
      Language: string option
      Created: DateTimeOffset option
      Modified: DateTimeOffset option
      /// Free-form extra properties (subject, keywords, producer, source app, ...).
      Properties: Map<string, string> }

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
    { /// Schema/version marker so consumers can evolve the model safely.
      Schema: string
      Metadata: DocumentMetadata
      /// Extracted media / binary assets, referenced by id from the body.
      Resources: Resource list
      /// The content, either fluid or paginated.
      Body: Body
      /// Page setup a paginated *target* should use when the source was fluid.
      DefaultPage: PageSetup option }

    /// Current schema identifier.
    static member CurrentSchema = "nao-doc/1"

    /// An empty fluid document.
    static member Empty =
        { Schema = Document.CurrentSchema
          Metadata = DocumentMetadata.Empty
          Resources = []
          Body = Fluid []
          DefaultPage = None }

    /// A fluid document from a list of blocks.
    static member OfBlocks (blocks: Block list) =
        { Document.Empty with Body = Fluid blocks }

    /// A paginated document from a list of pages.
    static member OfPages (pages: Page list) =
        { Document.Empty with Body = Paged pages }

[<RequireQualifiedAccess>]
module Document =

    /// All top-level blocks of a document as a single stream, flattening pages
    /// (and their headers/footers) in order. Useful for fluid writers.
    let toBlocks (doc: Document) : Block list =
        match doc.Body with
        | Fluid blocks -> blocks
        | Paged pages ->
            pages
            |> List.collect (fun p -> p.Header @ p.Content @ p.Footer)

    /// Look up a resource by id.
    let tryResource (id: ResourceId) (doc: Document) : Resource option =
        doc.Resources |> List.tryFind (fun r -> r.Id = id)
