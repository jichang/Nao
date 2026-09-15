namespace Nao.Documents

/// Typed constructors for document metadata and all its attributes.
[<RequireQualifiedAccess>]
module MetadataBuilder =
    let create title authors language created modified properties : DocumentMetadata =
        { Title = title
          Authors = authors
          Language = language
          Created = created
          Modified = modified
          Properties = properties }

/// Typed constructors and functional setters for resource entities.
[<RequireQualifiedAccess>]
module ResourceBuilder =
    let create id kind mediaType : Resource =
        { Id = id
          Kind = kind
          MediaType = mediaType
          LocalPath = None
          Uri = None
          Sha256 = None
          PixelWidth = None
          PixelHeight = None
          DurationMs = None
          Metadata = Map.empty }

    let localPath value (resource: Resource) = { resource with LocalPath = value }
    let uri value (resource: Resource) = { resource with Uri = value }
    let sha256 value (resource: Resource) = { resource with Sha256 = value }
    let pixelWidth value (resource: Resource) = { resource with PixelWidth = value }
    let pixelHeight value (resource: Resource) = { resource with PixelHeight = value }
    let durationMs value (resource: Resource) = { resource with DurationMs = value }
    let metadata value (resource: Resource) = { resource with Metadata = value }

    let metadataItem name value (resource: Resource) =
        { resource with
            Metadata = resource.Metadata |> Map.add name value }

    let removeMetadataItem name (resource: Resource) =
        { resource with
            Metadata = resource.Metadata |> Map.remove name }

/// Typed constructors for border entities.
[<RequireQualifiedAccess>]
module BorderBuilder =
    let create width color style radius : Border =
        { Width = width
          Color = color
          Style = style
          Radius = radius }

/// Typed constructors for layout options.
[<RequireQualifiedAccess>]
module LayoutOptionsBuilder =
    let flex direction wrap justify alignItems gap : FlexOptions =
        { Direction = direction
          Wrap = wrap
          Justify = justify
          AlignItems = alignItems
          Gap = gap }

    let grid columns rows columnGap rowGap : GridOptions =
        { Columns = columns
          Rows = rows
          ColumnGap = columnGap
          RowGap = rowGap }

/// Typed constructors for page setup, pages, and all their attributes.
[<RequireQualifiedAccess>]
module PageBuilder =
    let setup width height orientation margins : PageSetup =
        { Width = width
          Height = height
          Orientation = orientation
          Margins = margins }

    let create setup header footer content : Page =
        { Setup = setup
          Header = header
          Footer = footer
          Content = content }

    let withSetup value (page: Page) = { page with Setup = value }
    let withHeader value (page: Page) = { page with Header = value }
    let withFooter value (page: Page) = { page with Footer = value }
    let withContent value (page: Page) = { page with Content = value }

/// Typed constructors for complete fluid and paged documents.
[<RequireQualifiedAccess>]
module DocumentBuilder =
    let create schema metadata resources body defaultPage : Document =
        { Schema = schema
          Metadata = metadata
          Resources = resources
          Body = body
          Sources = Map.empty
          DefaultPage = defaultPage }

    let fluid metadata resources blocks defaultPage =
        create Document.CurrentSchema metadata resources (Fluid blocks) defaultPage

    let paged metadata resources pages defaultPage =
        create Document.CurrentSchema metadata resources (Paged pages) defaultPage

/// Typed constructors for every inline entity in the canonical model.
[<RequireQualifiedAccess>]
module InlineBuilder =
    let run text style = Run(text, style)
    let text value = Run(value, TextStyle.Default)
    let lineBreak = LineBreak
    let link children href = Link(children, href)
    let image resource alt size = InlineImage(resource, alt, size)
    let code value = InlineCode value
    let raw format data = InlineRaw(format, data)

/// Typed constructors for list entities and all their attributes.
[<RequireQualifiedAccess>]
module ListBuilder =
    let item content : ListItem = { Content = content }

    let create ordered start items : ListSpec =
        { Ordered = ordered
          Start = start
          Items = items }

