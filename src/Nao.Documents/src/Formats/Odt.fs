namespace Nao.Documents

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Xml.Linq

/// Reader and writer for OpenDocument Text packages (`.odt`).
module Odt =

    [<Literal>]
    let MediaType = "application/vnd.oasis.opendocument.text"

    let private office =
        XNamespace.Get "urn:oasis:names:tc:opendocument:xmlns:office:1.0"

    let private style = XNamespace.Get "urn:oasis:names:tc:opendocument:xmlns:style:1.0"
    let private text = XNamespace.Get "urn:oasis:names:tc:opendocument:xmlns:text:1.0"
    let private table = XNamespace.Get "urn:oasis:names:tc:opendocument:xmlns:table:1.0"

    let private manifest =
        XNamespace.Get "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0"

    let private xlink = XNamespace.Get "http://www.w3.org/1999/xlink"

    let private q (ns: XNamespace) name = ns + name

    let private styleName (s: TextStyle) =
        let parts =
            [ if s.Weight = Some Bold || s.Weight = Some Black || s.Weight = Some SemiBold then
                  "Bold"
              if s.Style = Some Italic || s.Style = Some Oblique then
                  "Italic"
              if s.Decorations |> List.contains Underline then
                  "Underline"
              if s.Decorations |> List.contains LineThrough then
                  "Strike" ]

        if parts.IsEmpty then None else Some(String.concat "" parts)

    let private textStyle (name: string) =
        let style = TextStyle.Default

        { style with
            Weight = if name.Contains "Bold" then Some Bold else None
            Style = if name.Contains "Italic" then Some Italic else None
            Decorations =
                [ if name.Contains "Underline" then
                      Underline
                  if name.Contains "Strike" then
                      LineThrough ] }

    let private styleNames =
        [ "Bold"; "Italic"; "Underline"; "Strike" ]
        |> List.collect (fun name -> [ name; name + "Bold"; name + "Italic" ])
        |> List.append
            [ "BoldItalic"
              "BoldUnderline"
              "BoldStrike"
              "ItalicUnderline"
              "ItalicStrike"
              "UnderlineStrike"
              "BoldItalicUnderline"
              "BoldItalicStrike"
              "BoldUnderlineStrike"
              "ItalicUnderlineStrike"
              "BoldItalicUnderlineStrike"
              "Code" ]
        |> List.distinct

    let private styleAttributes (name: string) =
        [ yield XAttribute(q style "family", "text")
          if name.Contains "Bold" then
              yield XAttribute(q style "font-weight", "bold")
          if name.Contains "Italic" then
              yield XAttribute(q style "font-style", "italic")
          if name.Contains "Underline" then
              yield XAttribute(q style "text-underline-style", "solid")
          if name.Contains "Strike" then
              yield XAttribute(q style "text-line-through-style", "solid")
          if name = "Code" then
              yield XAttribute(q style "font-family", "monospace") ]

    let private styledContent =
        styleNames |> List.map (fun name -> name, styleAttributes name)

    let rec private renderInline (i: Inline) : XNode list =
        match i with
        | Run(value, textStyle) ->
            match styleName textStyle with
            | Some name -> [ XElement(q text "span", XAttribute(q text "style-name", name), XText value) ]
            | None -> [ XText value :> XNode ]
        | LineBreak -> [ XElement(q text "line-break") ]
        | InlineCode value -> [ XElement(q text "span", XAttribute(q text "style-name", "Code"), XText value) ]
        | Link(children, href) ->
            [ XElement(
                  q text "a",
                  XAttribute(q xlink "href", href),
                  children |> List.collect renderInline |> List.map box
              ) ]
        | InlineImage(_, alt, _) -> [ XText(defaultArg alt "[image]") :> XNode ]
        | InlineRaw(_, value) -> [ XText value :> XNode ]

    let private renderParagraph (name: string) (inlines: Inline list) =
        XElement(q text name, inlines |> List.collect renderInline |> List.map box)

    let private renderCell (cell: TableCell) =
        XElement(
            q table "table-cell",
            cell.Content
            |> List.collect (function
                | Paragraph(inlines, _) -> [ renderParagraph "p" inlines ]
                | Heading(_, inlines) -> [ renderParagraph "p" inlines ]
                | _ -> [])
            |> List.map box
        )

    let rec private renderBlock (block: Block) : XElement list =
        match block with
        | Heading(level, inlines) ->
            [ XElement(
                  q text "h",
                  XAttribute(q text "outline-level", max 1 level),
                  inlines |> List.collect renderInline |> List.map box
              ) ]
        | Paragraph(inlines, _) -> [ renderParagraph "p" inlines ]
        | Table spec ->
            let rows =
                [ yield! spec.Header |> Option.toList; yield! spec.Rows ]
                |> List.map (fun row -> XElement(q table "table-row", row.Cells |> List.map (renderCell >> box)))

            [ XElement(q table "table", XAttribute(q table "name", "Table1"), rows |> List.map box) ]
        | ListBlock spec ->
            let items =
                spec.Items
                |> List.map (fun item ->
                    XElement(q text "list-item", item.Content |> List.collect renderBlock |> List.map box))

            [ XElement(q text "list", items |> List.map box) ]
        | Quote blocks ->
            [ XElement(
                  q text "p",
                  XText(InlineText.ofInlines [ Run("> ", TextStyle.Default) ]),
                  blocks |> List.collect renderBlock |> List.map box
              ) ]
        | CodeBlock(_, code) -> [ renderParagraph "p" [ InlineCode code ] ]
        | ThematicBreak -> [ renderParagraph "p" [ Run("----------", TextStyle.Default) ] ]
        | Container spec -> spec.Children |> List.collect (fun child -> renderBlock child.Content)
        | Media spec -> [ renderParagraph "p" [ Run(defaultArg spec.Alt "[media]", TextStyle.Default) ] ]
        | BlockRaw(_, value) -> [ renderParagraph "p" [ Run(value, TextStyle.Default) ] ]

    let private contentXml (doc: Document) =
        let automaticStyles =
            styledContent
            |> List.map (fun (name, attrs) ->
                XElement(q style "style", XAttribute(q style "name", name), attrs |> List.map box))

        let body = Document.toBlocks doc |> List.collect renderBlock |> List.map box

        XDocument(
            XElement(
                q office "document-content",
                XAttribute(XNamespace.Xmlns + "office", office.NamespaceName),
                XAttribute(XNamespace.Xmlns + "style", style.NamespaceName),
                XAttribute(XNamespace.Xmlns + "text", text.NamespaceName),
                XAttribute(XNamespace.Xmlns + "table", table.NamespaceName),
                XAttribute(XNamespace.Xmlns + "xlink", xlink.NamespaceName),
                XAttribute(q office "version", "1.3"),
                XElement(q office "automatic-styles", automaticStyles |> List.map box),
                XElement(q office "body", XElement(q office "text", body))
            )
        )

    let private stylesXml () =
        XDocument(
            XElement(
                q office "document-styles",
                XAttribute(XNamespace.Xmlns + "office", office.NamespaceName),
                XAttribute(XNamespace.Xmlns + "style", style.NamespaceName),
                XAttribute(q office "version", "1.3"),
                XElement(q office "styles")
            )
        )

    let private manifestXml () =
        let entry path media =
            XElement(
                q manifest "file-entry",
                XAttribute(q manifest "full-path", path),
                XAttribute(q manifest "media-type", media)
            )

        XDocument(
            XElement(
                q manifest "manifest",
                XAttribute(XNamespace.Xmlns + "manifest", manifest.NamespaceName),
                XAttribute(q manifest "version", "1.3"),
                entry "/" MediaType,
                entry "content.xml" "text/xml",
                entry "styles.xml" "text/xml",
                entry "meta.xml" "text/xml"
            )
        )

    let private addEntry (archive: ZipArchive) (name: string) (bytes: byte[]) (level: CompressionLevel) =
        let entry = archive.CreateEntry(name, level)
        use stream = entry.Open()
        stream.Write(bytes, 0, bytes.Length)

    type Writer() =
        interface IDocumentWriter with
            member _.MediaTypes = [ MediaType ]

            member _.Write(doc, output, _ctx) =
                use archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen = true)
                addEntry archive "mimetype" (Encoding.UTF8.GetBytes MediaType) CompressionLevel.NoCompression

                addEntry
                    archive
                    "content.xml"
                    (Encoding.UTF8.GetBytes((contentXml doc).ToString()))
                    CompressionLevel.Optimal

                addEntry archive "styles.xml" (Encoding.UTF8.GetBytes(stylesXml().ToString())) CompressionLevel.Optimal

                addEntry
                    archive
                    "meta.xml"
                    (Encoding.UTF8.GetBytes(
                        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-meta xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" office:version=\"1.3\"><office:meta/></office:document-meta>"
                    ))
                    CompressionLevel.Optimal

                addEntry
                    archive
                    "META-INF/manifest.xml"
                    (Encoding.UTF8.GetBytes(manifestXml().ToString()))
                    CompressionLevel.Optimal

    let rec private inlineContent (element: XElement) =
        element.Nodes()
        |> Seq.collect (fun node ->
            match node with
            | :? XText as value -> [ Run(value.Value, TextStyle.Default) ]
            | :? XElement as child when child.Name = q text "line-break" -> [ LineBreak ]
            | :? XElement as child when child.Name = q text "span" ->
                let style =
                    child.Attribute(q text "style-name")
                    |> Option.ofObj
                    |> Option.map (fun x -> textStyle x.Value)
                    |> Option.defaultValue TextStyle.Default

                [ Run(InlineText.ofInlines (inlineContent child), style) ]
            | :? XElement as child when child.Name = q text "a" ->
                let href =
                    child.Attribute(q xlink "href")
                    |> Option.ofObj
                    |> Option.map (fun x -> x.Value)
                    |> Option.defaultValue ""

                [ Link(inlineContent child, href) ]
            | _ -> [])
        |> List.ofSeq

    let private cellFromElement (cell: XElement) =
        { Content =
            cell.Elements(q text "p")
            |> Seq.map (fun p -> Paragraph(inlineContent p, None))
            |> List.ofSeq
          ColSpan = 1
          RowSpan = 1
          Style = None }

    let rec private blockFromElement (element: XElement) =
        if element.Name = q text "h" then
            [ Heading(
                  element.Attribute(q text "outline-level")
                  |> Option.ofObj
                  |> Option.map (fun x -> Int32.Parse x.Value)
                  |> Option.defaultValue 1,
                  inlineContent element
              ) ]
        elif element.Name = q text "p" then
            [ Paragraph(inlineContent element, None) ]
        elif element.Name = q text "list" then
            let items =
                element.Elements(q text "list-item")
                |> Seq.map (fun item -> { Content = item.Elements() |> Seq.collect blockFromElement |> List.ofSeq })
                |> List.ofSeq

            [ ListBlock
                  { Ordered = false
                    Start = None
                    Items = items } ]
        elif element.Name = q table "table" then
            let rows =
                element.Elements(q table "table-row")
                |> Seq.map (fun row ->
                    { Cells = row.Elements(q table "table-cell") |> Seq.map cellFromElement |> List.ofSeq })
                |> List.ofSeq

            match rows with
            | header :: body ->
                [ Table
                      { Columns = List.replicate header.Cells.Length { Width = None; Align = None }
                        Header = Some header
                        Rows = body
                        Style = None } ]
            | [] -> []
        else
            []

    type Reader() =
        interface IDocumentReader with
            member _.MediaTypes = [ MediaType ]

            member _.Read(input, _ctx) =
                use archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen = true)
                PackageValidation.validate _ctx.PackageReadLimits archive
                let entry = archive.GetEntry "content.xml"

                if isNull entry then
                    invalidArg "input" "ODT package is missing content.xml"

                use stream = entry.Open()
                let xml = XDocument.Load stream
                let body = xml.Root.Element(q office "body").Element(q office "text")

                let locatedBlocks =
                    body.Elements()
                    |> Seq.mapi (fun elementIndex element ->
                        blockFromElement element
                        |> List.map (fun block ->
                            block,
                            DocumentSourceLocation.PackagePath
                                { Part = "content.xml"
                                  Path = $"/office:document-content/office:body/office:text/*[{elementIndex + 1}]"
                                  Page = None }))
                    |> Seq.collect id
                    |> List.ofSeq

                { Document.OfBlocks(locatedBlocks |> List.map fst) with
                    Sources =
                        locatedBlocks
                        |> List.mapi (fun index (_, location) -> SourcePath.bodyBlock index, location)
                        |> Map.ofList }
