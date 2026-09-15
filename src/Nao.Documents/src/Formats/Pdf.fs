namespace Nao.Documents

/// PDF reader and writer facade.
module Pdf =
    [<Literal>]
    let MediaType = "application/pdf"

    type Reader() =
        interface IDocumentReader with
            member _.MediaTypes = [ MediaType ]

            member _.Read(input, context) =
                (PdfReader.Reader() :> IDocumentReader).Read(input, context)

    type Writer() =
        interface IDocumentWriter with
            member _.MediaTypes = [ MediaType ]

            member _.Write(document, output, context) =
                (PdfWriter.Writer() :> IDocumentWriter).Write(document, output, context)
