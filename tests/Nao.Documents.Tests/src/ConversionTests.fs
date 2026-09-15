namespace Nao.Documents.Tests

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Text.Json
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Documents

type private ExpectedOutput =
    | ExactText of suffix: string
    | PackageManifest of expectedName: string
    | PdfManifest of expectedName: string

/// End-to-end conversion tests for the unified document model. These assert the same
/// things the old smoke-docs.fsx script printed: every md→target conversion produces
/// a well-formed artifact, and the binary formats round-trip back to Markdown.
[<TestClass>]
type ConversionTests() =

    static let sampleMarkdown =
        "# Sample Report\n\n"
        + "A short paragraph with **bold**, *italic* and `code`.\n\n"
        + "## Items\n\n"
        + "- First item\n"
        + "- Second item\n\n"
        + "### Code\n\n"
        + "```fsharp\nlet x = 42\n```\n\n"
        + "> A quoted line.\n\n"
        + "| Name | Score |\n| --- | --- |\n| Alice | 10 |\n| Bob | 7 |\n\n"
        + "---\n\n"
        + "Final paragraph.\n"

    let registry = Formats.fullRegistry ()
    let packageLimits = PackageReadLimits.create 4096 67108864L 268435456L 200.0
    let ctx = ConversionContext.InMemory packageLimits

    let bytesOf (s: string) = Encoding.UTF8.GetBytes s

    let convert (src: string) (dst: string) (input: byte[]) =
        Converter.convertBytes registry src dst ctx input

    let mdToBytes (dst: string) =
        convert Markdown.MediaType dst (bytesOf sampleMarkdown)

    let fixturePath name =
        Path.Combine(System.AppContext.BaseDirectory, "Fixtures", name)

    let fixtureBytes name = File.ReadAllBytes(fixturePath name)

    let fixtureText name =
        File.ReadAllText(fixturePath name, Encoding.UTF8)

    let normalizeLineEndings (text: string) =
        text.Replace("\r\n", "\n").Replace("\r", "\n")

    let expectedPath name = Path.Combine("Expected", name)

    let textOf (bytes: byte[]) =
        Encoding.UTF8.GetString(bytes) |> normalizeLineEndings

    let packageEntries (bytes: byte[]) =
        use ms = new MemoryStream(bytes)
        use archive = new ZipArchive(ms, ZipArchiveMode.Read)

        archive.Entries
        |> Seq.map (fun e -> e.FullName)
        |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
        |> String.concat "\n"
        |> fun text -> text + "\n"

    let pdfManifest (bytes: byte[]) =
        let text = Encoding.Latin1.GetString bytes

        let firstLine =
            text.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryHead
            |> Option.defaultValue ""

        [ sprintf "header=%s" firstLine
          sprintf "has-xref=%b" (text.Contains "xref")
          sprintf "ends-eof=%b" (text.TrimEnd().EndsWith "%%EOF") ]
        |> String.concat "\n"
        |> fun text -> text + "\n"

    let assertMatchesExpected (expectedName: string) (actualText: string) (message: string) =
        let expected = fixtureText (expectedPath expectedName) |> normalizeLineEndings
        Assert.AreEqual<string>(expected, actualText, message)

    let fixtureInputs =
        [ "rich-sample.md", Markdown.MediaType
          "rich-sample.html", Html.MediaType
          "rich-sample.rtf", Rtf.MediaType
          "rich-sample.signedbytedoc", SignedByteDoc.MediaType ]

    let supportedTargets =
        [ PlainText.MediaType, ExactText "txt"
          Markdown.MediaType, ExactText "md"
          Html.MediaType, ExactText "html"
          SignedByteDoc.MediaType, ExactText "signedbytedoc"
          Docx.MediaType, PackageManifest "docx.entries.txt"
          Pdf.MediaType, PdfManifest "pdf.manifest.txt"
          Xlsx.MediaType, PackageManifest "xlsx.entries.txt"
          Pptx.MediaType, PackageManifest "pptx.entries.txt" ]

    /// Assert an OOXML (zip) package opens and contains every required part.
    let assertPackageParts (bytes: byte[]) (required: string list) =
        use ms = new MemoryStream(bytes)
        use archive = new ZipArchive(ms, ZipArchiveMode.Read)
        let entries = archive.Entries |> Seq.map (fun e -> e.FullName) |> Set.ofSeq

        for part in required do
            Assert.IsTrue(entries.Contains part, sprintf "Missing OOXML part: %s" part)

    [<TestMethod>]
    member _.RegistryExposesExpectedReadableTypes() =
        let readable = registry.ReadableTypes

        for mt in
            [ PlainText.MediaType
              Csv.MediaType
              Markdown.MediaType
              Html.MediaType
              Rtf.MediaType
              Rtf.TextMediaType
              Docx.MediaType
              Odt.MediaType
              Pdf.MediaType
              SignedByteDoc.MediaType ] do
            Assert.IsTrue(List.contains mt readable, sprintf "Expected readable type: %s" mt)

    [<TestMethod>]
    member _.RegistryExposesExpectedWritableTypes() =
        let writable = registry.WritableTypes

        for mt in
            [ PlainText.MediaType
              Csv.MediaType
              Markdown.MediaType
              Html.MediaType
              SignedByteDoc.MediaType
              Docx.MediaType
              Odt.MediaType
              Pdf.MediaType
              Xlsx.MediaType
              Pptx.MediaType ] do
            Assert.IsTrue(List.contains mt writable, sprintf "Expected writable type: %s" mt)

    [<TestMethod>]
    member _.MarkdownConvertsToEveryTarget_ProducesNonEmptyOutput() =
        for target, _ in supportedTargets do
            let bytes = mdToBytes target
            Assert.IsTrue(bytes.Length > 0, sprintf "Empty output for target %s" target)

    [<TestMethod>]
    member _.FixtureFilesAreCopiedToTestOutput() =
        let expectedTextFiles =
            fixtureInputs
            |> List.collect (fun (inputName, _) ->
                supportedTargets
                |> List.choose (function
                    | _, ExactText suffix -> Some(expectedPath (sprintf "%s.to.%s" inputName suffix))
                    | _, PackageManifest expectedName -> Some(expectedPath expectedName)
                    | _, PdfManifest expectedName -> Some(expectedPath expectedName)))
            |> List.distinct

        for name in (fixtureInputs |> List.map fst) @ expectedTextFiles do
            Assert.IsTrue(File.Exists(fixturePath name), sprintf "Missing fixture: %s" name)

    [<TestMethod>]
    member _.EachFixtureInputConvertsToEverySupportedTargetAndMatchesExpectedFiles() =
        for inputName, sourceType in fixtureInputs do
            for targetType, expectation in supportedTargets do
                let actual = convert sourceType targetType (fixtureBytes inputName)
                let message = sprintf "%s -> %s did not match expected output" inputName targetType

                match expectation with
                | ExactText suffix ->
                    assertMatchesExpected (sprintf "%s.to.%s" inputName suffix) (textOf actual) message
                | PackageManifest expectedName -> assertMatchesExpected expectedName (packageEntries actual) message
                | PdfManifest expectedName -> assertMatchesExpected expectedName (pdfManifest actual) message

    [<TestMethod>]
    member _.RichTextFixtureInputsConvertToHtmlAndPdf() =
        let cases =
            [ "rich-sample.md", Markdown.MediaType
              "rich-sample.html", Html.MediaType
              "rich-sample.rtf", Rtf.MediaType ]

        for fileName, sourceType in cases do
            let input = fixtureBytes fileName
            let html = Encoding.UTF8.GetString(convert sourceType Html.MediaType input)
            Assert.IsTrue(html.Contains "Rich Fixture Report", sprintf "%s -> HTML lost heading" fileName)
            Assert.IsTrue(html.Contains "bold text", sprintf "%s -> HTML lost styled text" fileName)
            let pdf = convert sourceType Pdf.MediaType input
            let pdfText = Encoding.Latin1.GetString pdf
            Assert.IsTrue(pdfText.StartsWith "%PDF-1.", sprintf "%s -> PDF missing header" fileName)
            Assert.IsTrue(pdfText.TrimEnd().EndsWith "%%EOF", sprintf "%s -> PDF missing trailer" fileName)

    [<TestMethod>]
    member _.RtfFixtureConvertsBackToMarkdown() =
        let md =
            Encoding.UTF8.GetString(convert Rtf.MediaType Markdown.MediaType (fixtureBytes "rich-sample.rtf"))

        Assert.IsTrue(md.Contains "Rich Fixture Report", "RTF fixture lost the heading")
        Assert.IsTrue(md.Contains "bold text", "RTF fixture lost bold text")
        Assert.IsTrue(md.Contains "italic text", "RTF fixture lost italic text")

    [<TestMethod>]
    member _.MarkdownToPlainText_ContainsTextContent() =
        let text = Encoding.UTF8.GetString(mdToBytes PlainText.MediaType)
        Assert.IsTrue(text.Contains "Sample Report", "Plain text lost the heading")
        Assert.IsTrue(text.Contains "First item", "Plain text lost the list item")

    [<TestMethod>]
    member _.CsvRoundTripsQuotedFields() =
        let csv =
            bytesOf "Name,Note\r\nAlice,\"likes apples, pears\"\r\nBob,\"line one\r\nline two\"\r\n"

        let markdown = Encoding.UTF8.GetString(convert Csv.MediaType Markdown.MediaType csv)
        Assert.IsTrue(markdown.Contains "Alice", "CSV lost the first row")
        Assert.IsTrue(markdown.Contains "likes apples, pears", "CSV lost the quoted comma")
        Assert.IsTrue(markdown.Contains "line one", "CSV lost the quoted newline content")

    [<TestMethod>]
    member _.MarkdownToCsvEscapesTableFields() =
        let markdown =
            bytesOf "| Name | Note |\n| --- | --- |\n| Alice | likes apples, pears |\n"

        let csv = Encoding.UTF8.GetString(convert Markdown.MediaType Csv.MediaType markdown)
        Assert.IsTrue(csv.Contains "\"likes apples, pears\"", "CSV did not quote a field containing a comma")

        let markdownAgain =
            Encoding.UTF8.GetString(convert Csv.MediaType Markdown.MediaType (Encoding.UTF8.GetBytes csv))

        Assert.IsTrue(markdownAgain.Contains "Alice", "CSV round-trip lost the table row")

    [<TestMethod>]
    member _.CsvCanUseCustomDelimiterAndNoHeader() =
        let options =
            { Csv.Options.Default with
                Delimiter = ';'
                HasHeader = false }

        let input = bytesOf "Alice;10\nBob;7\n"

        let document =
            let reader = Csv.Reader(options) :> IDocumentReader
            use stream = new MemoryStream(input)
            reader.Read(stream, ctx)

        match Document.toBlocks document with
        | [ Table spec ] ->
            Assert.IsNull(spec.Header)
            Assert.AreEqual(2, spec.Rows.Length)
        | _ -> Assert.Fail "CSV without a header should produce one table"

    [<TestMethod>]
    member _.TryConvertReturnsTypedErrors() =
        match Converter.tryConvertBytes registry "application/unknown" Markdown.MediaType ctx [||] with
        | Error(UnsupportedMediaType mediaType) -> Assert.AreEqual("application/unknown", mediaType)
        | result -> Assert.Fail(sprintf "Unexpected conversion result: %A" result)

        match Converter.tryConvertBytes registry Csv.MediaType Markdown.MediaType ctx (bytesOf "broken,\"csv") with
        | Error(InvalidInput(mediaType, _)) -> Assert.AreEqual(Csv.MediaType, mediaType)
        | result -> Assert.Fail(sprintf "Unexpected malformed CSV result: %A" result)

    [<TestMethod>]
    member _.TryConvertReportsFlattenedImageWarnings() =
        let markdown = bytesOf "![diagram](diagram.png)\n"

        match Converter.tryConvertBytesWithWarnings registry Markdown.MediaType PlainText.MediaType ctx markdown with
        | Ok(_, warnings) ->
            Assert.IsTrue(
                warnings
                |> List.exists (function
                    | UnsupportedBlock(mediaType, "image") when mediaType = PlainText.MediaType -> true
                    | _ -> false)
            )
        | Error error -> Assert.Fail(sprintf "Unexpected conversion error: %A" error)

    [<TestMethod>]
    member _.MarkdownToOdt_PackageHasRequiredParts() =
        assertPackageParts
            (mdToBytes Odt.MediaType)
            [ "mimetype"; "content.xml"; "styles.xml"; "meta.xml"; "META-INF/manifest.xml" ]

    [<TestMethod>]
    member _.OdtRoundTripsBackToMarkdown() =
        let odt = mdToBytes Odt.MediaType
        let markdown = Encoding.UTF8.GetString(convert Odt.MediaType Markdown.MediaType odt)
        Assert.IsTrue(markdown.Contains "Sample Report", "ODT lost the heading")
        Assert.IsTrue(markdown.Contains "First item", "ODT lost the list item")
        Assert.IsTrue(markdown.Contains "Alice", "ODT lost the table row")

    [<TestMethod>]
    member _.MarkdownToHtml_IsWellFormedHtml() =
        let html = Encoding.UTF8.GetString(mdToBytes Html.MediaType)
        Assert.IsTrue(html.Contains "<h1", "HTML missing <h1> heading")
        Assert.IsTrue(html.Contains "Sample Report", "HTML missing heading text")
        Assert.IsTrue(html.Contains "<li", "HTML missing list items")

    [<TestMethod>]
    member _.MarkdownToPdf_HasValidStructure() =
        let pdf = mdToBytes Pdf.MediaType
        let text = Encoding.Latin1.GetString pdf
        Assert.IsTrue(text.StartsWith "%PDF-1.", "PDF missing %PDF header")
        Assert.IsTrue(text.TrimEnd().EndsWith "%%EOF", "PDF missing %%EOF trailer")
        Assert.IsTrue(text.Contains "xref", "PDF missing cross-reference table")

    [<TestMethod>]
    member _.MarkdownToDocx_PackageHasRequiredParts() =
        assertPackageParts
            (mdToBytes Docx.MediaType)
            [ "[Content_Types].xml"; "_rels/.rels"; "word/document.xml"; "word/styles.xml" ]

    [<TestMethod>]
    member _.MarkdownToXlsx_PackageHasRequiredParts() =
        assertPackageParts
            (mdToBytes Xlsx.MediaType)
            [ "[Content_Types].xml"; "xl/workbook.xml"; "xl/worksheets/sheet1.xml" ]

    [<TestMethod>]
    member _.MarkdownToPptx_PackageHasRequiredParts() =
        assertPackageParts
            (mdToBytes Pptx.MediaType)
            [ "[Content_Types].xml"
              "ppt/presentation.xml"
              "ppt/slideMasters/slideMaster1.xml"
              "ppt/slideMasters/theme/theme1.xml" ]

    [<TestMethod>]
    member _.HtmlRoundTripsBackToMarkdown() =
        let html = mdToBytes Html.MediaType
        let md = Encoding.UTF8.GetString(convert Html.MediaType Markdown.MediaType html)
        Assert.IsTrue(md.Contains "Sample Report", "HTML→Markdown lost the heading")
        Assert.IsTrue(md.Contains "First item", "HTML→Markdown lost the list item")

    [<TestMethod>]
    member _.DocxRoundTripsBackToMarkdown() =
        let docx = mdToBytes Docx.MediaType
        let md = Encoding.UTF8.GetString(convert Docx.MediaType Markdown.MediaType docx)
        Assert.IsTrue(md.Contains "Sample Report", "DOCX→Markdown lost the heading")
        Assert.IsTrue(md.Contains "First item", "DOCX→Markdown lost the list item")

    [<TestMethod>]
    member _.NativeJsonApiRoundTripsDocument() =
        let original = Document.OfBlocks [ Doc.heading 1 "JSON API"; Doc.para "Round-trip" ]
        let json = SignedByteDoc.serialize original
        Assert.IsFalse(json.Contains('\n'), "Native document JSON must remain compact")
        Assert.IsFalse(json.Contains('\r'), "Native document JSON must remain compact")
        use parsed = JsonDocument.Parse(json)
        Assert.AreEqual(Document.CurrentSchema, parsed.RootElement.GetProperty("Schema").GetString())
        let restored = SignedByteDoc.deserialize json
        Assert.AreEqual(original, restored)
        Assert.IsTrue(SignedByteDoc.isValid restored)

    [<TestMethod>]
    member _.NativeJsonSchemaIsExportedAsJson() =
        use parsed = JsonDocument.Parse(SignedByteDoc.JsonSchema)

        Assert.AreEqual(
            "https://json-schema.org/draft/2020-12/schema",
            parsed.RootElement.GetProperty("$schema").GetString()
        )

        Assert.AreEqual(
            Document.CurrentSchema,
            parsed.RootElement.GetProperty("properties").GetProperty("Schema").GetProperty("const").GetString()
        )

    [<TestMethod>]
    member _.NativeJsonValidationReportsInvalidSchema() =
        let invalid =
            { Document.Empty with
                Schema = "signedbyte-doc/unknown" }

        let errors = SignedByteDoc.validate invalid
        Assert.IsFalse(SignedByteDoc.isValid invalid)
        Assert.IsTrue(errors |> List.exists (fun error -> error.Contains "Schema"))
