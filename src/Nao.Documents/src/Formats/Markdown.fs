namespace Nao.Documents

open System
open System.IO
open System.Text

/// Reader and writer for `text/markdown` (CommonMark-ish subset). Markdown is the
/// canonical *fluid* format, so it exercises headings, lists, code, quotes, tables,
/// emphasis, links and images of the unified model.
module Markdown =

    [<Literal>]
    let MediaType = "text/markdown"

    // --- Writer: Document -> Markdown ---------------------------------------------

    let rec private renderInline (i: Inline) : string =
        match i with
        | Run(text, style) ->
            let bold = style.Weight = Some Bold || style.Weight = Some Black
            let italic = style.Style = Some Italic || style.Style = Some Oblique
            let strike = style.Decorations |> List.contains LineThrough
            let mutable s = text
            if strike then s <- "~~" + s + "~~"
            if bold then s <- "**" + s + "**"
            if italic then s <- "*" + s + "*"
            s
        | LineBreak -> "  \n"
        | Link(children, href) ->
            let inner = children |> List.map renderInline |> String.concat ""
            sprintf "[%s](%s)" inner href
        | InlineImage(ResourceId id, alt, _) ->
            sprintf "![%s](%s)" (defaultArg alt "") id
        | InlineCode code -> "`" + code + "`"
        | InlineRaw(_, data) -> data

    let private renderInlines (inlines: Inline list) =
        inlines |> List.map renderInline |> String.concat ""

    let private resourceRef (doc: Document) (id: ResourceId) =
        match Document.tryResource id doc with
        | Some r -> r.LocalPath |> Option.orElse r.Uri |> Option.defaultValue id.Value
        | None -> id.Value

    let rec private renderBlock (doc: Document) (sb: StringBuilder) (block: Block) =
        match block with
        | Heading(level, inlines) ->
            let hashes = String('#', max 1 (min 6 level))
            sb.Append(hashes).Append(' ').AppendLine(renderInlines inlines).AppendLine() |> ignore
        | Paragraph(inlines, _) ->
            sb.AppendLine(renderInlines inlines).AppendLine() |> ignore
        | ListBlock spec ->
            spec.Items
            |> List.iteri (fun idx item ->
                let marker =
                    if spec.Ordered then sprintf "%d. " (idx + defaultArg spec.Start 1)
                    else "- "
                match item.Content with
                | Paragraph(inlines, _) :: _ ->
                    sb.Append(marker).AppendLine(renderInlines inlines) |> ignore
                | _ ->
                    sb.Append(marker).AppendLine("") |> ignore)
            sb.AppendLine() |> ignore
        | CodeBlock(lang, code) ->
            sb.Append("```").AppendLine(defaultArg lang "") |> ignore
            sb.AppendLine(code) |> ignore
            sb.AppendLine("```").AppendLine() |> ignore
        | Quote blocks ->
            let inner = StringBuilder()
            blocks |> List.iter (renderBlock doc inner)
            inner.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n')
            |> Array.iter (fun line -> sb.Append("> ").AppendLine(line) |> ignore)
            sb.AppendLine() |> ignore
        | Media spec ->
            let path = resourceRef doc spec.Resource
            sb.AppendLine(sprintf "![%s](%s)" (defaultArg spec.Alt "") path) |> ignore
            if not spec.Caption.IsEmpty then
                sb.Append('*').Append(renderInlines spec.Caption).AppendLine("*") |> ignore
            sb.AppendLine() |> ignore
        | Table spec ->
            let renderRow (row: TableRow) =
                "| "
                + (row.Cells
                   |> List.map (fun c ->
                       c.Content
                       |> List.choose (function Paragraph(i, _) -> Some(renderInlines i) | _ -> None)
                       |> String.concat " ")
                   |> String.concat " | ")
                + " |"
            let columnCount =
                match spec.Header with
                | Some h -> h.Cells.Length
                | None -> spec.Rows |> List.tryHead |> Option.map (fun r -> r.Cells.Length) |> Option.defaultValue 0
            spec.Header
            |> Option.iter (fun h ->
                sb.AppendLine(renderRow h) |> ignore
                sb.AppendLine("|" + String.replicate columnCount " --- |") |> ignore)
            spec.Rows |> List.iter (fun r -> sb.AppendLine(renderRow r) |> ignore)
            sb.AppendLine() |> ignore
        | Container spec ->
            spec.Children |> List.iter (fun c -> renderBlock doc sb c.Content)
        | ThematicBreak ->
            sb.AppendLine("---").AppendLine() |> ignore
        | BlockRaw(_, data) ->
            sb.AppendLine(data).AppendLine() |> ignore

    /// Writes a `Document` as Markdown.
    type Writer() =
        interface IDocumentWriter with
            member _.MediaTypes = [ MediaType ]
            member _.Write(doc, output, _ctx) =
                let sb = StringBuilder()
                doc.Metadata.Title |> Option.iter (fun t -> sb.Append("# ").AppendLine(t).AppendLine() |> ignore)
                Document.toBlocks doc |> List.iter (renderBlock doc sb)
                use w = new StreamWriter(output, UTF8Encoding(false), 1024, leaveOpen = true)
                w.Write(sb.ToString().TrimEnd() + "\n")

    // --- Reader: Markdown -> Document (via Markdig) -------------------------------

    /// Maps the Markdig CommonMark AST onto the unified document model. Kept in a
    /// private sub-module so the Markdig type abbreviations don't clash with the
    /// model's own `Inline` / `Block` / `Table` types.
    module private MarkdigMap =

        type MHeading = Markdig.Syntax.HeadingBlock
        type MPara = Markdig.Syntax.ParagraphBlock
        type MList = Markdig.Syntax.ListBlock
        type MListItem = Markdig.Syntax.ListItemBlock
        type MQuote = Markdig.Syntax.QuoteBlock
        type MFenced = Markdig.Syntax.FencedCodeBlock
        type MCode = Markdig.Syntax.CodeBlock
        type MThematic = Markdig.Syntax.ThematicBreakBlock
        type MHtmlBlock = Markdig.Syntax.HtmlBlock
        type MLeaf = Markdig.Syntax.LeafBlock
        type MContainerBlock = Markdig.Syntax.ContainerBlock
        type MTable = Markdig.Extensions.Tables.Table
        type MRow = Markdig.Extensions.Tables.TableRow
        type MCell = Markdig.Extensions.Tables.TableCell
        type MLiteral = Markdig.Syntax.Inlines.LiteralInline
        type MCodeInline = Markdig.Syntax.Inlines.CodeInline
        type MLineBreak = Markdig.Syntax.Inlines.LineBreakInline
        type MEmphasis = Markdig.Syntax.Inlines.EmphasisInline
        type MLink = Markdig.Syntax.Inlines.LinkInline
        type MAutolink = Markdig.Syntax.Inlines.AutolinkInline
        type MHtmlInline = Markdig.Syntax.Inlines.HtmlInline
        type MContainerInline = Markdig.Syntax.Inlines.ContainerInline

        let pipeline =
            let b = Markdig.MarkdownPipelineBuilder()
            Markdig.MarkdownExtensions.UseAdvancedExtensions(b).Build()

        /// Join the raw text lines of a leaf block (code / raw html) into one string.
        let leafText (lb: MLeaf) : string =
            let lines = lb.Lines
            let sb = StringBuilder()
            for i in 0 .. lines.Count - 1 do
                if i > 0 then sb.Append('\n') |> ignore
                sb.Append(lines.Lines.[i].Slice.ToString()) |> ignore
            sb.ToString()

        let rec mapInline (style: TextStyle) (inl: Markdig.Syntax.Inlines.Inline) : Inline list =
            match inl with
            | :? MLiteral as x -> [ Run(x.Content.ToString(), style) ]
            | :? MCodeInline as x -> [ InlineCode x.Content ]
            | :? MLineBreak -> [ LineBreak ]
            | :? MAutolink as x -> [ Link([ Run(x.Url, style) ], x.Url) ]
            | :? MHtmlInline as x -> [ InlineRaw("html", x.Tag) ]
            | :? MLink as x ->
                let url = if isNull x.Url then "" else x.Url
                let children =
                    x |> Seq.cast<Markdig.Syntax.Inlines.Inline> |> Seq.collect (mapInline style) |> List.ofSeq
                if x.IsImage then
                    let alt = InlineText.ofInlines children
                    [ InlineImage(ResourceId url, (if String.IsNullOrEmpty alt then None else Some alt), None) ]
                else [ Link(children, url) ]
            | :? MEmphasis as x ->
                let style2 =
                    match x.DelimiterChar, x.DelimiterCount with
                    | '~', _ -> { style with Decorations = LineThrough :: style.Decorations }
                    | _, n when n >= 2 -> { style with Weight = Some Bold }
                    | _ -> { style with Style = Some Italic }
                x |> Seq.cast<Markdig.Syntax.Inlines.Inline> |> Seq.collect (mapInline style2) |> List.ofSeq
            | :? MContainerInline as x ->
                x |> Seq.cast<Markdig.Syntax.Inlines.Inline> |> Seq.collect (mapInline style) |> List.ofSeq
            | _ -> []

        let inlinesOf (c: MContainerInline) : Inline list =
            if isNull (box c) then []
            else c |> Seq.cast<Markdig.Syntax.Inlines.Inline> |> Seq.collect (mapInline TextStyle.Default) |> List.ofSeq

        let rec mapBlock (b: Markdig.Syntax.Block) : Block list =
            match b with
            | :? MHeading as h -> [ Heading(h.Level, inlinesOf h.Inline) ]
            | :? MTable as t -> [ mapTable t ]
            | :? MList as l -> [ mapList l ]
            | :? MQuote as q ->
                [ Quote(q |> Seq.cast<Markdig.Syntax.Block> |> Seq.collect mapBlock |> List.ofSeq) ]
            | :? MFenced as f ->
                let lang = if String.IsNullOrWhiteSpace f.Info then None else Some f.Info
                [ CodeBlock(lang, (leafText f).TrimEnd('\n')) ]
            | :? MCode as c -> [ CodeBlock(None, (leafText c).TrimEnd('\n')) ]
            | :? MThematic -> [ ThematicBreak ]
            | :? MHtmlBlock as h -> [ BlockRaw("html", (leafText h).TrimEnd('\n')) ]
            | :? MPara as p -> [ Paragraph(inlinesOf p.Inline, None) ]
            | :? MContainerBlock as cont ->
                cont |> Seq.cast<Markdig.Syntax.Block> |> Seq.collect mapBlock |> List.ofSeq
            | _ -> []

        and mapList (l: MList) : Block =
            let items =
                l |> Seq.cast<MListItem>
                |> Seq.map (fun item ->
                    { Content = item |> Seq.cast<Markdig.Syntax.Block> |> Seq.collect mapBlock |> List.ofSeq })
                |> List.ofSeq
            let start =
                if l.IsOrdered then
                    match Int32.TryParse l.OrderedStart with
                    | true, n -> Some n
                    | _ -> None
                else None
            ListBlock { Ordered = l.IsOrdered; Start = start; Items = items }

        and mapTable (t: MTable) : Block =
            let mapRow (r: MRow) : TableRow =
                { Cells =
                    r |> Seq.cast<MCell>
                    |> Seq.map (fun c ->
                        { Content = c |> Seq.cast<Markdig.Syntax.Block> |> Seq.collect mapBlock |> List.ofSeq
                          ColSpan = max 1 c.ColumnSpan
                          RowSpan = max 1 c.RowSpan
                          Style = None })
                    |> List.ofSeq }
            let rows = t |> Seq.cast<MRow> |> List.ofSeq
            let header = rows |> List.tryFind (fun r -> r.IsHeader) |> Option.map mapRow
            let body = rows |> List.filter (fun r -> not r.IsHeader) |> List.map mapRow
            let colCount =
                match header with
                | Some h -> h.Cells.Length
                | None -> body |> List.tryHead |> Option.map (fun r -> r.Cells.Length) |> Option.defaultValue 0
            Table { Columns = List.replicate colCount { Width = None; Align = None }; Header = header; Rows = body; Style = None }

    /// Parses Markdown into a fluid `Document` with the Markdig CommonMark parser
    /// (advanced extensions on: pipe tables, strikethrough, autolinks, ...).
    type Reader() =
        interface IDocumentReader with
            member _.MediaTypes = [ MediaType ]
            member _.Read(input, _ctx) =
                use r = new StreamReader(input, Encoding.UTF8, true, 1024, leaveOpen = true)
                let text = r.ReadToEnd()
                let md = Markdig.Markdown.Parse(text, MarkdigMap.pipeline)
                let blocks =
                    md |> Seq.cast<Markdig.Syntax.Block> |> Seq.collect MarkdigMap.mapBlock |> List.ofSeq
                Document.OfBlocks blocks

    /// A `ConverterRegistry` preconfigured with the built-in text formats
    /// (markdown + plain text), ready for A → unified → B conversions.
    let defaultRegistry () =
        let reg = ConverterRegistry()
        reg.RegisterReader(Reader())
        reg.RegisterWriter(Writer())
        reg.RegisterReader(PlainText.Reader())
        reg.RegisterWriter(PlainText.Writer())
        reg
