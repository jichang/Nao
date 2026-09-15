namespace Nao.Documents

/// A `ConverterRegistry` preconfigured with every built-in format, so any supported
/// A → unified → B conversion works out of the box.
///
/// Readable: text, csv, markdown, html, rtf, docx, odt, pdf, signedbytedoc.
/// Writable: text, csv, markdown, html, docx, odt, signedbytedoc, pdf, xlsx, pptx.
[<RequireQualifiedAccess>]
module Formats =

    /// Build a registry with all built-in readers and writers registered.
    let fullRegistry () : ConverterRegistry =
        let reg = ConverterRegistry()
        // Plain text + Markdown (fluid).
        reg.RegisterReader(PlainText.Reader())
        reg.RegisterWriter(PlainText.Writer())
        // CSV tables.
        reg.RegisterReader(Csv.Reader())
        reg.RegisterWriter(Csv.Writer())
        // OpenDocument Text.
        reg.RegisterReader(Odt.Reader())
        reg.RegisterWriter(Odt.Writer())
        reg.RegisterReader(Markdown.Reader())
        reg.RegisterWriter(Markdown.Writer())
        // HTML (fluid).
        reg.RegisterReader(Html.Reader())
        reg.RegisterWriter(Html.Writer())
        // Unified SignedByte document model JSON.
        reg.RegisterReader(SignedByteDoc.Reader())
        reg.RegisterWriter(SignedByteDoc.Writer())
        // Rich Text Format input (via RtfPipe).
        reg.RegisterReader(Rtf.Reader())
        // Word (fluid).
        reg.RegisterReader(Docx.Reader())
        reg.RegisterWriter(Docx.Writer())
        reg.RegisterReader(Pdf.Reader())
        // Paginated / spreadsheet / presentation writers.
        reg.RegisterWriter(Pdf.Writer())
        reg.RegisterWriter(Xlsx.Writer())
        reg.RegisterWriter(Pptx.Writer())
        reg
