namespace Nao.Documents

/// Main-axis direction of a flex container.
type FlexDirection =
    | Row
    | RowReverse
    | Column
    | ColumnReverse

/// Wrapping behaviour of a flex container.
type FlexWrap =
    | NoWrap
    | Wrap
    | WrapReverse

/// Options for a flexbox-style (1-D) container.
type FlexOptions =
    { Direction: FlexDirection
      Wrap: FlexWrap
      /// Distribution of children along the main axis.
      Justify: Align
      /// Alignment of children along the cross axis.
      AlignItems: Align
      /// Gap between children.
      Gap: Length }

    static member Default =
        { Direction = Row
          Wrap = NoWrap
          Justify = Start
          AlignItems = Stretch
          Gap = Length.Zero }

/// Options for a 2-D grid container. Tracks are sized with `Length` values where
/// `Fr` means a fraction of the free space and `Auto` means content-sized.
type GridOptions =
    { /// Column track sizes, e.g. [Length.Fr 1.0; Length.Px 200.0; Length.Auto].
      Columns: Length list
      /// Explicit row track sizes; empty means rows are created implicitly.
      Rows: Length list
      ColumnGap: Length
      RowGap: Length }

    static member Default =
        { Columns = []
          Rows = []
          ColumnGap = Length.Zero
          RowGap = Length.Zero }

/// How a container arranges its children.
type Layout =
    /// Normal document flow — children stacked in reading order (the fluid default).
    | Flow
    /// Flexbox-style 1-D layout.
    | Flex of FlexOptions
    /// 2-D grid layout with explicit tracks.
    | Grid of GridOptions
    /// Absolute positioning — every child is placed by an explicit `Rect`.
    | Absolute

/// Per-child placement hints, interpreted according to the parent `Layout`.
/// Fields irrelevant to the active layout are simply ignored.
type Placement =
    { // --- Grid ---
      /// 1-based grid column the child starts in.
      Column: int option
      ColumnSpan: int option
      /// 1-based grid row the child starts in.
      Row: int option
      RowSpan: int option
      // --- Flex ---
      Grow: float option
      Shrink: float option
      Basis: Length option
      Order: int option
      // --- Absolute ---
      /// Explicit rectangle within the container (absolute layout only).
      Rect: Rect option
      // --- Any ---
      /// Cross-axis self-alignment override.
      AlignSelf: Align option }

    /// No placement hints — let the layout default-position the child.
    static member None =
        { Column = None
          ColumnSpan = None
          Row = None
          RowSpan = None
          Grow = None
          Shrink = None
          Basis = None
          Order = None
          Rect = None
          AlignSelf = None }

    /// Place a child at an absolute rectangle.
    static member At (rect: Rect) = { Placement.None with Rect = Some rect }
    /// Place a child in a grid cell with an optional span.
    static member Cell (column: int, row: int, ?columnSpan: int, ?rowSpan: int) =
        { Placement.None with
            Column = Some column
            Row = Some row
            ColumnSpan = columnSpan
            RowSpan = rowSpan }
