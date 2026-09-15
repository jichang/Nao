namespace Nao.Documents

open System
open System.IO
open System.Text

/// Reader and writer for comma-separated values. CSV maps naturally to a table;
/// the first input row is treated as the table header.
module Csv =

    [<Literal>]
    let MediaType = "text/csv"

    /// Controls CSV parsing and serialization.
    type Options =
        { Delimiter: char
          HasHeader: bool
          Encoding: Encoding }

        static member Default =
            { Delimiter = ','
              HasHeader = true
              Encoding = UTF8Encoding(false) }

    let private escapeField (delimiter: char) (value: string) =
        let value = if isNull value then "" else value

        if value.IndexOfAny([| delimiter; '"'; '\r'; '\n' |]) >= 0 then
            "\"" + value.Replace("\"", "\"\"") + "\""
        else
            value

    let private cellText (cell: TableCell) =
        cell.Content
        |> List.choose (function
            | Paragraph(inlines, _) -> Some(InlineText.ofInlines inlines)
            | Heading(_, inlines) -> Some(InlineText.ofInlines inlines)
            | _ -> None)
        |> String.concat " "

    let private rowText (row: TableRow) = row.Cells |> List.map cellText

    let private renderRow (delimiter: char) (sb: StringBuilder) (row: string list) =
        row
        |> List.map (escapeField delimiter)
        |> String.concat (string delimiter)
        |> sb.AppendLine
        |> ignore

    /// Writes tables as CSV. Non-table blocks are emitted as one-column rows.
    type Writer(?options: Options) =
        interface IDocumentWriter with
            member _.MediaTypes = [ MediaType ]

            member _.Write(doc, output, _ctx) =
                let options = defaultArg options Options.Default
                let sb = StringBuilder()

                let rec renderBlock block =
                    match block with
                    | Table spec ->
                        if options.HasHeader then
                            spec.Header |> Option.iter (rowText >> renderRow options.Delimiter sb)

                        spec.Rows |> List.iter (rowText >> renderRow options.Delimiter sb)
                    | Paragraph(inlines, _) -> renderRow options.Delimiter sb [ InlineText.ofInlines inlines ]
                    | Heading(_, inlines) -> renderRow options.Delimiter sb [ InlineText.ofInlines inlines ]
                    | Container spec -> spec.Children |> List.iter (fun child -> renderBlock child.Content)
                    | _ -> ()

                Document.toBlocks doc |> List.iter renderBlock
                use writer = new StreamWriter(output, options.Encoding, 1024, leaveOpen = true)
                writer.Write(sb.ToString())

    let private parseRows (delimiter: char) (text: string) =
        let rows = ResizeArray<string list>()
        let row = ResizeArray<string>()
        let field = StringBuilder()
        let mutable quoted = false
        let mutable i = 0

        let finishField () =
            row.Add(field.ToString())
            field.Clear() |> ignore

        let finishRow () =
            finishField ()
            rows.Add(List.ofSeq row)
            row.Clear()

        while i < text.Length do
            let ch = text.[i]

            if quoted then
                if ch = '"' then
                    if i + 1 < text.Length && text.[i + 1] = '"' then
                        field.Append('"') |> ignore
                        i <- i + 1
                    else
                        quoted <- false
                else
                    field.Append(ch) |> ignore
            else
                match ch with
                | '"' when field.Length = 0 -> quoted <- true
                | c when c = delimiter -> finishField ()
                | '\r' ->
                    if i + 1 < text.Length && text.[i + 1] = '\n' then
                        i <- i + 1

                    finishRow ()
                | '\n' -> finishRow ()
                | _ -> field.Append(ch) |> ignore

            i <- i + 1

        if quoted then
            invalidArg "input" "CSV contains an unterminated quoted field"

        if field.Length > 0 || row.Count > 0 then
            finishRow ()

        List.ofSeq rows

    let private makeCell text =
        { Content = [ Paragraph([ Run(text, TextStyle.Default) ], None) ]
          ColSpan = 1
          RowSpan = 1
          Style = None }

    let private makeRow values = { Cells = values |> List.map makeCell }

    /// Reads CSV into one table. The first row becomes the optional header.
    type Reader(?options: Options) =
        interface IDocumentReader with
            member _.MediaTypes = [ MediaType ]

            member _.Read(input, _ctx) =
                let options = defaultArg options Options.Default
                use reader = new StreamReader(input, options.Encoding, true, 1024, leaveOpen = true)

                match parseRows options.Delimiter (reader.ReadToEnd()) with
                | [] -> Document.OfBlocks []
                | firstRow :: rows ->
                    let header, body =
                        if options.HasHeader then
                            Some firstRow, rows
                        else
                            None, firstRow :: rows

                    let width = (firstRow :: rows) |> List.map List.length |> List.max

                    let normalize values =
                        values @ List.replicate (width - values.Length) ""

                    let header = header |> Option.map (normalize >> makeRow)
                    let body = body |> List.map (normalize >> makeRow)

                    Table
                        { Columns = List.replicate width { Width = None; Align = None }
                          Header = header
                          Rows = body
                          Style = None }
                    |> List.singleton
                    |> Document.OfBlocks