/// Typed constructors for table entities and all their attributes.
[<RequireQualifiedAccess>]
module TableBuilder =
    let column width align : TableColumn = { Width = width; Align = align }

    let cell content colSpan rowSpan style : TableCell =
        { Content = content
          ColSpan = colSpan
          RowSpan = rowSpan
          Style = style }

    let row cells : TableRow = { Cells = cells }

    let create columns header rows style : TableSpec =
        { Columns = columns
          Header = header
          Rows = rows
          Style = style }

/// Typed constructors for media entities and all their attributes.
[<RequireQualifiedAccess>]
module MediaBuilder =
    let create resource alt caption size : MediaSpec =
        { Resource = resource
          Alt = alt
          Caption = caption
          Size = size }

/// Typed constructors for layout entities and all their attributes.
[<RequireQualifiedAccess>]
module LayoutBuilder =
    let child content placement : LayoutChild =
        { Content = content
          Placement = placement }

    let container layout children style : ContainerSpec =
        { Layout = layout
          Children = children
          Style = style }

/// Typed constructors for every block entity in the canonical model.
[<RequireQualifiedAccess>]
module BlockBuilder =
    let paragraph inlines align = Paragraph(inlines, align)
    let heading level inlines = Heading(level, inlines)
    let list spec = ListBlock spec
    let table spec = Table spec
    let code language value = CodeBlock(language, value)
    let quote blocks = Quote blocks
    let media spec = Media spec
    let container spec = Container spec
    let thematicBreak = ThematicBreak
    let raw format data = BlockRaw(format, data)

/// Functional setters for every character-style attribute.
[<RequireQualifiedAccess>]
module TextStyleBuilder =
    let empty = TextStyle.Default
    let fontFamily value (style: TextStyle) = { style with FontFamily = value }
    let fontSize value (style: TextStyle) = { style with FontSize = value }
    let weight value (style: TextStyle) = { style with Weight = value }
    let fontStyle value (style: TextStyle) = { style with Style = value }
    let decorations value (style: TextStyle) = { style with Decorations = value }
    let color value (style: TextStyle) = { style with Color = value }
    let background value (style: TextStyle) = { style with Background = value }
    let features value (style: TextStyle) = { style with Features = value }

    let feature name value (style: TextStyle) =
        { style with
            Features = style.Features |> Map.add name value }

    let removeFeature name (style: TextStyle) =
        { style with
            Features = style.Features |> Map.remove name }

/// Functional setters for every box-style attribute.
[<RequireQualifiedAccess>]
module BoxStyleBuilder =
    let empty = BoxStyle.Default
    let background value (style: BoxStyle) = { style with Background = value }
    let border value (style: BoxStyle) = { style with Border = value }
    let padding value (style: BoxStyle) = { style with Padding = value }
    let margin value (style: BoxStyle) = { style with Margin = value }
    let width value (style: BoxStyle) = { style with Width = value }
    let height value (style: BoxStyle) = { style with Height = value }
    let minWidth value (style: BoxStyle) = { style with MinWidth = value }
    let maxWidth value (style: BoxStyle) = { style with MaxWidth = value }
    let textAlign value (style: BoxStyle) = { style with TextAlign = value }

/// Functional setters for every child-placement attribute.
[<RequireQualifiedAccess>]
module PlacementBuilder =
    let empty = Placement.None
    let column value (placement: Placement) = { placement with Column = value }
    let columnSpan value (placement: Placement) = { placement with ColumnSpan = value }
    let row value (placement: Placement) = { placement with Row = value }
    let rowSpan value (placement: Placement) = { placement with RowSpan = value }
    let grow value (placement: Placement) = { placement with Grow = value }
    let shrink value (placement: Placement) = { placement with Shrink = value }
    let basis value (placement: Placement) = { placement with Basis = value }
    let order value (placement: Placement) = { placement with Order = value }
    let rect value (placement: Placement) = { placement with Rect = value }
    let alignSelf value (placement: Placement) = { placement with AlignSelf = value }
