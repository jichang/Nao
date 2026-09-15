namespace Nao.Documents

open System
open UglyToad.PdfPig
open UglyToad.PdfPig.Core
open UglyToad.PdfPig.Exceptions

/// Extracts text from PDF pages while retaining page geometry for citations.
module internal PdfReader =
    [<Literal>]
    let MediaType = "application/pdf"

    let private pageSetup width height =
        { Width = Length.Pt width
          Height = Length.Pt height
          Orientation = if width > height then Landscape else Portrait
          Margins = Edges.Zero }

    let private pageBounds (page: UglyToad.PdfPig.Content.Page) =
        let bounds = page.CropBox.Bounds

        { X = Length.Pt bounds.Left
          Y = Length.Pt bounds.Bottom
          Width = Length.Pt bounds.Width
          Height = Length.Pt bounds.Height }

    type Reader() =
        interface IDocumentReader with
            member _.MediaTypes = [ MediaType ]

            member _.Read(input, _ctx) =
                try
                    use source = PdfDocument.Open input

                    let pages =
                        source.GetPages()
                        |> Seq.map (fun page ->
                            let text = page.GetWords() |> Seq.map _.Text |> String.concat " " |> _.Trim()

                            let content =
                                if String.IsNullOrWhiteSpace text then
                                    []
                                else
                                    [ Paragraph([ Run(text, TextStyle.Default) ], None) ]

                            page,
                            { Setup = pageSetup page.Width page.Height
                              Header = []
                              Footer = []
                              Content = content })
                        |> List.ofSeq

                    let sources =
                        pages
                        |> List.choose (fun (sourcePage, page) ->
                            page.Content
                            |> List.tryHead
                            |> Option.map (fun _ ->
                                SourcePath.pageContent sourcePage.Number 0,
                                DocumentSourceLocation.PageRegion
                                    { Page = sourcePage.Number
                                      Bounds = pageBounds sourcePage
                                      Origin = PageCoordinateOrigin.BottomLeft }))
                        |> Map.ofList

                    let requiresOcr =
                        not (List.isEmpty pages)
                        && pages |> List.forall (snd >> _.Content >> List.isEmpty)
                        && pages |> List.exists (fst >> _.NumberOfImages >> (fun count -> count > 0))

                    { Document.OfPages(pages |> List.map snd) with
                        Sources = sources
                        Metadata =
                            { DocumentMetadata.Empty with
                                Properties =
                                    if requiresOcr then
                                        Map.ofList [ "document.requires-ocr", "true" ]
                                    else
                                        Map.empty } }
                with
                | :? PdfDocumentEncryptedException as error -> raise (DocumentEncryptedException error.Message)
                | :? PdfDocumentStackDepthException as error -> raise (DocumentMalformedException error.Message)
                | :? PdfDocumentFormatException as error -> raise (DocumentMalformedException error.Message)
