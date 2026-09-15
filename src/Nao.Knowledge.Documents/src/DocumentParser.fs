namespace Nao.Knowledge.Documents

/// Public document parsers that can only execute through a hard-memory-limited worker.
[<RequireQualifiedAccess>]
module DocumentParser =
    let markdown config =
        DocumentParserRuntime.markdown config.PackageReadLimits
        |> IsolatedDocumentParser.create config

    let html config =
        DocumentParserRuntime.html config.PackageReadLimits
        |> IsolatedDocumentParser.create config

    let docx config =
        DocumentParserRuntime.docx config.PackageReadLimits
        |> IsolatedDocumentParser.create config

    let odt config =
        DocumentParserRuntime.odt config.PackageReadLimits
        |> IsolatedDocumentParser.create config

    let pdf config =
        DocumentParserRuntime.pdf config.PackageReadLimits
        |> IsolatedDocumentParser.create config

    let structured config =
        [ markdown config; html config; docx config; odt config; pdf config ]
