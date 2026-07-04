namespace Nao.Documents

/// A `ConverterRegistry` preconfigured with every built-in format, so any supported
/// A → unified → B conversion works out of the box.
///
/// Readable: text, markdown, html, docx.
/// Writable: text, markdown, html, docx, pdf, xlsx, pptx.
[<RequireQualifiedAccess>]
module Formats =

    /// Build a registry with all built-in readers and writers registered.
    let fullRegistry () : ConverterRegistry =
        let reg = ConverterRegistry()
        // Plain text + Markdown (fluid).
        reg.RegisterReader(PlainText.Reader())
        reg.RegisterWriter(PlainText.Writer())
        reg.RegisterReader(Markdown.Reader())
        reg.RegisterWriter(Markdown.Writer())
        // HTML (fluid).
        reg.RegisterReader(Html.Reader())
        reg.RegisterWriter(Html.Writer())
        // Word (fluid).
        reg.RegisterReader(Docx.Reader())
        reg.RegisterWriter(Docx.Writer())
        // Paginated / spreadsheet / presentation writers.
        reg.RegisterWriter(Pdf.Writer())
        reg.RegisterWriter(Xlsx.Writer())
        reg.RegisterWriter(Pptx.Writer())
        reg
