namespace Nao.Documents

open System
open System.IO
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging

// F# cannot abbreviate a namespace as a module, so the OOXML schema types are exposed
// through short alias modules. This keeps the writers readable (W.Paragraph, S.Cell,
// P.Slide, D.Run) without `open`ing namespaces that would clash with the unified
// model's own `Run`, `Paragraph`, `Table`, `Color`, ... names.
module W =
    type RunProperties = DocumentFormat.OpenXml.Wordprocessing.RunProperties
    type Bold = DocumentFormat.OpenXml.Wordprocessing.Bold
    type Italic = DocumentFormat.OpenXml.Wordprocessing.Italic
    type Underline = DocumentFormat.OpenXml.Wordprocessing.Underline
    type UnderlineValues = DocumentFormat.OpenXml.Wordprocessing.UnderlineValues
    type Strike = DocumentFormat.OpenXml.Wordprocessing.Strike
    type Color = DocumentFormat.OpenXml.Wordprocessing.Color
    type Text = DocumentFormat.OpenXml.Wordprocessing.Text
    type Run = DocumentFormat.OpenXml.Wordprocessing.Run
    type Break = DocumentFormat.OpenXml.Wordprocessing.Break
    type RunFonts = DocumentFormat.OpenXml.Wordprocessing.RunFonts
    type Paragraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph
    type ParagraphProperties = DocumentFormat.OpenXml.Wordprocessing.ParagraphProperties
    type ParagraphStyleId = DocumentFormat.OpenXml.Wordprocessing.ParagraphStyleId
    type Indentation = DocumentFormat.OpenXml.Wordprocessing.Indentation
    type Justification = DocumentFormat.OpenXml.Wordprocessing.Justification
    type JustificationValues = DocumentFormat.OpenXml.Wordprocessing.JustificationValues
    type Body = DocumentFormat.OpenXml.Wordprocessing.Body
    type Table = DocumentFormat.OpenXml.Wordprocessing.Table
    type TableProperties = DocumentFormat.OpenXml.Wordprocessing.TableProperties
    type TableStyle = DocumentFormat.OpenXml.Wordprocessing.TableStyle
    type TableBorders = DocumentFormat.OpenXml.Wordprocessing.TableBorders
    type BorderType = DocumentFormat.OpenXml.Wordprocessing.BorderType
    type TopBorder = DocumentFormat.OpenXml.Wordprocessing.TopBorder
    type LeftBorder = DocumentFormat.OpenXml.Wordprocessing.LeftBorder
    type BottomBorder = DocumentFormat.OpenXml.Wordprocessing.BottomBorder
    type RightBorder = DocumentFormat.OpenXml.Wordprocessing.RightBorder
    type InsideHorizontalBorder = DocumentFormat.OpenXml.Wordprocessing.InsideHorizontalBorder
    type InsideVerticalBorder = DocumentFormat.OpenXml.Wordprocessing.InsideVerticalBorder
    type BorderValues = DocumentFormat.OpenXml.Wordprocessing.BorderValues
    type TableRow = DocumentFormat.OpenXml.Wordprocessing.TableRow
    type TableCell = DocumentFormat.OpenXml.Wordprocessing.TableCell
    type TableCellProperties = DocumentFormat.OpenXml.Wordprocessing.TableCellProperties
    type GridSpan = DocumentFormat.OpenXml.Wordprocessing.GridSpan
    type ParagraphBorders = DocumentFormat.OpenXml.Wordprocessing.ParagraphBorders
    type Styles = DocumentFormat.OpenXml.Wordprocessing.Styles
    type Style = DocumentFormat.OpenXml.Wordprocessing.Style
    type StyleValues = DocumentFormat.OpenXml.Wordprocessing.StyleValues
    type StyleName = DocumentFormat.OpenXml.Wordprocessing.StyleName
    type StyleRunProperties = DocumentFormat.OpenXml.Wordprocessing.StyleRunProperties
    type SectionProperties = DocumentFormat.OpenXml.Wordprocessing.SectionProperties
    type PageSize = DocumentFormat.OpenXml.Wordprocessing.PageSize
    type PageMargin = DocumentFormat.OpenXml.Wordprocessing.PageMargin
    type Document = DocumentFormat.OpenXml.Wordprocessing.Document
    type TabChar = DocumentFormat.OpenXml.Wordprocessing.TabChar

module S =
    type Cell = DocumentFormat.OpenXml.Spreadsheet.Cell
    type CellValue = DocumentFormat.OpenXml.Spreadsheet.CellValue
    type CellValues = DocumentFormat.OpenXml.Spreadsheet.CellValues
    type InlineString = DocumentFormat.OpenXml.Spreadsheet.InlineString
    type Text = DocumentFormat.OpenXml.Spreadsheet.Text
    type Sheets = DocumentFormat.OpenXml.Spreadsheet.Sheets
    type Sheet = DocumentFormat.OpenXml.Spreadsheet.Sheet
    type Workbook = DocumentFormat.OpenXml.Spreadsheet.Workbook
    type SheetData = DocumentFormat.OpenXml.Spreadsheet.SheetData
    type Row = DocumentFormat.OpenXml.Spreadsheet.Row
    type Worksheet = DocumentFormat.OpenXml.Spreadsheet.Worksheet

