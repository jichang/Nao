namespace Nao.Documents.Tests

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Documents

[<TestClass>]
type DocumentEditorTests() =

    [<TestMethod>]
    member _.``editor builds styled fluid content``() =
        let titleStyle =
            { TextStyle.Default with
                FontSize = Some(Length.Px 18) }

        let bodyStyle =
            { TextStyle.Default with
                FontSize = Some(Length.Px 14) }

        let document: Document =
            match
                Document.Empty
                |> DocumentEditor.setTitle (Some "About AI models")
                |> DocumentEditor.appendHeading 1 "About AI models" titleStyle
                |> Result.bind (DocumentEditor.appendParagraph "Introduction" bodyStyle None)
            with
            | Ok document -> document
            | Error error -> failwithf "Unexpected edit error: %A" error

        Assert.AreEqual(Some "About AI models", document.Metadata.Title)

        match document.Body with
        | Fluid [ Heading(1, [ Run(title, headingStyle) ]); Paragraph([ Run(paragraph, paragraphStyle) ], None) ] ->
            Assert.AreEqual("About AI models", title)
            Assert.AreEqual(Some(Length.Px 18), headingStyle.FontSize)
            Assert.AreEqual("Introduction", paragraph)
            Assert.AreEqual(Some(Length.Px 14), paragraphStyle.FontSize)
        | body -> Assert.Fail(sprintf "Unexpected document body: %A" body)

    [<TestMethod>]
    member _.``editor rejects invalid heading level``() =
        let result =
            DocumentEditor.appendHeading 7 "Invalid" TextStyle.Default Document.Empty

        Assert.AreEqual(Error(DocumentEditError.InvalidHeadingLevel 7), result)

    [<TestMethod>]
    member _.``builders cover every content entity and style attribute``() =
        let foreground = Color.Of "#123456"
        let background = Color.Of "#eeeeee"

        let textStyle =
            TextStyleBuilder.empty
            |> TextStyleBuilder.fontFamily (Some "Aptos")
            |> TextStyleBuilder.fontSize (Some(Length.Px 14))
            |> TextStyleBuilder.weight (Some FontWeight.Bold)
            |> TextStyleBuilder.fontStyle (Some FontStyleKind.Italic)
            |> TextStyleBuilder.decorations [ Underline; LineThrough; Overline ]
            |> TextStyleBuilder.color (Some foreground)
            |> TextStyleBuilder.background (Some background)
            |> TextStyleBuilder.feature "letter-spacing" "1px"

        let border = BorderBuilder.create (Length.Px 1) foreground Solid (Length.Px 4)

        let boxStyle =
            BoxStyleBuilder.empty
            |> BoxStyleBuilder.background (Some background)
            |> BoxStyleBuilder.border (Some border)
            |> BoxStyleBuilder.padding (Some(Edges.All(Length.Px 8)))
            |> BoxStyleBuilder.margin (Some(Edges.Axes(Length.Px 4, Length.Px 6)))
            |> BoxStyleBuilder.width (Some(Length.Percent 100))
            |> BoxStyleBuilder.height (Some(Length.Px 80))
            |> BoxStyleBuilder.minWidth (Some(Length.Px 20))
            |> BoxStyleBuilder.maxWidth (Some(Length.Px 400))
            |> BoxStyleBuilder.textAlign (Some TextAlign.Center)

        let rect =
            { X = Length.Px 1
              Y = Length.Px 2
              Width = Length.Px 30
              Height = Length.Px 40 }

        let placement =
            PlacementBuilder.empty
            |> PlacementBuilder.column (Some 1)
            |> PlacementBuilder.columnSpan (Some 2)
            |> PlacementBuilder.row (Some 3)
            |> PlacementBuilder.rowSpan (Some 4)
            |> PlacementBuilder.grow (Some 1)
            |> PlacementBuilder.shrink (Some 0)
            |> PlacementBuilder.basis (Some(Length.Percent 50))
            |> PlacementBuilder.order (Some 5)
            |> PlacementBuilder.rect (Some rect)
            |> PlacementBuilder.alignSelf (Some Align.End)

        let resourceId = ResourceId.Of "image-1"

        let size =
            { Width = Length.Px 320
              Height = Length.Px 180 }

        let resource =
            ResourceBuilder.create resourceId ResourceKind.Image "image/png"
            |> ResourceBuilder.localPath (Some "resources/image.png")
            |> ResourceBuilder.uri (Some "https://example.com/image.png")
            |> ResourceBuilder.sha256 (Some "abc123")
            |> ResourceBuilder.pixelWidth (Some 320)
            |> ResourceBuilder.pixelHeight (Some 180)
            |> ResourceBuilder.durationMs None
            |> ResourceBuilder.metadataItem "source" "test"

        let inlines =
            [ InlineBuilder.run "styled" textStyle
              InlineBuilder.text "plain"
              InlineBuilder.lineBreak
              InlineBuilder.link [ InlineBuilder.text "link" ] "https://example.com"
              InlineBuilder.image resourceId (Some "alt") (Some size)
              InlineBuilder.code "value"
              InlineBuilder.raw "html" "<span />" ]

        let paragraph = BlockBuilder.paragraph inlines (Some TextAlign.Justify)

        let table =
            TableBuilder.create
                [ TableBuilder.column (Some(Length.Fr 1)) (Some TextAlign.Left) ]
                (Some(TableBuilder.row [ TableBuilder.cell [ paragraph ] 1 1 (Some boxStyle) ]))
                [ TableBuilder.row [ TableBuilder.cell [ paragraph ] 2 3 None ] ]
                (Some boxStyle)

        let media = MediaBuilder.create resourceId (Some "alt") inlines (Some size)

        let container =
            LayoutBuilder.container
                (Grid(LayoutOptionsBuilder.grid [ Length.Fr 1; Length.Auto ] [] (Length.Px 8) (Length.Px 8)))
                [ LayoutBuilder.child paragraph placement ]
                (Some boxStyle)

        let blocks =
            [ paragraph
              BlockBuilder.heading 2 inlines
              BlockBuilder.list (ListBuilder.create true (Some 3) [ ListBuilder.item [ paragraph ] ])
              BlockBuilder.table table
              BlockBuilder.code (Some "fsharp") "let value = 1"
              BlockBuilder.quote [ paragraph ]
              BlockBuilder.media media
              BlockBuilder.container container
              BlockBuilder.thematicBreak
              BlockBuilder.raw "custom" "payload" ]

        let metadata =
            MetadataBuilder.create (Some "All entities") [ "Author" ] (Some "en") None None Map.empty

        let setup = PageBuilder.setup (Length.Pt 100) (Length.Pt 200) Portrait Edges.Zero
        let page = PageBuilder.create setup [ paragraph ] [ paragraph ] blocks
        let document = DocumentBuilder.paged metadata [ resource ] [ page ] (Some setup)

        Assert.AreEqual(7, inlines.Length)
        Assert.AreEqual(10, blocks.Length)
        Assert.AreEqual(Some "Aptos", textStyle.FontFamily)
        Assert.AreEqual(Some border, boxStyle.Border)
        Assert.AreEqual(Some 2, placement.ColumnSpan)
        Assert.AreEqual(Some rect, placement.Rect)
        Assert.AreEqual(Some 320, resource.PixelWidth)
        Assert.AreEqual(Some "test", resource.Metadata |> Map.tryFind "source")
        Assert.AreEqual(Some "All entities", document.Metadata.Title)

        match document.Body with
        | Paged [ builtPage ] -> Assert.AreEqual(10, builtPage.Content.Length)
        | body -> Assert.Fail(sprintf "Unexpected built body: %A" body)

    [<TestMethod>]
    member _.``editor updates metadata resources and arbitrary fluid blocks``() =
        let created = DateTimeOffset(2026, 7, 30, 8, 0, 0, TimeSpan.Zero)

        let resource =
            Resource.Local("image-1", ResourceKind.Image, "image/png", "resources/image.png")

        let paragraph = BlockBuilder.paragraph [ InlineBuilder.text "first" ] None
        let heading = BlockBuilder.heading 1 [ InlineBuilder.text "title" ]

        let edited =
            Document.Empty
            |> DocumentEditor.setTitle (Some "Document")
            |> DocumentEditor.setAuthors [ "Ada"; "Linus" ]
            |> DocumentEditor.setLanguage (Some "en-US")
            |> DocumentEditor.setCreated (Some created)
            |> DocumentEditor.setModified (Some created)
            |> DocumentEditor.setProperty "subject" "API coverage"
            |> DocumentEditor.setDefaultPage (Some PageSetup.Letter)
            |> DocumentEditor.addResource resource
            |> Result.bind (DocumentEditor.appendBlocks [ paragraph; heading ])
            |> Result.bind (DocumentEditor.insertBlock 1 BlockBuilder.thematicBreak)
            |> Result.bind (DocumentEditor.updateBlock 2 (fun _ -> BlockBuilder.code None "updated"))
            |> Result.bind (DocumentEditor.removeBlock 0)

        match edited with
        | Error error -> Assert.Fail(sprintf "Unexpected edit error: %A" error)
        | Ok document ->
            Assert.AreEqual(Some "Document", document.Metadata.Title)
            CollectionAssert.AreEqual([| "Ada"; "Linus" |], document.Metadata.Authors |> List.toArray)
            Assert.AreEqual(Some "en-US", document.Metadata.Language)
            Assert.AreEqual(Some created, document.Metadata.Created)
            Assert.AreEqual(Some "API coverage", document.Metadata.Properties |> Map.tryFind "subject")
            Assert.AreEqual(Some PageSetup.Letter, document.DefaultPage)
            Assert.AreEqual(1, document.Resources.Length)

            match document.Body with
            | Fluid [ ThematicBreak; CodeBlock(None, "updated") ] -> ()
            | body -> Assert.Fail(sprintf "Unexpected document body: %A" body)

        Assert.AreEqual(
            Error(DocumentEditError.DuplicateResourceId resource.Id),
            Document.Empty
            |> DocumentEditor.addResource resource
            |> Result.bind (DocumentEditor.addResource resource)
        )

        Assert.AreEqual(Error(DocumentEditError.InvalidBlockIndex 1), DocumentEditor.removeBlock 1 Document.Empty)

    [<TestMethod>]
    member _.``editor updates arbitrary paged content``() =
        let first =
            Page.Of(PageSetup.A4, [ BlockBuilder.paragraph [ InlineBuilder.text "one" ] None ])

        let second =
            Page.Of(
                PageSetup.Slide16x9,
                [ BlockBuilder.media (MediaBuilder.create (ResourceId.Of "video") None [] None) ]
            )

        let document = Document.OfPages [ first ]

        let edited =
            document
            |> DocumentEditor.appendPage second
            |> Result.bind (
                DocumentEditor.updatePage 0 (fun page ->
                    { page with
                        Footer = [ BlockBuilder.raw "field" "page-number" ] })
            )
            |> Result.bind (DocumentEditor.removePage 1)

        match edited with
        | Ok { Body = Paged [ page ] } -> Assert.AreEqual(1, page.Footer.Length)
        | result -> Assert.Fail(sprintf "Unexpected paged result: %A" result)

        Assert.AreEqual(Error DocumentEditError.RequiresPagedBody, DocumentEditor.appendPage first Document.Empty)

        Assert.AreEqual(
            Error DocumentEditError.RequiresFluidBody,
            DocumentEditor.appendBlock BlockBuilder.thematicBreak document
        )

        Assert.AreEqual(Error(DocumentEditError.InvalidPageIndex 2), DocumentEditor.removePage 2 document)
