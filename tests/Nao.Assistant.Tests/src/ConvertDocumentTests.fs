namespace Nao.Assistant.Tests

open System
open System.IO
open System.Text.Json
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Assistant

/// Regression tests for the `convert_document` tool's target resolution.
///
/// These pin the fix for the bug where "convert markdown to pdf" was carried out in the
/// WRONG direction (the tool inferred a pdf→markdown conversion). The source's extension
/// must determine the SOURCE format and the target token the TARGET format — never the
/// reverse — and a bare format name ("pdf") must derive an output filename from the source
/// rather than producing a file literally named "pdf" or silently falling back to text.
[<TestClass>]
type ConvertDocumentTests() =

    static let mutable workspace = ""

    /// Point the tool's fallback workspace at an isolated temp dir BEFORE any test runs:
    /// AssistantTools resolves its work dir once, on first use, so the env var must be set
    /// before the module is ever touched.
    [<AssemblyInitialize>]
    static member Init(_ctx: TestContext) =
        let dataDir =
            Path.Combine(Path.GetTempPath(), "nao-convert-tests", Guid.NewGuid().ToString("N"))

        Environment.SetEnvironmentVariable("NAO_DATA_DIR", dataDir)
        workspace <- Path.Combine(dataDir, "workspace")
        Directory.CreateDirectory workspace |> ignore

    member private _.WriteSource (name: string) (content: string) =
        File.WriteAllText(Path.Combine(workspace, name), content)

    member private _.RunTool (tool: Tool) (input: string) =
        match
            tool.RunAsync AgentContext.allowAll input
            |> fun task -> task.GetAwaiter().GetResult()
        with
        | Ok output -> output
        | Error failure ->
            Assert.Fail(failure.Message)
            ""

    member private this.Convert(input: string) =
        let json = this.RunTool AssistantTools.convertDocument input
        JsonDocument.Parse(json).RootElement

    member private this.WriteDocument(input: string) =
        let json = this.RunTool AssistantTools.writeDocument input
        JsonDocument.Parse(json).RootElement

    [<TestMethod>]
    member _.ServerTools_AreRegisteredOnlyWhenAgentDeclaresThem() =
        Assert.AreEqual(0, AssistantTools.toolsForAgentDeclarations [] |> List.length)

        let declared = AssistantTools.toolsForAgentDeclarations [ "write_document" ]
        Assert.AreEqual(1, declared.Length)
        Assert.AreEqual("write_document", declared.Head.Name)

    [<TestMethod>]
    member this.MarkdownToPdf_ConvertsInTheRequestedDirection() =
        this.WriteSource "report.md" "# Sample Report\n\nFirst item.\n"
        let result = this.Convert """{"source":"report.md","target":"pdf"}"""
        // The source is markdown and the target is pdf — not the reverse.
        Assert.AreEqual(SignedByte.Documents.Markdown.MediaType, result.GetProperty("from").GetString())
        Assert.AreEqual(SignedByte.Documents.Pdf.MediaType, result.GetProperty("to").GetString())

    [<TestMethod>]
    member this.BareFormatTarget_DerivesOutputNameFromSource() =
        this.WriteSource "notes.md" "# Notes\n\nBody.\n"
        let result = this.Convert """{"source":"notes.md","target":"pdf"}"""
        let converted = result.GetProperty("converted").GetString()
        // The output is named after the source, not a file literally called "pdf".
        Assert.IsTrue(converted.EndsWith("notes.pdf"), sprintf "Unexpected output path: %s" converted)
        Assert.IsTrue(File.Exists(Path.Combine(workspace, "notes.pdf")))

    [<TestMethod>]
    member this.ObjectFieldFragmentParams_AreAcceptedForModelRepairTolerance() =
        this.WriteSource "fragment.md" "# Fragment\n\nBody.\n"
        let result = this.Convert "\"source\":\"fragment.md\",\"target\":\"pdf\""
        let converted = result.GetProperty("converted").GetString()
        Assert.AreEqual(SignedByte.Documents.Markdown.MediaType, result.GetProperty("from").GetString())
        Assert.AreEqual(SignedByte.Documents.Pdf.MediaType, result.GetProperty("to").GetString())
        Assert.IsTrue(converted.EndsWith("fragment.pdf"), sprintf "Unexpected output path: %s" converted)
        Assert.IsTrue(File.Exists(Path.Combine(workspace, "fragment.pdf")))

    [<TestMethod>]
    member this.NaoDocTargetAndSource_UseUnifiedDocumentExtension() =
        this.WriteSource "model-source.md" "# Model Source\n\nBody.\n"
        let written = this.Convert """{"source":"model-source.md","target":"naodoc"}"""
        let modelPath = Path.Combine(workspace, "model-source.naodoc")
        Assert.AreEqual(SignedByte.Documents.Markdown.MediaType, written.GetProperty("from").GetString())
        Assert.AreEqual(SignedByte.Documents.SignedByteDoc.MediaType, written.GetProperty("to").GetString())
        Assert.IsTrue(File.Exists modelPath)

        let readBack = this.Convert """{"source":"model-source.naodoc","target":"pdf"}"""
        let converted = readBack.GetProperty("converted").GetString()
        Assert.AreEqual(SignedByte.Documents.SignedByteDoc.MediaType, readBack.GetProperty("from").GetString())
        Assert.AreEqual(SignedByte.Documents.Pdf.MediaType, readBack.GetProperty("to").GetString())
        Assert.IsTrue(converted.EndsWith("model-source.pdf"), sprintf "Unexpected output path: %s" converted)

    [<TestMethod>]
    member this.WriteDocument_CreatesDesignedNaoDocAndConvertsToHtml() =
        let request =
            """{"target":"designed","document":{"Schema":"nao-doc/1","Metadata":{"Title":"Designed Document","Authors":[],"Language":null,"Created":null,"Modified":null,"Properties":{}},"Resources":[],"Body":{"Case":"Fluid","Fields":[[{"Case":"Heading","Fields":[1,[{"Case":"Run","Fields":["Designed Document",{"FontFamily":"Aptos Display","FontSize":{"Value":24,"Unit":{"Case":"Pt"}},"Weight":{"Case":"Bold"},"Style":null,"Decorations":[],"Color":null,"Background":null,"Features":{}}]}]]},{"Case":"Heading","Fields":[2,[{"Case":"Run","Fields":["First Section",{"FontFamily":null,"FontSize":{"Value":16,"Unit":{"Case":"Pt"}},"Weight":null,"Style":null,"Decorations":[],"Color":null,"Background":null,"Features":{}}]}]]},{"Case":"Paragraph","Fields":[[{"Case":"Run","Fields":["This section was generated from requirements.",{"FontFamily":null,"FontSize":null,"Weight":null,"Style":null,"Decorations":[],"Color":null,"Background":null,"Features":{}}]}],null]}]]},"DefaultPage":null}}"""

        let result = this.WriteDocument request
        let modelPath = Path.Combine(workspace, "designed.naodoc")
        Assert.AreEqual(SignedByte.Documents.SignedByteDoc.MediaType, result.GetProperty("mediaType").GetString())
        Assert.IsTrue(File.Exists modelPath)

        let converted = this.Convert """{"source":"designed.naodoc","target":"html"}"""
        Assert.AreEqual(SignedByte.Documents.SignedByteDoc.MediaType, converted.GetProperty("from").GetString())
        Assert.AreEqual(SignedByte.Documents.Html.MediaType, converted.GetProperty("to").GetString())
        let htmlPath = Path.Combine(workspace, "designed.html")
        Assert.IsTrue(File.Exists htmlPath)
        let html = File.ReadAllText htmlPath
        Assert.IsTrue(html.Contains "Designed Document", "HTML output lost designed title")
        Assert.IsTrue(html.Contains "font-family:Aptos Display", "HTML output lost title font family")

    [<TestMethod>]
    member this.RichTextSource_CanConvertToPdf() =
        this.WriteSource
            "rich-notes.rtf"
            @"{\rtf1\ansi\deff0{\fonttbl{\f0 Calibri;}}\fs24 Rich Notes\par A paragraph with \b bold text\b0.\par}"

        let result = this.Convert """{"source":"rich-notes.rtf","target":"pdf"}"""
        let converted = result.GetProperty("converted").GetString()
        Assert.AreEqual(SignedByte.Documents.Rtf.MediaType, result.GetProperty("from").GetString())
        Assert.AreEqual(SignedByte.Documents.Pdf.MediaType, result.GetProperty("to").GetString())
        Assert.IsTrue(converted.EndsWith("rich-notes.pdf"), sprintf "Unexpected output path: %s" converted)
        Assert.IsTrue(File.Exists(Path.Combine(workspace, "rich-notes.pdf")))

    [<TestMethod>]
    member this.UnsupportedTarget_ReturnsErrorNotSilentText() =
        this.WriteSource "doc.md" "# Doc\n"
        let result = this.Convert """{"source":"doc.md","target":"nonsense"}"""
        // An unknown target must surface an error rather than silently degrading.
        Assert.IsTrue(result.TryGetProperty("error") |> fst, "Expected an error for an unsupported target format")