module P =
    type Shape = DocumentFormat.OpenXml.Presentation.Shape
    type NonVisualShapeProperties = DocumentFormat.OpenXml.Presentation.NonVisualShapeProperties
    type NonVisualDrawingProperties = DocumentFormat.OpenXml.Presentation.NonVisualDrawingProperties
    type NonVisualShapeDrawingProperties = DocumentFormat.OpenXml.Presentation.NonVisualShapeDrawingProperties
    type ApplicationNonVisualDrawingProperties = DocumentFormat.OpenXml.Presentation.ApplicationNonVisualDrawingProperties
    type PlaceholderShape = DocumentFormat.OpenXml.Presentation.PlaceholderShape
    type PlaceholderValues = DocumentFormat.OpenXml.Presentation.PlaceholderValues
    type ShapeProperties = DocumentFormat.OpenXml.Presentation.ShapeProperties
    type TextBody = DocumentFormat.OpenXml.Presentation.TextBody
    type ShapeTree = DocumentFormat.OpenXml.Presentation.ShapeTree
    type NonVisualGroupShapeProperties = DocumentFormat.OpenXml.Presentation.NonVisualGroupShapeProperties
    type NonVisualGroupShapeDrawingProperties = DocumentFormat.OpenXml.Presentation.NonVisualGroupShapeDrawingProperties
    type GroupShapeProperties = DocumentFormat.OpenXml.Presentation.GroupShapeProperties
    type CommonSlideData = DocumentFormat.OpenXml.Presentation.CommonSlideData
    type Slide = DocumentFormat.OpenXml.Presentation.Slide
    type SlideMaster = DocumentFormat.OpenXml.Presentation.SlideMaster
    type SlideLayout = DocumentFormat.OpenXml.Presentation.SlideLayout
    type Presentation = DocumentFormat.OpenXml.Presentation.Presentation
    type SlideMasterIdList = DocumentFormat.OpenXml.Presentation.SlideMasterIdList
    type SlideMasterId = DocumentFormat.OpenXml.Presentation.SlideMasterId
    type SlideIdList = DocumentFormat.OpenXml.Presentation.SlideIdList
    type SlideId = DocumentFormat.OpenXml.Presentation.SlideId
    type SlideSize = DocumentFormat.OpenXml.Presentation.SlideSize
    type NotesSize = DocumentFormat.OpenXml.Presentation.NotesSize

module D =
    type ShapeLocks = DocumentFormat.OpenXml.Drawing.ShapeLocks
    type BodyProperties = DocumentFormat.OpenXml.Drawing.BodyProperties
    type ListStyle = DocumentFormat.OpenXml.Drawing.ListStyle
    type Paragraph = DocumentFormat.OpenXml.Drawing.Paragraph
    type ParagraphProperties = DocumentFormat.OpenXml.Drawing.ParagraphProperties
    type Run = DocumentFormat.OpenXml.Drawing.Run
    type RunProperties = DocumentFormat.OpenXml.Drawing.RunProperties
    type Text = DocumentFormat.OpenXml.Drawing.Text
    type TransformGroup = DocumentFormat.OpenXml.Drawing.TransformGroup
    type Offset = DocumentFormat.OpenXml.Drawing.Offset
    type Extents = DocumentFormat.OpenXml.Drawing.Extents
    type ChildOffset = DocumentFormat.OpenXml.Drawing.ChildOffset
    type ChildExtents = DocumentFormat.OpenXml.Drawing.ChildExtents
    type Theme = DocumentFormat.OpenXml.Drawing.Theme

