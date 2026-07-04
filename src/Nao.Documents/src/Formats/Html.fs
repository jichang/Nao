namespace Nao.Documents

open System
open System.IO
open System.Text
open System.Text.RegularExpressions

/// Reader and writer for `text/html`. HTML is a *fluid* format like Markdown, but
/// richer: it carries inline styling (bold/italic/underline, colour), links, images,
/// lists, tables, code and block quotes. The writer emits a small, clean document;
/// the reader parses a pragmatic subset of common block and inline elements.
module Html =

    [<Literal>]
    let MediaType = "text/html"

    // --- Shared escaping ----------------------------------------------------------

    let private escape (s: string) =
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")

    let private escapeAttr (s: string) =
        (escape s).Replace("\"", "&quot;")

    let private unescape (s: string) =
        s.Replace("&lt;", "<").Replace("&gt;", ">")
         .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'")
         .Replace("&nbsp;", " ").Replace("&amp;", "&")

    // --- Writer: Document -> HTML -------------------------------------------------

    let private cssLength (l: Length) =
        match l.Unit with
        | Px -> sprintf "%gpx" l.Value
        | Pt -> sprintf "%gpt" l.Value
        | Em -> sprintf "%gem" l.Value
        | Percent -> sprintf "%g%%" l.Value
        | Fr -> sprintf "%gfr" l.Value
        | Auto -> "auto"

    /// Build the inline CSS for a run's non-structural styling (colour, font).
    let private runCss (style: TextStyle) =
        [ match style.Color with Some c -> yield sprintf "color:%s" c.Hex | None -> ()
          match style.Background with Some c -> yield sprintf "background-color:%s" c.Hex | None -> ()
          match style.FontFamily with Some f -> yield sprintf "font-family:%s" f | None -> ()
          match style.FontSize with Some s -> yield sprintf "font-size:%s" (cssLength s) | None -> () ]
        |> String.concat ";"

    let rec private renderInline (doc: Document) (i: Inline) : string =
        match i with
        | Run(text, style) ->
            let mutable s = escape text
            let css = runCss style
            if css <> "" then s <- sprintf "<span style=\"%s\">%s</span>" css s
            if style.Decorations |> List.contains Underline then s <- "<u>" + s + "</u>"
            if style.Decorations |> List.contains LineThrough then s <- "<s>" + s + "</s>"
            let italic = style.Style = Some Italic || style.Style = Some Oblique
            if italic then s <- "<em>" + s + "</em>"
            let bold =
                match style.Weight with
                | Some(Bold | Black | SemiBold) -> true
                | _ -> false
            if bold then s <- "<strong>" + s + "</strong>"
            s
        | LineBreak -> "<br/>"
        | Link(children, href) ->
            let inner = children |> List.map (renderInline doc) |> String.concat ""
            sprintf "<a href=\"%s\">%s</a>" (escapeAttr href) inner
        | InlineImage(id, alt, _) ->
            let src =
                match Document.tryResource id doc with
                | Some r -> r.LocalPath |> Option.orElse r.Uri |> Option.defaultValue id.Value
                | None -> id.Value
            sprintf "<img src=\"%s\" alt=\"%s\"/>" (escapeAttr src) (escapeAttr (defaultArg alt ""))
        | InlineCode code -> "<code>" + escape code + "</code>"
        | InlineRaw(format, data) when format = MediaType || format = "html" -> data
        | InlineRaw(_, data) -> escape data

    let private renderInlines (doc: Document) (inlines: Inline list) =
        inlines |> List.map (renderInline doc) |> String.concat ""

    let private alignAttr (align: TextAlign option) =
        match align with
        | Some Left -> " style=\"text-align:left\""
        | Some Center -> " style=\"text-align:center\""
        | Some Right -> " style=\"text-align:right\""
        | Some Justify -> " style=\"text-align:justify\""
        | None -> ""

    let rec private renderBlock (doc: Document) (sb: StringBuilder) (indent: string) (block: Block) =
        let line (s: string) = sb.Append(indent).AppendLine(s) |> ignore
        match block with
        | Heading(level, inlines) ->
            let l = max 1 (min 6 level)
            line (sprintf "<h%d>%s</h%d>" l (renderInlines doc inlines) l)
        | Paragraph(inlines, align) ->
            line (sprintf "<p%s>%s</p>" (alignAttr align) (renderInlines doc inlines))
        | ListBlock spec ->
            let tag = if spec.Ordered then "ol" else "ul"
            let startAttr =
                match spec.Start with
                | Some n when spec.Ordered && n <> 1 -> sprintf " start=\"%d\"" n
                | _ -> ""
            line (sprintf "<%s%s>" tag startAttr)
            for item in spec.Items do
                line "  <li>"
                item.Content |> List.iter (renderBlock doc sb (indent + "    "))
                line "  </li>"
            line (sprintf "</%s>" tag)
        | CodeBlock(lang, code) ->
            let cls = match lang with Some l -> sprintf " class=\"language-%s\"" l | None -> ""
            line (sprintf "<pre><code%s>%s</code></pre>" cls (escape code))
        | Quote blocks ->
            line "<blockquote>"
            blocks |> List.iter (renderBlock doc sb (indent + "  "))
            line "</blockquote>"
        | Media spec ->
            let src =
                match Document.tryResource spec.Resource doc with
                | Some r -> r.LocalPath |> Option.orElse r.Uri |> Option.defaultValue spec.Resource.Value
                | None -> spec.Resource.Value
            line "<figure>"
            line (sprintf "  <img src=\"%s\" alt=\"%s\"/>" (escapeAttr src) (escapeAttr (defaultArg spec.Alt "")))
            if not spec.Caption.IsEmpty then
                line (sprintf "  <figcaption>%s</figcaption>" (renderInlines doc spec.Caption))
            line "</figure>"
        | Table spec ->
            line "<table>"
            let renderCell (tag: string) (cell: TableCell) =
                let span = if cell.ColSpan > 1 then sprintf " colspan=\"%d\"" cell.ColSpan else ""
                let rspan = if cell.RowSpan > 1 then sprintf " rowspan=\"%d\"" cell.RowSpan else ""
                let inner =
                    cell.Content
                    |> List.choose (function Paragraph(i, _) -> Some(renderInlines doc i) | _ -> None)
                    |> String.concat " "
                sprintf "<%s%s%s>%s</%s>" tag span rspan inner tag
            spec.Header
            |> Option.iter (fun h ->
                line "  <thead>"
                line ("    <tr>" + (h.Cells |> List.map (renderCell "th") |> String.concat "") + "</tr>")
                line "  </thead>")
            line "  <tbody>"
            for row in spec.Rows do
                line ("    <tr>" + (row.Cells |> List.map (renderCell "td") |> String.concat "") + "</tr>")
            line "  </tbody>"
            line "</table>"
        | Container spec ->
            line "<div>"
            spec.Children |> List.iter (fun c -> renderBlock doc sb (indent + "  ") c.Content)
            line "</div>"
        | ThematicBreak -> line "<hr/>"
        | BlockRaw(format, data) when format = MediaType || format = "html" -> line data
        | BlockRaw(_, data) -> line (sprintf "<pre>%s</pre>" (escape data))

    /// Writes a `Document` as a standalone HTML5 document.
    type Writer() =
        interface IDocumentWriter with
            member _.MediaTypes = [ MediaType ]
            member _.Write(doc, output, _ctx) =
                let sb = StringBuilder()
                let lang = doc.Metadata.Language |> Option.map (sprintf " lang=\"%s\"") |> Option.defaultValue ""
                sb.AppendLine("<!DOCTYPE html>") |> ignore
                sb.AppendLine(sprintf "<html%s>" lang) |> ignore
                sb.AppendLine("<head>") |> ignore
                sb.AppendLine("  <meta charset=\"utf-8\"/>") |> ignore
                doc.Metadata.Title |> Option.iter (fun t -> sb.AppendLine(sprintf "  <title>%s</title>" (escape t)) |> ignore)
                doc.Metadata.Authors
                |> List.iter (fun a -> sb.AppendLine(sprintf "  <meta name=\"author\" content=\"%s\"/>" (escapeAttr a)) |> ignore)
                sb.AppendLine("</head>") |> ignore
                sb.AppendLine("<body>") |> ignore
                doc.Metadata.Title |> Option.iter (fun t -> sb.AppendLine(sprintf "  <h1>%s</h1>" (escape t)) |> ignore)
                Document.toBlocks doc |> List.iter (renderBlock doc sb "  ")
                sb.AppendLine("</body>") |> ignore
                sb.AppendLine("</html>") |> ignore
                use w = new StreamWriter(output, UTF8Encoding(false), 1024, leaveOpen = true)
                w.Write(sb.ToString())

    // --- Reader: HTML -> Document (via HtmlAgilityPack) --------------------------

    /// Maps the HtmlAgilityPack DOM onto the unified document model. Kept in a private
    /// sub-module to scope the HAP-specific helpers.
    module private HapMap =
        open HtmlAgilityPack

        let blockTags =
            set [ "p"; "div"; "section"; "article"; "main"; "header"; "footer"; "aside"; "nav"
                  "h1"; "h2"; "h3"; "h4"; "h5"; "h6"; "ul"; "ol"; "li"; "blockquote"; "pre"
                  "table"; "thead"; "tbody"; "tfoot"; "tr"; "td"; "th"; "hr"; "figure"; "figcaption" ]

        let decode (s: string) = HtmlEntity.DeEntitize(s)

        let attr (node: HtmlNode) (name: string) =
            match node.GetAttributeValue(name, null) with
            | null -> None
            | v -> Some(decode v)

        let rec mapInline (style: TextStyle) (node: HtmlNode) : Inline list =
            match node.NodeType with
            | HtmlNodeType.Text ->
                let t = decode node.InnerText
                if t = "" then [] else [ Run(t, style) ]
            | HtmlNodeType.Comment -> []
            | _ ->
                match node.Name.ToLowerInvariant() with
                | "br" -> [ LineBreak ]
                | "strong" | "b" -> children { style with Weight = Some Bold } node
                | "em" | "i" -> children { style with Style = Some Italic } node
                | "u" -> children { style with Decorations = Underline :: style.Decorations } node
                | "s" | "strike" | "del" -> children { style with Decorations = LineThrough :: style.Decorations } node
                | "code" -> [ InlineCode(decode node.InnerText) ]
                | "a" -> [ Link(children style node, defaultArg (attr node "href") "") ]
                | "img" -> [ InlineImage(ResourceId(defaultArg (attr node "src") ""), attr node "alt", None) ]
                | _ -> children style node

        and children (style: TextStyle) (node: HtmlNode) : Inline list =
            node.ChildNodes |> Seq.collect (mapInline style) |> List.ofSeq

        let inlineChildren (node: HtmlNode) = children TextStyle.Default node

        let rec mapBlock (node: HtmlNode) : Block list =
            match node.NodeType with
            | HtmlNodeType.Text ->
                let t = decode node.InnerText
                if t.Trim() = "" then [] else [ Paragraph([ Run(t, TextStyle.Default) ], None) ]
            | HtmlNodeType.Comment -> []
            | _ ->
                match node.Name.ToLowerInvariant() with
                | "h1" | "h2" | "h3" | "h4" | "h5" | "h6" ->
                    [ Heading(int (node.Name.Substring 1), inlineChildren node) ]
                | "p" -> [ Paragraph(inlineChildren node, None) ]
                | "ul" | "ol" -> [ mapList node ]
                | "pre" -> [ CodeBlock(None, (decode node.InnerText).TrimEnd('\n')) ]
                | "blockquote" -> [ Quote(node.ChildNodes |> Seq.collect mapBlock |> List.ofSeq) ]
                | "hr" -> [ ThematicBreak ]
                | "table" -> [ mapTable node ]
                | "img" ->
                    [ Media
                          { Resource = ResourceId(defaultArg (attr node "src") "")
                            Alt = attr node "alt"
                            Caption = []
                            Size = None } ]
                | "figcaption" -> [ Paragraph(inlineChildren node, None) ]
                | "figure" -> node.ChildNodes |> Seq.collect mapBlock |> List.ofSeq
                | "br" -> []
                | "script" | "style" | "title" | "meta" | "link" | "head" -> []
                | "strong" | "b" | "em" | "i" | "u" | "s" | "strike" | "del" | "code" | "a" | "span" ->
                    let inl = mapInline TextStyle.Default node
                    if inl |> List.forall (function Run(t, _) -> t.Trim() = "" | _ -> false) then []
                    else [ Paragraph(inl, None) ]
                | _ -> node.ChildNodes |> Seq.collect mapBlock |> List.ofSeq

        and mapList (node: HtmlNode) : Block =
            let ordered = node.Name.ToLowerInvariant() = "ol"
            let items =
                node.ChildNodes
                |> Seq.filter (fun n -> n.NodeType = HtmlNodeType.Element && n.Name.ToLowerInvariant() = "li")
                |> Seq.map (fun li ->
                    let hasBlockChild =
                        li.ChildNodes
                        |> Seq.exists (fun n ->
                            n.NodeType = HtmlNodeType.Element && blockTags.Contains(n.Name.ToLowerInvariant()))
                    let content =
                        if hasBlockChild then li.ChildNodes |> Seq.collect mapBlock |> List.ofSeq
                        else [ Paragraph(inlineChildren li, None) ]
                    { Content = content })
                |> List.ofSeq
            let start =
                match node.GetAttributeValue("start", 0) with
                | n when ordered && n > 1 -> Some n
                | _ -> None
            ListBlock { Ordered = ordered; Start = start; Items = items }

        and mapTable (node: HtmlNode) : Block =
            let mapRow (tr: HtmlNode) =
                let isHeader = tr.ChildNodes |> Seq.exists (fun n -> n.Name.ToLowerInvariant() = "th")
                let cells =
                    tr.ChildNodes
                    |> Seq.filter (fun n ->
                        let nm = n.Name.ToLowerInvariant()
                        nm = "td" || nm = "th")
                    |> Seq.map (fun c ->
                        { Content = [ Paragraph(inlineChildren c, None) ]
                          ColSpan = max 1 (c.GetAttributeValue("colspan", 1))
                          RowSpan = max 1 (c.GetAttributeValue("rowspan", 1))
                          Style = None })
                    |> List.ofSeq
                isHeader, { Cells = cells }
            let rows = node.Descendants("tr") |> Seq.map mapRow |> List.ofSeq
            let header = rows |> List.tryFind fst |> Option.map snd
            let body = rows |> List.filter (fst >> not) |> List.map snd
            let colCount =
                match header with
                | Some h -> h.Cells.Length
                | None -> body |> List.tryHead |> Option.map (fun r -> r.Cells.Length) |> Option.defaultValue 0
            Table { Columns = List.replicate colCount { Width = None; Align = None }; Header = header; Rows = body; Style = None }

        let parse (html: string) : Document =
            let doc = HtmlDocument()
            doc.LoadHtml(html)
            let titleNode = doc.DocumentNode.SelectSingleNode("//title")
            let title =
                if isNull titleNode then None
                else
                    let t = (decode titleNode.InnerText).Trim()
                    if t = "" then None else Some t
            let root =
                let body = doc.DocumentNode.SelectSingleNode("//body")
                if isNull body then doc.DocumentNode else body
            let blocks = root.ChildNodes |> Seq.collect mapBlock |> List.ofSeq
            { Document.OfBlocks blocks with Metadata = { DocumentMetadata.Empty with Title = title } }

    /// Reads HTML into a fluid `Document` using the HtmlAgilityPack DOM parser.
    type Reader() =
        interface IDocumentReader with
            member _.MediaTypes = [ MediaType ]
            member _.Read(input, _ctx) =
                use r = new StreamReader(input, Encoding.UTF8, true, 1024, leaveOpen = true)
                HapMap.parse (r.ReadToEnd())
