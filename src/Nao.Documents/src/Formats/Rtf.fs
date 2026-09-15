namespace Nao.Documents

open System.IO
open System.Text

/// Reader for Rich Text Format (`.rtf`, `application/rtf`). Parsing is delegated to
/// RtfPipe, which converts the RTF stream to HTML before it enters the unified model.
module Rtf =

    do Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)

    [<Literal>]
    let MediaType = "application/rtf"

    [<Literal>]
    let TextMediaType = "text/rtf"

    let private mediaTypes = [ MediaType; TextMediaType ]

    let private readHtml (html: string) (ctx: ConversionContext) =
        let bytes = Encoding.UTF8.GetBytes(html)
        use htmlStream = new MemoryStream(bytes)
        let document = (Html.Reader() :> IDocumentReader).Read(htmlStream, ctx)
        { document with Sources = Map.empty }

    type Reader() =
        interface IDocumentReader with
            member _.MediaTypes = mediaTypes

            member _.Read(input, ctx) =
                use reader = new StreamReader(input, Encoding.UTF8, true, 1024, leaveOpen = true)
                let html = RtfPipe.Rtf.ToHtml(RtfPipe.RtfSource(reader), RtfPipe.RtfHtmlSettings())
                readHtml html ctx
