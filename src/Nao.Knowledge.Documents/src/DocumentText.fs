namespace Nao.Knowledge.Documents

open Nao.Documents

module internal DocumentText =
    let rec inlineText =
        function
        | Run(text, _) -> text
        | LineBreak -> "\n"
        | Link(children, _) -> children |> List.map inlineText |> String.concat ""
        | InlineImage(_, alt, _) -> defaultArg alt ""
        | InlineCode code -> code
        | InlineRaw(_, data) -> data

    let private inlinesText inlines =
        inlines |> List.map inlineText |> String.concat ""

    let rec blockText =
        function
        | Paragraph(inlines, _)
        | Heading(_, inlines) -> inlinesText inlines
        | ListBlock specification ->
            specification.Items
            |> List.collect _.Content
            |> List.map blockText
            |> String.concat "\n"
        | Table specification ->
            let rowText row =
                row.Cells
                |> List.map (fun cell -> cell.Content |> List.map blockText |> String.concat "\n")
                |> String.concat " | "

            [ yield! specification.Header |> Option.toList |> List.map rowText
              yield! specification.Rows |> List.map rowText ]
            |> String.concat "\n"
        | CodeBlock(_, code) -> code
        | Quote blocks -> blocks |> List.map blockText |> String.concat "\n"
        | Media specification ->
            let caption = inlinesText specification.Caption

            if caption = "" then
                defaultArg specification.Alt ""
            else
                caption
        | Container specification ->
            specification.Children
            |> List.map (_.Content >> blockText)
            |> String.concat "\n"
        | ThematicBreak -> ""
        | BlockRaw(_, data) -> data

    let rec private inlineLinks =
        function
        | Link(children, target) -> target :: (children |> List.collect inlineLinks)
        | _ -> []

    let rec blockLinks =
        function
        | Paragraph(inlines, _)
        | Heading(_, inlines) -> inlines |> List.collect inlineLinks
        | ListBlock specification -> specification.Items |> List.collect _.Content |> List.collect blockLinks
        | Table specification ->
            [ yield! specification.Header |> Option.toList; yield! specification.Rows ]
            |> List.collect _.Cells
            |> List.collect _.Content
            |> List.collect blockLinks
        | Quote blocks -> blocks |> List.collect blockLinks
        | Media specification -> specification.Caption |> List.collect inlineLinks
        | Container specification -> specification.Children |> List.collect (_.Content >> blockLinks)
        | _ -> []

    let isStandaloneLink =
        function
        | Paragraph(inlines, _) ->
            inlines
            |> List.exists (function
                | Link _ -> true
                | _ -> false)
            && inlines
               |> List.forall (function
                   | Link _ -> true
                   | Run(text, _) -> System.String.IsNullOrWhiteSpace text
                   | _ -> false)
        | _ -> false