/// Tiny helpers shared by the OOXML writers for wrapping primitive values in the
/// SDK's typed value classes (F# does not apply the SDK's implicit conversions).
[<AutoOpen>]
module private OoxmlValues =
    let inline ev (v: 'a when 'a: struct) = EnumValue<'a>(v)
    let sv (s: string) = StringValue s
    let u32 (v: uint32) = UInt32Value v
    let i32 (v: int) = Int32Value v
    let i64 (v: int64) = Int64Value v
    let bv (x: bool) = BooleanValue x
    /// Wrap a single child element. A one-argument OOXML constructor binds to the
    /// `IEnumerable<OpenXmlElement>` overload (every element is enumerable over its
    /// own children), which would re-parent the child's children instead of nesting
    /// the child. Appending explicitly avoids that overload.
    let inline w1 (parent: 'p when 'p :> OpenXmlElement) (child: #OpenXmlElement) : 'p =
        parent.AppendChild(child :> OpenXmlElement) |> ignore
        parent

/// Shared helpers for the Office Open XML writers.
[<RequireQualifiedAccess>]
module internal Ooxml =

    /// A six-hex-digit colour (no leading '#') for OOXML colour attributes.
    let hex6 (c: Color) =
        let h = c.Hex.TrimStart('#')
        if h.Length >= 6 then h.Substring(0, 6) else h.PadRight(6, '0')


/// Reader and writer for Word documents (`.docx`,
/// `application/vnd.openxmlformats-officedocument.wordprocessingml.document`),
/// backed by the DocumentFormat.OpenXml SDK. The SDK owns the package, content-types
/// and relationships, so there is no hand-written ZIP/XML plumbing to maintain.
module Docx =

    [<Literal>]
    let MediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"

    // --- Writer -------------------------------------------------------------------

    let private runProps (style: TextStyle) : W.RunProperties option =
        let rp = W.RunProperties()
        let mutable any = false
        match style.Weight with
        | Some(Bold | Black | SemiBold) ->
            rp.AppendChild(W.Bold()) |> ignore
            any <- true
        | _ -> ()
        match style.Style with
        | Some(Italic | Oblique) ->
            rp.AppendChild(W.Italic()) |> ignore
            any <- true
        | _ -> ()
        if style.Decorations |> List.contains Underline then
            rp.AppendChild(W.Underline(Val = ev W.UnderlineValues.Single)) |> ignore
            any <- true
        if style.Decorations |> List.contains LineThrough then
            rp.AppendChild(W.Strike()) |> ignore
            any <- true
        match style.Color with
        | Some c ->
            rp.AppendChild(W.Color(Val = sv (Ooxml.hex6 c))) |> ignore
            any <- true
        | None -> ()
        if any then Some rp else None

    let private mkText (s: string) =
        let t = W.Text(s)
        t.Space <- ev SpaceProcessingModeValues.Preserve
        t

    let private makeRun (rPr: W.RunProperties option) (children: OpenXmlElement list) =
        let r = W.Run()
        rPr |> Option.iter (fun p -> r.AppendChild p |> ignore)
        children |> List.iter (fun c -> r.AppendChild c |> ignore)
        r

    let private textRun (style: TextStyle) (text: string) =
        makeRun (runProps style) [ mkText text :> OpenXmlElement ]

    let private codeRun (code: string) =
        let rp = W.RunProperties()
        rp.AppendChild(W.RunFonts(Ascii = sv "Consolas", HighAnsi = sv "Consolas")) |> ignore
        makeRun (Some rp) [ mkText code :> OpenXmlElement ]

    let private linkRun (text: string) =
        let rp = W.RunProperties()
        rp.AppendChild(W.Color(Val = sv "0563C1")) |> ignore
        rp.AppendChild(W.Underline(Val = ev W.UnderlineValues.Single)) |> ignore
        makeRun (Some rp) [ mkText text :> OpenXmlElement ]

    let rec private renderRun (i: Inline) : W.Run list =
        match i with
        | Run(text, style) -> [ textRun style text ]
        | LineBreak -> [ makeRun None [ W.Break() :> OpenXmlElement ] ]
        | Link(children, href) -> (children |> List.collect renderRun) @ [ linkRun (sprintf " (%s)" href) ]
        | InlineImage(_, alt, _) -> [ textRun TextStyle.Default (defaultArg alt "[image]") ]
        | InlineCode code -> [ codeRun code ]
        | InlineRaw(_, data) -> [ textRun TextStyle.Default data ]

    let private renderRuns (inlines: Inline list) : OpenXmlElement list =
        inlines |> List.collect renderRun |> List.map (fun r -> r :> OpenXmlElement)

    let private makePara (pPr: W.ParagraphProperties option) (children: OpenXmlElement list) =
        let p = W.Paragraph()
        pPr |> Option.iter (fun pp -> p.AppendChild pp |> ignore)
        children |> List.iter (fun c -> p.AppendChild c |> ignore)
        p

    let private pPrStyle (styleId: string) =
        let pp = W.ParagraphProperties()
        pp.AppendChild(W.ParagraphStyleId(Val = sv styleId)) |> ignore
        pp

    let private indentPPr (left: int) =
        let pp = W.ParagraphProperties()
        pp.AppendChild(W.Indentation(Left = sv (string left))) |> ignore
        pp

    let private jcProps (a: TextAlign) : W.ParagraphProperties option =
        let v =
            match a with
            | Center -> Some W.JustificationValues.Center
            | Right -> Some W.JustificationValues.Right
            | Justify -> Some W.JustificationValues.Both
            | Left -> None
        v
        |> Option.map (fun jv ->
            let pp = W.ParagraphProperties()
            pp.AppendChild(W.Justification(Val = ev jv)) |> ignore
            pp)

    let rec private renderBlock (block: Block) : OpenXmlElement list =
        match block with
        | Heading(level, inlines) ->
            let l = max 1 (min 6 level)
            [ makePara (Some(pPrStyle (sprintf "Heading%d" l))) (renderRuns inlines) :> OpenXmlElement ]
        | Paragraph(inlines, align) ->
            let pPr = align |> Option.bind jcProps
            [ makePara pPr (renderRuns inlines) :> OpenXmlElement ]
        | ListBlock spec ->
            spec.Items
            |> List.mapi (fun idx item ->
                let marker =
                    if spec.Ordered then sprintf "%d. " (idx + defaultArg spec.Start 1) else "\u2022 "
                let runs =
                    match item.Content with
                    | Paragraph(inlines, _) :: _ -> textRun TextStyle.Default marker :: (inlines |> List.collect renderRun)
                    | _ -> [ textRun TextStyle.Default marker ]
                makePara (Some(indentPPr 360)) (runs |> List.map (fun r -> r :> OpenXmlElement)) :> OpenXmlElement)
        | CodeBlock(_, code) ->
            code.Replace("\r\n", "\n").Split('\n')
            |> Array.map (fun ln ->
                makePara (Some(pPrStyle "Code")) [ codeRun ln :> OpenXmlElement ] :> OpenXmlElement)
            |> Array.toList
        | Quote blocks ->
            blocks
            |> List.collect (fun b ->
                match b with
                | Paragraph(inlines, _) -> [ makePara (Some(pPrStyle "Quote")) (renderRuns inlines) :> OpenXmlElement ]
                | other -> renderBlock other)
        | Media spec ->
            [ makePara None [ textRun TextStyle.Default ("[" + defaultArg spec.Alt "media" + "]") :> OpenXmlElement ]
              :> OpenXmlElement ]
        | Table spec -> [ renderTable spec :> OpenXmlElement ]
        | Container spec -> spec.Children |> List.collect (fun c -> renderBlock c.Content)
        | ThematicBreak ->
            let pp = W.ParagraphProperties()
            let borders = W.ParagraphBorders()
            borders.AppendChild(W.BottomBorder(Val = ev W.BorderValues.Single, Size = u32 6u, Space = u32 1u, Color = sv "auto"))
            |> ignore
            pp.AppendChild borders |> ignore
            [ makePara (Some pp) [] :> OpenXmlElement ]
        | BlockRaw(_, data) ->
            [ makePara None [ textRun TextStyle.Default data :> OpenXmlElement ] :> OpenXmlElement ]

    and private renderTable (spec: TableSpec) : W.Table =
        let tbl = W.Table()
        let tblPr = W.TableProperties()
        tblPr.AppendChild(W.TableStyle(Val = sv "TableGrid")) |> ignore
        let borders = W.TableBorders()
        let mk (b: #W.BorderType) =
            b.Val <- ev W.BorderValues.Single
            b.Size <- u32 4u
            b.Space <- u32 0u
            b.Color <- sv "auto"
            b
        borders.AppendChild(mk (W.TopBorder())) |> ignore
        borders.AppendChild(mk (W.LeftBorder())) |> ignore
        borders.AppendChild(mk (W.BottomBorder())) |> ignore
        borders.AppendChild(mk (W.RightBorder())) |> ignore
        borders.AppendChild(mk (W.InsideHorizontalBorder())) |> ignore
        borders.AppendChild(mk (W.InsideVerticalBorder())) |> ignore
        tblPr.AppendChild borders |> ignore
        tbl.AppendChild tblPr |> ignore
        let renderRow (row: TableRow) =
            let tr = W.TableRow()
            for c in row.Cells do
                let tc = W.TableCell()
                if c.ColSpan > 1 then
                    let tcPr = W.TableCellProperties()
                    tcPr.AppendChild(W.GridSpan(Val = i32 c.ColSpan)) |> ignore
                    tc.AppendChild tcPr |> ignore
                let content = c.Content |> List.collect renderBlock
                let content = if content.IsEmpty then [ W.Paragraph() :> OpenXmlElement ] else content
                content |> List.iter (fun el -> tc.AppendChild el |> ignore)
                tr.AppendChild tc |> ignore
            tr
        spec.Header |> Option.iter (fun h -> tbl.AppendChild(renderRow h) |> ignore)
        spec.Rows |> List.iter (fun r -> tbl.AppendChild(renderRow r) |> ignore)
        tbl

    let private buildStyles () =
        let styles = W.Styles()
        let normal =
            W.Style(Type = ev W.StyleValues.Paragraph, StyleId = sv "Normal", Default = OnOffValue.FromBoolean true)
        normal.AppendChild(W.StyleName(Val = sv "Normal")) |> ignore
        styles.AppendChild normal |> ignore
        for n in 1..6 do
            let st = W.Style(Type = ev W.StyleValues.Paragraph, StyleId = sv (sprintf "Heading%d" n))
            st.AppendChild(W.StyleName(Val = sv (sprintf "heading %d" n))) |> ignore
            let rpr = W.StyleRunProperties()
            rpr.AppendChild(W.Bold()) |> ignore
            st.AppendChild rpr |> ignore
            styles.AppendChild st |> ignore
        let code = W.Style(Type = ev W.StyleValues.Paragraph, StyleId = sv "Code")
        code.AppendChild(W.StyleName(Val = sv "Code")) |> ignore
        let crpr = W.StyleRunProperties()
        crpr.AppendChild(W.RunFonts(Ascii = sv "Consolas", HighAnsi = sv "Consolas")) |> ignore
        code.AppendChild crpr |> ignore
        styles.AppendChild code |> ignore
        let quote = W.Style(Type = ev W.StyleValues.Paragraph, StyleId = sv "Quote")
        quote.AppendChild(W.StyleName(Val = sv "Quote")) |> ignore
        let qrpr = W.StyleRunProperties()
        qrpr.AppendChild(W.Italic()) |> ignore
        quote.AppendChild qrpr |> ignore
        styles.AppendChild quote |> ignore
        styles

    /// Writes a `Document` as a `.docx` package.
    type Writer() =
        interface IDocumentWriter with
            member _.MediaTypes = [ MediaType ]
            member _.Write(doc, output, _ctx) =
                use mem = new MemoryStream()
                using (WordprocessingDocument.Create(mem, WordprocessingDocumentType.Document)) (fun wd ->
                    let mainPart = wd.AddMainDocumentPart()
                    let stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>()
                    stylesPart.Styles <- buildStyles ()

                    let titleBlocks =
                        match doc.Metadata.Title with
                        | Some t -> [ Heading(1, [ Run(t, TextStyle.Default) ]) ]
                        | None -> []

                    let body = W.Body()
                    (titleBlocks @ Document.toBlocks doc)
                    |> List.collect renderBlock
                    |> List.iter (fun el -> body.AppendChild el |> ignore)

                    let sectPr = W.SectionProperties()
                    sectPr.AppendChild(W.PageSize(Width = u32 11906u, Height = u32 16838u)) |> ignore
                    sectPr.AppendChild(
                        W.PageMargin(
                            Top = i32 1440,
                            Right = u32 1440u,
                            Bottom = i32 1440,
                            Left = u32 1440u,
                            Header = u32 720u,
                            Footer = u32 720u,
                            Gutter = u32 0u))
                    |> ignore
                    body.AppendChild sectPr |> ignore

                    mainPart.Document <- w1 (W.Document()) body)
                mem.Position <- 0L
                mem.CopyTo output

    // --- Reader -------------------------------------------------------------------

    let private runStyle (r: W.Run) =
        match r.RunProperties with
        | null -> TextStyle.Default
        | rPr ->
            { TextStyle.Default with
                Weight = (if not (isNull rPr.Bold) then Some Bold else None)
                Style = (if not (isNull rPr.Italic) then Some Italic else None)
                Decorations =
                    [ if not (isNull rPr.Underline) then Underline
                      if not (isNull rPr.Strike) then LineThrough ] }

    let private paraInlines (p: W.Paragraph) : Inline list =
        p.Elements<W.Run>()
        |> Seq.collect (fun r ->
            let style = runStyle r
            r.ChildElements
            |> Seq.collect (fun el ->
                match el with
                | :? W.Text as t -> [ Run(t.Text, style) ]
                | :? W.Break -> [ LineBreak ]
                | :? W.TabChar -> [ Run("\t", style) ]
                | _ -> []))
        |> List.ofSeq

    let private headingLevel (p: W.Paragraph) =
        match p.ParagraphProperties with
        | null -> None
        | pPr ->
            match pPr.ParagraphStyleId with
            | null -> None
            | sid when not (isNull sid.Val) && sid.Val.Value.StartsWith "Heading" ->
                match Int32.TryParse(sid.Val.Value.Substring 7) with
                | true, n -> Some n
                | _ -> None
            | _ -> None

    let private parseTable (tbl: W.Table) =
        let rows =
            tbl.Elements<W.TableRow>()
            |> Seq.map (fun tr ->
                { Cells =
                    tr.Elements<W.TableCell>()
                    |> Seq.map (fun tc ->
                        let blocks =
                            tc.Elements<W.Paragraph>()
                            |> Seq.map (fun p -> Paragraph(paraInlines p, None))
                            |> List.ofSeq
                        { Content = blocks; ColSpan = 1; RowSpan = 1; Style = None })
                    |> List.ofSeq })
            |> List.ofSeq
        let header, body =
            match rows with
            | h :: rest -> Some h, rest
            | [] -> None, []
        let columns =
            let count = header |> Option.map (fun h -> h.Cells.Length) |> Option.defaultValue 0
            List.replicate count { Width = None; Align = None }
        Table { Columns = columns; Header = header; Rows = body; Style = None }

    /// Reads a `.docx` package into a fluid `Document` (paragraphs, headings, tables).
    type Reader() =
        interface IDocumentReader with
            member _.MediaTypes = [ MediaType ]
            member _.Read(input, _ctx) =
                use mem = new MemoryStream()
                input.CopyTo mem
                mem.Position <- 0L
                use wd = WordprocessingDocument.Open(mem, false)
                match wd.MainDocumentPart with
                | null -> Document.Empty
                | main ->
                    match main.Document with
                    | null -> Document.Empty
                    | document ->
                        match document.Body with
                        | null -> Document.Empty
                        | body ->
                            let blocks =
                                body.ChildElements
                                |> Seq.choose (fun el ->
                                    match el with
                                    | :? W.Paragraph as p ->
                                        match headingLevel p with
                                        | Some n -> Some(Heading(n, paraInlines p))
                                        | None -> Some(Paragraph(paraInlines p, None))
                                    | :? W.Table as t -> Some(parseTable t)
                                    | _ -> None)
                                |> List.ofSeq
                            Document.OfBlocks blocks


/// Writer for Excel workbooks (`.xlsx`,
/// `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`), backed by the
/// DocumentFormat.OpenXml SDK. The document is flattened to rows on a single worksheet.
/// There is no reader (spreadsheets are not a natural source for the prose model).
module Xlsx =

    [<Literal>]
    let MediaType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"

    /// 0-based column index → spreadsheet column name (A, B, ..., Z, AA, ...).
    let private colName (index: int) =
        let chars = ResizeArray<char>()
        let mutable v = index + 1
        while v > 0 do
            let r = (v - 1) % 26
            chars.Add(char (int 'A' + r))
            v <- (v - 1) / 26
        chars |> Seq.rev |> Seq.toArray |> String

    let private makeCell (col: int) (row: int) (value: string) : S.Cell =
        let cellRef = colName col + string row
        let c = S.Cell(CellReference = sv cellRef)
        match Double.TryParse value with
        | true, _ when value.Trim() <> "" ->
            c.DataType <- ev S.CellValues.Number
            c.CellValue <- S.CellValue(value.Trim())
        | _ ->
            c.DataType <- ev S.CellValues.InlineString
            let inlineStr = S.InlineString()
            let t = S.Text(value)
            t.Space <- ev SpaceProcessingModeValues.Preserve
            inlineStr.AppendChild t |> ignore
            c.InlineString <- inlineStr
        c

    /// Flatten a document into rows of string cells.
    let rec private rowsOf (block: Block) : string list list =
        match block with
        | Heading(_, inlines)
        | Paragraph(inlines, _) -> [ [ InlineText.ofInlines inlines ] ]
        | ListBlock spec ->
            spec.Items
            |> List.map (fun item ->
                [ item.Content
                  |> List.choose (function
                      | Paragraph(i, _) -> Some(InlineText.ofInlines i)
                      | _ -> None)
                  |> String.concat " " ])
        | CodeBlock(_, code) ->
            code.Replace("\r\n", "\n").Split('\n') |> Array.map (fun ln -> [ ln ]) |> Array.toList
        | Quote blocks -> blocks |> List.collect rowsOf
        | Table spec ->
            let rowCells (r: TableRow) =
                r.Cells
                |> List.map (fun c ->
                    c.Content
                    |> List.choose (function
                        | Paragraph(i, _) -> Some(InlineText.ofInlines i)
                        | _ -> None)
                    |> String.concat " ")
            (spec.Header |> Option.map (fun h -> [ rowCells h ]) |> Option.defaultValue [])
            @ (spec.Rows |> List.map rowCells)
        | Media spec -> [ [ defaultArg spec.Alt "[media]" ] ]
        | Container spec -> spec.Children |> List.collect (fun c -> rowsOf c.Content)
        | ThematicBreak -> [ [] ]
        | BlockRaw(_, data) -> [ [ data ] ]

    /// Writes a `Document` as a single-sheet `.xlsx` workbook.
    type Writer() =
        interface IDocumentWriter with
            member _.MediaTypes = [ MediaType ]
            member _.Write(doc, output, _ctx) =
                let allRows =
                    (match doc.Metadata.Title with
                     | Some t -> [ [ t ] ]
                     | None -> [])
                    @ (Document.toBlocks doc |> List.collect rowsOf)
                use mem = new MemoryStream()
                using (SpreadsheetDocument.Create(mem, SpreadsheetDocumentType.Workbook)) (fun sd ->
                    let wbPart = sd.AddWorkbookPart()
                    let wsPart = wbPart.AddNewPart<WorksheetPart>()
                    let sheetData = S.SheetData()
                    allRows
                    |> List.iteri (fun ri cells ->
                        let row = S.Row(RowIndex = u32 (uint32 (ri + 1)))
                        cells |> List.iteri (fun ci v -> row.AppendChild(makeCell ci (ri + 1) v) |> ignore)
                        sheetData.AppendChild row |> ignore)
                    wsPart.Worksheet <- w1 (S.Worksheet()) sheetData

                    let sheets = S.Sheets()
                    sheets.AppendChild(S.Sheet(Name = sv "Sheet1", SheetId = u32 1u, Id = sv (wbPart.GetIdOfPart wsPart)))
                    |> ignore
                    let wb = S.Workbook()
                    wb.AppendChild sheets |> ignore
                    wbPart.Workbook <- wb)
                mem.Position <- 0L
                mem.CopyTo output


/// Writer for PowerPoint decks (`.pptx`,
/// `application/vnd.openxmlformats-officedocument.presentationml.presentation`), backed
/// by the DocumentFormat.OpenXml SDK. Slides are derived from the document; the SDK owns
/// the package, content-types and relationships. There is no reader.
module Pptx =

    [<Literal>]
    let MediaType = "application/vnd.openxmlformats-officedocument.presentationml.presentation"

    let private pml = "http://schemas.openxmlformats.org/presentationml/2006/main"
    let private dml = "http://schemas.openxmlformats.org/drawingml/2006/main"
    let private rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"

    /// A slide: a title and a list of body lines (with indent level).
    type private Slide = { Title: string; Lines: (int * string) list }

    let rec private linesOf (indent: int) (block: Block) : (int * string) list =
        match block with
        | Heading(_, inlines) -> [ indent, InlineText.ofInlines inlines ]
        | Paragraph(inlines, _) -> [ indent, InlineText.ofInlines inlines ]
        | ListBlock spec ->
            spec.Items
            |> List.collect (fun item -> item.Content |> List.collect (linesOf (indent + 1)))
        | CodeBlock(_, code) ->
            code.Replace("\r\n", "\n").Split('\n') |> Array.map (fun ln -> indent, ln) |> Array.toList
        | Quote blocks -> blocks |> List.collect (linesOf (indent + 1))
        | Media spec -> [ indent, "[" + defaultArg spec.Alt "media" + "]" ]
        | Table spec ->
            let rowText (r: TableRow) =
                r.Cells
                |> List.map (fun c ->
                    c.Content
                    |> List.choose (function
                        | Paragraph(i, _) -> Some(InlineText.ofInlines i)
                        | _ -> None)
                    |> String.concat " ")
                |> String.concat " | "
            (spec.Header |> Option.map (fun h -> [ indent, rowText h ]) |> Option.defaultValue [])
            @ (spec.Rows |> List.map (fun r -> indent, rowText r))
        | Container spec -> spec.Children |> List.collect (fun c -> linesOf indent c.Content)
        | ThematicBreak -> []
        | BlockRaw(_, data) -> [ indent, data ]

    /// Split a fluid block stream into slides at level-1 headings.
    let private slidesFromBlocks (title: string option) (blocks: Block list) : Slide list =
        let slides = ResizeArray<Slide>()
        let mutable cur = { Title = defaultArg title "Slide"; Lines = [] }
        let mutable started = false
        for b in blocks do
            match b with
            | Heading(1, inlines) ->
                if started then slides.Add cur
                cur <- { Title = InlineText.ofInlines inlines; Lines = [] }
                started <- true
            | other ->
                cur <- { cur with Lines = cur.Lines @ linesOf 0 other }
                started <- true
        if started then slides.Add cur
        if slides.Count = 0 then slides.Add cur
        List.ofSeq slides

    // --- Static boilerplate parts (master / layout / theme) -----------------------
    // These never vary with the document, so they are kept as fixed OOXML fragments
    // and parsed by the SDK; the SDK still wires up content-types and relationships.

    let private slideMasterXml =
        sprintf "<p:sldMaster xmlns:p=\"%s\" xmlns:a=\"%s\" xmlns:r=\"%s\">" pml dml rel
        + "<p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg><p:spTree>"
        + "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>"
        + "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>"
        + "</p:spTree></p:cSld>"
        + "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>"
        + "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>"
        + "<p:txStyles><p:titleStyle><a:lvl1pPr><a:defRPr sz=\"4400\"/></a:lvl1pPr></p:titleStyle>"
        + "<p:bodyStyle><a:lvl1pPr><a:buChar char=\"\u2022\"/><a:defRPr sz=\"2400\"/></a:lvl1pPr>"
        + "<a:lvl2pPr marL=\"457200\"><a:buChar char=\"\u2013\"/><a:defRPr sz=\"2000\"/></a:lvl2pPr>"
        + "<a:lvl3pPr marL=\"914400\"><a:buChar char=\"\u2022\"/><a:defRPr sz=\"1800\"/></a:lvl3pPr></p:bodyStyle>"
        + "<p:otherStyle/></p:txStyles></p:sldMaster>"

    let private slideLayoutXml =
        sprintf "<p:sldLayout xmlns:p=\"%s\" xmlns:a=\"%s\" xmlns:r=\"%s\" type=\"obj\" preserve=\"1\">" pml dml rel
        + "<p:cSld name=\"Title and Content\"><p:spTree>"
        + "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>"
        + "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>"
        + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>"

    let private themeXml =
        let scheme (name: string) (hex: string) = sprintf "<a:%s><a:srgbClr val=\"%s\"/></a:%s>" name hex name
        sprintf "<a:theme xmlns:a=\"%s\" name=\"Office\"><a:themeElements>" dml
        + "<a:clrScheme name=\"Office\">"
        + "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1>"
        + "<a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>"
        + scheme "dk2" "44546A"
        + scheme "lt2" "E7E6E6"
        + scheme "accent1" "4472C4"
        + scheme "accent2" "ED7D31"
        + scheme "accent3" "A5A5A5"
        + scheme "accent4" "FFC000"
        + scheme "accent5" "5B9BD5"
        + scheme "accent6" "70AD47"
        + scheme "hlink" "0563C1"
        + scheme "folHlink" "954F72"
        + "</a:clrScheme>"
        + "<a:fontScheme name=\"Office\"><a:majorFont><a:latin typeface=\"Calibri Light\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>"
        + "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>"
        + "<a:fmtScheme name=\"Office\">"
        + "<a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst>"
        + "<a:lnStyleLst><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst>"
        + "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>"
        + "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst>"
        + "</a:fmtScheme></a:themeElements></a:theme>"

    // --- Dynamic slide construction (SDK objects) ---------------------------------

    let private titleShape (title: string) =
        let sp = P.Shape()
        sp.AppendChild(
            P.NonVisualShapeProperties(
                P.NonVisualDrawingProperties(Id = u32 2u, Name = sv "Title"),
                w1 (P.NonVisualShapeDrawingProperties()) (D.ShapeLocks(NoGrouping = bv true)),
                w1 (P.ApplicationNonVisualDrawingProperties()) (P.PlaceholderShape(Type = ev P.PlaceholderValues.Title))))
        |> ignore
        sp.AppendChild(P.ShapeProperties()) |> ignore
        let body =
            P.TextBody(D.BodyProperties(), D.ListStyle(), (w1 (D.Paragraph()) (w1 (D.Run()) (D.Text(title)))))
        sp.AppendChild body |> ignore
        sp

    let private bodyShape (lines: (int * string) list) =
        let sp = P.Shape()
        sp.AppendChild(
            P.NonVisualShapeProperties(
                P.NonVisualDrawingProperties(Id = u32 3u, Name = sv "Content"),
                w1 (P.NonVisualShapeDrawingProperties()) (D.ShapeLocks(NoGrouping = bv true)),
                w1 (P.ApplicationNonVisualDrawingProperties()) (P.PlaceholderShape(Index = u32 1u))))
        |> ignore
        sp.AppendChild(P.ShapeProperties()) |> ignore
        let body = P.TextBody(D.BodyProperties(), D.ListStyle())
        if lines.IsEmpty then
            body.AppendChild(D.Paragraph()) |> ignore
        else
            for (lvl, text) in lines do
                let para = D.Paragraph()
                if lvl > 0 then
                    para.AppendChild(D.ParagraphProperties(Level = i32 (min 8 lvl))) |> ignore
                para.AppendChild(w1 (D.Run()) (D.Text(text))) |> ignore
                body.AppendChild para |> ignore
        sp.AppendChild body |> ignore
        sp

    let private buildSlide (slide: Slide) : P.Slide =
        let shapeTree = P.ShapeTree()
        shapeTree.AppendChild(
            P.NonVisualGroupShapeProperties(
                P.NonVisualDrawingProperties(Id = u32 1u, Name = sv ""),
                P.NonVisualGroupShapeDrawingProperties(),
                P.ApplicationNonVisualDrawingProperties()))
        |> ignore
        shapeTree.AppendChild(
            w1 (P.GroupShapeProperties()) (
                D.TransformGroup(
                    D.Offset(X = i64 0L, Y = i64 0L),
                    D.Extents(Cx = i64 0L, Cy = i64 0L),
                    D.ChildOffset(X = i64 0L, Y = i64 0L),
                    D.ChildExtents(Cx = i64 0L, Cy = i64 0L))))
        |> ignore
        shapeTree.AppendChild(titleShape slide.Title) |> ignore
        shapeTree.AppendChild(bodyShape slide.Lines) |> ignore
        w1 (P.Slide()) (w1 (P.CommonSlideData()) shapeTree)

    /// Writes a `Document` as a `.pptx` package.
    type Writer() =
        interface IDocumentWriter with
            member _.MediaTypes = [ MediaType ]
            member _.Write(doc, output, _ctx) =
                let slides =
                    match doc.Body with
                    | Paged pages ->
                        pages
                        |> List.mapi (fun i p ->
                            let content = p.Header @ p.Content @ p.Footer
                            let title =
                                content
                                |> List.tryPick (function
                                    | Heading(_, inlines) -> Some(InlineText.ofInlines inlines)
                                    | _ -> None)
                                |> Option.defaultValue (sprintf "Slide %d" (i + 1))
                            { Title = title; Lines = content |> List.collect (linesOf 0) })
                    | Fluid blocks -> slidesFromBlocks doc.Metadata.Title blocks

                use mem = new MemoryStream()
                using (PresentationDocument.Create(mem, PresentationDocumentType.Presentation)) (fun pd ->
                    let presPart = pd.AddPresentationPart()
                    presPart.Presentation <- P.Presentation()

                    // Master → layout → theme (static), with controlled relationship ids.
                    let masterPart = presPart.AddNewPart<SlideMasterPart>("rIdMaster")
                    let layoutPart = masterPart.AddNewPart<SlideLayoutPart>("rId1")
                    let themePart = masterPart.AddNewPart<ThemePart>("rId2")
                    layoutPart.AddPart(masterPart, "rId1") |> ignore
                    masterPart.SlideMaster <- P.SlideMaster(slideMasterXml)
                    layoutPart.SlideLayout <- P.SlideLayout(slideLayoutXml)
                    themePart.Theme <- D.Theme(themeXml)

                    // Slides.
                    let slideParts =
                        slides
                        |> List.map (fun s ->
                            let sp = presPart.AddNewPart<SlidePart>()
                            sp.Slide <- buildSlide s
                            sp.AddPart(layoutPart, "rId1") |> ignore
                            sp)

                    // presentation.xml structure.
                    let presentation = presPart.Presentation
                    presentation.AppendChild(
                        w1 (P.SlideMasterIdList()) (P.SlideMasterId(Id = u32 2147483648u, RelationshipId = sv "rIdMaster")))
                    |> ignore
                    let sldIdList = P.SlideIdList()
                    slideParts
                    |> List.iteri (fun i sp ->
                        sldIdList.AppendChild(
                            P.SlideId(Id = u32 (256u + uint32 i), RelationshipId = sv (presPart.GetIdOfPart sp)))
                        |> ignore)
                    presentation.AppendChild sldIdList |> ignore
                    presentation.AppendChild(P.SlideSize(Cx = i32 9144000, Cy = i32 6858000)) |> ignore
                    presentation.AppendChild(P.NotesSize(Cx = i64 6858000L, Cy = i64 9144000L)) |> ignore)
                mem.Position <- 0L
                mem.CopyTo output
