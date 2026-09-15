namespace Nao.Documents.Tests

open System.IO
open System.IO.Compression
open System.Text
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Documents

[<TestClass>]
type SourceProvenanceTests() =
    let packageLimits = PackageReadLimits.create 4096 67108864L 268435456L 200.0

    let read (reader: IDocumentReader) (bytes: byte array) =
        use input = new MemoryStream(bytes)
        reader.Read(input, ConversionContext.InMemory packageLimits)

    [<TestMethod>]
    member _.``Markdown records exact UTF-8 byte character and line ranges``() =
        let text = "# Héading\n\nParagraph"

        let content =
            Array.append (Encoding.UTF8.Preamble.ToArray()) (Encoding.UTF8.GetBytes text)

        let document = read (Markdown.Reader()) content

        let source =
            match document.Sources.[SourcePath.bodyBlock 0] with
            | DocumentSourceLocation.ExactText source -> source
            | source -> failwithf "Expected exact text source, got %A." source

        Assert.AreEqual(3L, source.ByteOffset)
        Assert.AreEqual(int64 (Encoding.UTF8.GetByteCount "# Héading"), source.ByteLength)
        Assert.AreEqual(0, source.CharacterOffset)
        Assert.AreEqual("# Héading".Length, source.CharacterLength)
        Assert.AreEqual(1, source.LineStart)
        Assert.AreEqual(1, source.LineEnd)
        Assert.AreEqual("# Héading", text.Substring(source.CharacterOffset, source.CharacterLength))

    [<TestMethod>]
    member _.``HTML records the original outer-node range``() =
        let text = "<body><h1>Guide</h1><p>Text</p></body>"
        let document = read (Html.Reader()) (Encoding.UTF8.GetBytes text)

        let exact path =
            match document.Sources.[path] with
            | DocumentSourceLocation.ExactText source -> source
            | source -> failwithf "Expected exact text source, got %A." source

        let heading = exact (SourcePath.bodyBlock 0)
        let paragraph = exact (SourcePath.bodyBlock 1)

        Assert.AreEqual("<h1>Guide</h1>", text.Substring(heading.CharacterOffset, heading.CharacterLength))
        Assert.AreEqual("<p>Text</p>", text.Substring(paragraph.CharacterOffset, paragraph.CharacterLength))
        Assert.AreEqual(int64 heading.CharacterOffset, heading.ByteOffset)
        Assert.AreEqual(int64 heading.CharacterLength, heading.ByteLength)

    [<TestMethod>]
    member _.``DOCX and ODT record structural package paths``() =
        let source = Document.OfBlocks [ Doc.heading 1 "Guide"; Doc.para "Text" ]

        let roundTrip (writer: IDocumentWriter) (reader: IDocumentReader) expectedPart =
            use output = new MemoryStream()
            writer.Write(source, output, ConversionContext.InMemory packageLimits)
            let document = read reader (output.ToArray())

            match document.Sources.[SourcePath.bodyBlock 0] with
            | DocumentSourceLocation.PackagePath location ->
                Assert.AreEqual(expectedPart, location.Part)
                Assert.IsTrue(location.Path.StartsWith("/"))
            | location -> Assert.Fail($"Expected structural package source, got {location}.")

        roundTrip (Docx.Writer()) (Docx.Reader()) "word/document.xml"
        roundTrip (Odt.Writer()) (Odt.Reader()) "content.xml"

    [<TestMethod>]
    member _.``PDF records page number and geometric bounds``() =
        let source = Document.OfBlocks [ Doc.heading 1 "Guide"; Doc.para "Page text" ]
        use output = new MemoryStream()
        (Pdf.Writer() :> IDocumentWriter).Write(source, output, ConversionContext.InMemory packageLimits)
        let document = read (Pdf.Reader()) (output.ToArray())

        match document.Body with
        | Paged pages -> Assert.AreEqual(1, pages.Length)
        | Fluid _ -> Assert.Fail "Expected a paginated PDF document."

        match document.Sources.[SourcePath.pageContent 1 0] with
        | DocumentSourceLocation.PageRegion location ->
            Assert.AreEqual(1, location.Page)
            Assert.AreEqual(PageCoordinateOrigin.BottomLeft, location.Origin)
            Assert.IsTrue(location.Bounds.Width.Value > 0.0)
            Assert.IsTrue(location.Bounds.Height.Value > 0.0)
        | location -> Assert.Fail($"Expected PDF page-region source, got {location}.")

    [<TestMethod>]
    member _.``Compressed package expansion is rejected before XML parsing``() =
        use output = new MemoryStream()

        use archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen = true)

        let entry = archive.CreateEntry("content.xml", CompressionLevel.SmallestSize)
        use stream = entry.Open()
        let content = Encoding.UTF8.GetBytes(String.replicate 10000 "x")
        stream.Write content
        stream.Dispose()
        archive.Dispose()

        let strictLimits = PackageReadLimits.create 8 20000L 20000L 2.0
        use input = new MemoryStream(output.ToArray())

        Assert.ThrowsExactly<PackageLimitExceededException>(fun () ->
            (Odt.Reader() :> IDocumentReader).Read(input, ConversionContext.InMemory strictLimits)
            |> ignore)
        |> ignore
