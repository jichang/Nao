namespace Nao.Documents

/// Measurement unit for a length in the unified document model.
///
/// Fluid formats (markdown/html/text) lean on `Px`/`Percent`/`Em`; paginated
/// formats (pdf/word/ppt) lean on `Pt`. `Fr` and `Auto` exist for grid tracks.
type Unit =
    /// Device-independent pixels — the natural unit for screen / fluid layout.
    | Px
    /// Typographic points (1/72 inch) — the natural unit for print / pagination.
    | Pt
    /// Relative to the current font size.
    | Em
    /// Percentage of the containing block.
    | Percent
    /// Fractional unit of remaining space — only meaningful for grid tracks.
    | Fr
    /// Automatic / content-derived size.
    | Auto

/// A scalar measurement paired with its unit.
type Length =
    { Value: float
      Unit: Unit }

    static member Px v = { Value = v; Unit = Px }
    static member Pt v = { Value = v; Unit = Pt }
    static member Em v = { Value = v; Unit = Em }
    static member Percent v = { Value = v; Unit = Percent }
    static member Fr v = { Value = v; Unit = Fr }
    /// The "auto" length (value is ignored).
    static member Auto = { Value = 0.0; Unit = Auto }
    /// Zero pixels.
    static member Zero = { Value = 0.0; Unit = Px }

/// An sRGB color stored as a hex string: "#RRGGBB" or "#RRGGBBAA".
///
/// Kept as a string (rather than packed ints) so the model serializes cleanly and
/// stays format-agnostic; consumers parse it however they like.
type Color =
    | Color of string

    member this.Hex = let (Color h) = this in h
    static member Of (hex: string) = Color hex

/// A 2-D point expressed in document units.
type Point = { X: Length; Y: Length }

/// A width/height pair expressed in document units.
type Size = { Width: Length; Height: Length }

/// An axis-aligned rectangle used for absolute positioning.
type Rect =
    { X: Length
      Y: Length
      Width: Length
      Height: Length }

/// Spacing applied to the four sides of a box (used for margin and padding).
type Edges =
    { Top: Length
      Right: Length
      Bottom: Length
      Left: Length }

    /// Same length on all four edges.
    static member All (l: Length) = { Top = l; Right = l; Bottom = l; Left = l }
    /// Vertical (top/bottom) and horizontal (left/right) lengths.
    static member Axes (vertical: Length, horizontal: Length) =
        { Top = vertical; Right = horizontal; Bottom = vertical; Left = horizontal }
    static member Zero = Edges.All Length.Zero

/// Alignment / distribution along a layout axis. A superset that covers both the
/// "self alignment" and "content distribution" cases of flex and grid.
type Align =
    | Start
    | Center
    | End
    | Stretch
    | Baseline
    | SpaceBetween
    | SpaceAround
    | SpaceEvenly
