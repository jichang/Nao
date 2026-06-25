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
        let dataDir = Path.Combine(Path.GetTempPath(), "nao-convert-tests", Guid.NewGuid().ToString("N"))
        Environment.SetEnvironmentVariable("NAO_DATA_DIR", dataDir)
        workspace <- Path.Combine(dataDir, "workspace")
        Directory.CreateDirectory workspace |> ignore

    member private _.WriteSource (name: string) (content: string) =
        File.WriteAllText(Path.Combine(workspace, name), content)

    member private _.Convert (input: string) =
        let json = (AssistantTools.convertDocument.Execute ToolContext.allowAll input).GetAwaiter().GetResult()
        JsonDocument.Parse(json).RootElement

    [<TestMethod>]
    member this.MarkdownToPdf_ConvertsInTheRequestedDirection() =
        this.WriteSource "report.md" "# Sample Report\n\nFirst item.\n"
        let result = this.Convert """{"source":"report.md","target":"pdf"}"""
        // The source is markdown and the target is pdf — not the reverse.
        Assert.AreEqual(Nao.Documents.Markdown.MediaType, result.GetProperty("from").GetString())
        Assert.AreEqual(Nao.Documents.Pdf.MediaType, result.GetProperty("to").GetString())

    [<TestMethod>]
    member this.BareFormatTarget_DerivesOutputNameFromSource() =
        this.WriteSource "notes.md" "# Notes\n\nBody.\n"
        let result = this.Convert """{"source":"notes.md","target":"pdf"}"""
        let converted = result.GetProperty("converted").GetString()
        // The output is named after the source, not a file literally called "pdf".
        Assert.IsTrue(converted.EndsWith("notes.pdf"), sprintf "Unexpected output path: %s" converted)
        Assert.IsTrue(File.Exists(Path.Combine(workspace, "notes.pdf")))

    [<TestMethod>]
    member this.UnsupportedTarget_ReturnsErrorNotSilentText() =
        this.WriteSource "doc.md" "# Doc\n"
        let result = this.Convert """{"source":"doc.md","target":"nonsense"}"""
        // An unknown target must surface an error rather than silently degrading.
        Assert.IsTrue(
            result.TryGetProperty("error") |> fst,
            "Expected an error for an unsupported target format")
