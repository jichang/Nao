namespace Nao.Documents

open System
open System.IO
open System.Text

/// Shared helpers for flattening inline content to a plain string.
[<RequireQualifiedAccess>]
module internal InlineText =

    let rec ofInline (i: Inline) : string =
        match i with
        | Run(text, _) -> text
        | LineBreak -> "\n"
        | Link(children, href) -> (ofInlines children) + " (" + href + ")"
        | InlineImage(_, alt, _) -> defaultArg alt "[image]"
        | InlineCode code -> code
        | InlineRaw(_, data) -> data

    and ofInlines (inlines: Inline list) : string =
        inlines |> List.map ofInline |> String.concat ""

/// Reader and writer for `text/plain`. The plain-text form is the lowest common
/// denominator: structure is flattened to lines, media becomes alt text.
module PlainText =

    [<Literal>]
    let MediaType = "text/plain"

    let private renderBlock (sb: StringBuilder) (block: Block) =
        let rec go (indent: string) (block: Block) =
            match block with
            | Paragraph(inlines, _) ->
                sb.Append(indent).AppendLine(InlineText.ofInlines inlines) |> ignore
                sb.AppendLine() |> ignore
            | Heading(_, inlines) ->
                sb.Append(indent).AppendLine(InlineText.ofInlines inlines) |> ignore
                sb.AppendLine() |> ignore
            | ListBlock spec ->
                spec.Items
                |> List.iteri (fun idx item ->
                    let bullet =
                        if spec.Ordered then sprintf "%d. " (idx + (defaultArg spec.Start 1))
                        else "- "
                    sb.Append(indent).Append(bullet) |> ignore
                    match item.Content with
                    | Paragraph(inlines, _) :: rest ->
                        sb.AppendLine(InlineText.ofInlines inlines) |> ignore
                        rest |> List.iter (go (indent + "  "))
                    | blocks -> blocks |> List.iter (go (indent + "  ")))
                sb.AppendLine() |> ignore
            | CodeBlock(_, code) ->
                sb.AppendLine(code) |> ignore
                sb.AppendLine() |> ignore
            | Quote blocks ->
                blocks |> List.iter (go (indent + "> "))
            | Media spec ->
                sb.Append(indent).AppendLine(defaultArg spec.Alt "[media]") |> ignore
            | Container spec ->
                spec.Children |> List.iter (fun c -> go indent c.Content)
            | Table spec ->
                let renderRow (row: TableRow) =
                    row.Cells
                    |> List.map (fun c ->
                        c.Content
                        |> List.choose (function Paragraph(i, _) -> Some(InlineText.ofInlines i) | _ -> None)
                        |> String.concat " ")
                    |> String.concat "\t"
                spec.Header |> Option.iter (fun h -> sb.Append(indent).AppendLine(renderRow h) |> ignore)
                spec.Rows |> List.iter (fun r -> sb.Append(indent).AppendLine(renderRow r) |> ignore)
                sb.AppendLine() |> ignore
            | ThematicBreak ->
                sb.Append(indent).AppendLine("----------") |> ignore
            | BlockRaw(_, data) ->
                sb.Append(indent).AppendLine(data) |> ignore
        go "" block

    /// Writes a `Document` as plain text.
    type Writer() =
        interface IDocumentWriter with
            member _.MediaTypes = [ MediaType ]
            member _.Write(doc, output, _ctx) =
                let sb = StringBuilder()
                doc.Metadata.Title |> Option.iter (fun t -> sb.AppendLine(t).AppendLine() |> ignore)
                Document.toBlocks doc |> List.iter (renderBlock sb)
                use w = new StreamWriter(output, UTF8Encoding(false), 1024, leaveOpen = true)
                w.Write(sb.ToString())

    /// Reads plain text into a fluid `Document`: blank-line-separated paragraphs.
    type Reader() =
        interface IDocumentReader with
            member _.MediaTypes = [ MediaType ]
            member _.Read(input, _ctx) =
                use r = new StreamReader(input, Encoding.UTF8, true, 1024, leaveOpen = true)
                let raw = r.ReadToEnd()
                let blocks =
                    raw.Replace("\r\n", "\n").Split([| "\n\n" |], StringSplitOptions.None)
                    |> Array.map (fun chunk -> chunk.Trim('\n'))
                    |> Array.filter (fun chunk -> chunk.Trim() <> "")
                    |> Array.map (fun chunk ->
                        let lines = chunk.Split('\n')
                        let inlines =
                            lines
                            |> Array.mapi (fun i line ->
                                if i = 0 then [ Run(line, TextStyle.Default) ]
                                else [ LineBreak; Run(line, TextStyle.Default) ])
                            |> Array.toList
                            |> List.concat
                        Paragraph(inlines, None))
                    |> Array.toList
                Document.OfBlocks blocks
