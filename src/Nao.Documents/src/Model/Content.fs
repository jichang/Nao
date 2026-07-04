namespace Nao.Documents

open System

/// Inline-level content — a run that lives inside a paragraph, heading or table cell.
type Inline =
    /// A styled run of text.
    | Run of text: string * style: TextStyle
    /// A forced line break within a block (not a paragraph break).
    | LineBreak
    /// A hyperlink wrapping inline children.
    | Link of children: Inline list * href: string
    /// An inline image referencing an extracted resource.
    | InlineImage of resource: ResourceId * alt: string option * size: Size option
    /// Inline monospaced code.
    | InlineCode of string
    /// Escape hatch for format-specific inline content the model doesn't capture
    /// (e.g. a raw HTML span, a math expression). `format` identifies the payload.
    | InlineRaw of format: string * data: string

/// Block-level content node. The body of a document is built from these.
type Block =
    /// A paragraph of inline content with an optional alignment override.
    | Paragraph of inlines: Inline list * align: TextAlign option
    /// A heading of the given level (1 = top level).
    | Heading of level: int * inlines: Inline list
    /// An ordered or unordered list.
    | ListBlock of ListSpec
    /// A table.
    | Table of TableSpec
    /// A fenced code block with an optional language hint.
    | CodeBlock of language: string option * code: string
    /// A block quote containing nested blocks.
    | Quote of Block list
    /// A block-level media embed (image / video / audio) referencing a resource.
    | Media of MediaSpec
    /// A layout container holding explicitly-placed children.
    | Container of ContainerSpec
    /// A horizontal rule / section divider.
    | ThematicBreak
    /// Escape hatch for format-specific block content the model doesn't capture.
    | BlockRaw of format: string * data: string

/// An ordered or unordered list.
and ListSpec =
    { Ordered: bool
      /// Starting number for ordered lists.
      Start: int option
      Items: ListItem list }

/// A single list item, which may contain arbitrary nested blocks.
and ListItem = { Content: Block list }

/// A table with optional header row and per-column hints.
and TableSpec =
    { Columns: TableColumn list
      Header: TableRow option
      Rows: TableRow list
      Style: BoxStyle option }

/// Column-level sizing / alignment hints.
and TableColumn =
    { Width: Length option
      Align: TextAlign option }

/// A table row.
and TableRow = { Cells: TableCell list }

/// A table cell holding nested blocks, with optional spans and styling.
and TableCell =
    { Content: Block list
      ColSpan: int
      RowSpan: int
      Style: BoxStyle option }

/// A block-level media embed.
and MediaSpec =
    { Resource: ResourceId
      Alt: string option
      /// Optional caption rendered with the media.
      Caption: Inline list
      /// Display size override; `None` uses the resource's intrinsic size.
      Size: Size option }

/// A layout container: a box with a `Layout` and a list of placed children.
/// This is how grid / flex / absolute positioning enters the document tree.
and ContainerSpec =
    { Layout: Layout
      Children: LayoutChild list
      Style: BoxStyle option }

/// A child of a container, pairing its content with placement hints.
and LayoutChild =
    { Content: Block
      Placement: Placement }

// --- Helpers for building common nodes -------------------------------------------

/// Convenience constructors for inline and block content.
[<RequireQualifiedAccess>]
module Doc =
    /// A plain unstyled run.
    let text (s: string) = Run(s, TextStyle.Default)
    /// A bold run.
    let bold (s: string) = Run(s, TextStyle.Bold)
    /// A paragraph from a single plain string.
    let para (s: string) = Paragraph([ text s ], None)
    /// A paragraph from inline content.
    let paraOf (inlines: Inline list) = Paragraph(inlines, None)
    /// A heading from a plain string.
    let heading (level: int) (s: string) = Heading(level, [ text s ])
    /// An unordered list from plain-string items.
    let bullets (items: string list) =
        ListBlock
            { Ordered = false
              Start = None
              Items = items |> List.map (fun s -> { Content = [ para s ] }) }
