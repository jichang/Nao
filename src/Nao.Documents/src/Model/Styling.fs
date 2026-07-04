namespace Nao.Documents

/// Font weight, mapped by writers to the closest value a target format supports.
type FontWeight =
    | Thin
    | Light
    | Normal
    | Medium
    | SemiBold
    | Bold
    | Black

/// Upright vs slanted text.
type FontStyleKind =
    | Upright
    | Italic
    | Oblique

/// A line decoration applied to a run of text.
type TextDecoration =
    | Underline
    | LineThrough
    | Overline

/// Horizontal text alignment within a block.
type TextAlign =
    | Left
    | Center
    | Right
    | Justify

/// Character-level styling for a run of text. Every field is optional so a run can
/// inherit from its context and writers only emit what is explicitly set.
type TextStyle =
    { FontFamily: string option
      FontSize: Length option
      Weight: FontWeight option
      Style: FontStyleKind option
      Decorations: TextDecoration list
      Color: Color option
      Background: Color option
      /// Extensible bag for things the model doesn't name (OpenType features,
      /// super/subscript, letter-spacing, ...).
      Features: Map<string, string> }

    /// An empty style — inherit everything from context.
    static member Default =
        { FontFamily = None
          FontSize = None
          Weight = None
          Style = None
          Decorations = []
          Color = None
          Background = None
          Features = Map.empty }

    static member Bold = { TextStyle.Default with Weight = Some Bold }
    static member Italic = { TextStyle.Default with Style = Some Italic }

/// How a box border is drawn.
type BorderStyle =
    | NoBorder
    | Solid
    | Dashed
    | Dotted

/// A box border specification.
type Border =
    { Width: Length
      Color: Color
      Style: BorderStyle
      /// Corner radius (uniform).
      Radius: Length }

/// Block / box-level visual styling: background, spacing, sizing and border.
/// All fields optional for the same inherit-by-default reason as `TextStyle`.
type BoxStyle =
    { Background: Color option
      Border: Border option
      Padding: Edges option
      Margin: Edges option
      Width: Length option
      Height: Length option
      MinWidth: Length option
      MaxWidth: Length option
      /// Default text alignment for inline content inside this box.
      TextAlign: TextAlign option }

    static member Default =
        { Background = None
          Border = None
          Padding = None
          Margin = None
          Width = None
          Height = None
          MinWidth = None
          MaxWidth = None
          TextAlign = None }
