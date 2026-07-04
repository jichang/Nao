namespace Nao.Documents

open System
open System.IO

/// Writer for `application/pdf`, backed by PDFsharp/MigraDoc. The unified document
/// model is mapped onto a MigraDoc document (sections, paragraphs, tables) and rendered
/// to a real PDF — MigraDoc owns layout, pagination and the low-level PDF object graph,
/// so there is no hand-rolled PDF serialization to maintain. There is intentionally no
/// PDF *reader*: parsing arbitrary PDF back into structured content is out of scope.
module Pdf =

    [<Literal>]
    let MediaType = "application/pdf"

    // MigraDoc type aliases (kept private to avoid clashing with the unified model's
    // own `Document`, `Paragraph`, `Table`, ... names).
    type private MdDocument = MigraDoc.DocumentObjectModel.Document
    type private MdUnit = MigraDoc.DocumentObjectModel.Unit
    type private MdRenderer = MigraDoc.Rendering.PdfDocumentRenderer

    let private FontBody = "DejaVu Sans"
    let private FontMono = "DejaVu Sans Mono"

    /// Resolves embeddable TrueType faces from the host so PDFsharp can render on a
    /// headless Linux box (which has no GDI font fallback). Scans the common font
    /// directories for DejaVu/Liberation faces and serves their bytes on demand.
    module private Fonts =
        open System.Collections.Concurrent

        let private dirs =
            [ "/usr/share/fonts"
              "/usr/local/share/fonts"
              Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".fonts")
              Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.Personal, ".fonts")
              @"C:\Windows\Fonts" ]
            |> List.filter Directory.Exists

        let private findFirst (names: string list) : string option =
            dirs
            |> List.tryPick (fun dir ->
                names
                |> List.tryPick (fun n ->
                    try
                        Directory.EnumerateFiles(dir, n, SearchOption.AllDirectories) |> Seq.tryHead
                    with _ -> None))

        // Map a logical face key to a file on disk. Missing variants fall back to the
        // closest available face in `resolveKey`, so a partial font set still works.
        let private faceFiles =
            dict
                [ "sans", findFirst [ "DejaVuSans.ttf"; "LiberationSans-Regular.ttf" ]
                  "sans-b", findFirst [ "DejaVuSans-Bold.ttf"; "LiberationSans-Bold.ttf" ]
                  "sans-i", findFirst [ "DejaVuSans-Oblique.ttf"; "LiberationSans-Italic.ttf" ]
                  "sans-bi", findFirst [ "DejaVuSans-BoldOblique.ttf"; "LiberationSans-BoldItalic.ttf" ]
                  "mono", findFirst [ "DejaVuSansMono.ttf"; "LiberationMono-Regular.ttf" ]
                  "mono-b", findFirst [ "DejaVuSansMono-Bold.ttf"; "LiberationMono-Bold.ttf" ]
                  "mono-i", findFirst [ "DejaVuSansMono-Oblique.ttf"; "LiberationMono-Italic.ttf" ]
                  "mono-bi", findFirst [ "DejaVuSansMono-BoldOblique.ttf"; "LiberationMono-BoldItalic.ttf" ] ]

        let private has key =
            match faceFiles.TryGetValue key with
            | true, Some _ -> true
            | _ -> false

        /// Pick the best available face key for a family/style, degrading gracefully.
        let resolveKey (prefix: string) (bold: bool) (italic: bool) : string =
            let order =
                match bold, italic with
                | true, true -> [ prefix + "-bi"; prefix + "-b"; prefix + "-i"; prefix ]
                | true, false -> [ prefix + "-b"; prefix ]
                | false, true -> [ prefix + "-i"; prefix ]
                | false, false -> [ prefix ]
            order |> List.tryFind has |> Option.defaultValue prefix

        let private cache = ConcurrentDictionary<string, byte[]>()

        let fontBytes (faceKey: string) : byte[] =
            let load key =
                match faceFiles.TryGetValue key with
                | true, Some path -> File.ReadAllBytes path
                | _ -> null
            cache.GetOrAdd(
                faceKey,
                fun key ->
                    match load key with
                    | null -> load "sans"
                    | bytes -> bytes)

    type private DocumentFontResolver() =
        interface PdfSharp.Fonts.IFontResolver with
            member _.ResolveTypeface(familyName: string, isBold: bool, isItalic: bool) =
                let fam = (if isNull familyName then "" else familyName).ToLowerInvariant()
                let prefix =
                    if fam.Contains "mono" || fam.Contains "courier" || fam.Contains "consol" then "mono"
                    else "sans"
                PdfSharp.Fonts.FontResolverInfo(Fonts.resolveKey prefix isBold isItalic)

            member _.GetFont(faceName: string) = Fonts.fontBytes faceName

    let private resolverLock = obj ()
    let mutable private resolverSet = false

    /// Install the custom font resolver exactly once (PDFsharp forbids resetting it).
    let private ensureFontResolver () =
        lock resolverLock (fun () ->
            if not resolverSet then
                PdfSharp.Fonts.GlobalFontSettings.FontResolver <- DocumentFontResolver()
                resolverSet <- true)

    let private isBold (style: TextStyle) =
        match style.Weight with
        | Some SemiBold
        | Some Bold
        | Some Black -> true
        | _ -> false

    let private isItalic (style: TextStyle) =
        match style.Style with
        | Some Italic
        | Some Oblique -> true
        | _ -> false

    let private mdAlign (a: TextAlign) =
        match a with
        | Left -> MigraDoc.DocumentObjectModel.ParagraphAlignment.Left
        | Center -> MigraDoc.DocumentObjectModel.ParagraphAlignment.Center
        | Right -> MigraDoc.DocumentObjectModel.ParagraphAlignment.Right
        | Justify -> MigraDoc.DocumentObjectModel.ParagraphAlignment.Justify

    let rec private addInline (p: MigraDoc.DocumentObjectModel.Paragraph) (inl: Inline) =
        match inl with
        | Run(text, style) ->
            let ft = p.AddFormattedText(text)
            if isBold style then ft.Bold <- true
            if isItalic style then ft.Italic <- true
            if style.FontFamily |> Option.exists (fun f -> f.ToLowerInvariant().Contains "mono") then
                ft.Font.Name <- FontMono
        | LineBreak -> p.AddLineBreak()
        | Link(inner, _href) -> inner |> List.iter (addInline p)
        | InlineImage(_, alt, _) -> p.AddText("[" + defaultArg alt "image" + "]") |> ignore
        | InlineCode code ->
            let ft = p.AddFormattedText(code)
            ft.Font.Name <- FontMono
        | InlineRaw(_, data) -> p.AddText(data) |> ignore

    let private addInlines (p: MigraDoc.DocumentObjectModel.Paragraph) (inlines: Inline list) =
        inlines |> List.iter (addInline p)

    let rec private addBlock (section: MigraDoc.DocumentObjectModel.Section) (contentWidth: float) (block: Block) =
        match block with
        | Heading(level, inlines) ->
            let l = max 1 (min 6 level)
            let p = section.AddParagraph()
            p.Format.Font.Name <- FontBody
            p.Format.Font.Size <- MdUnit(max 12.0 (24.0 - float (l - 1) * 2.0))
            p.Format.Font.Bold <- true
            p.Format.SpaceBefore <- MdUnit 6.0
            p.Format.SpaceAfter <- MdUnit 3.0
            addInlines p inlines
        | Paragraph(inlines, align) ->
            let p = section.AddParagraph()
            p.Format.Font.Name <- FontBody
            p.Format.Font.Size <- MdUnit 11.0
            p.Format.SpaceAfter <- MdUnit 5.0
            align |> Option.iter (fun a -> p.Format.Alignment <- mdAlign a)
            addInlines p inlines
        | ListBlock spec ->
            spec.Items
            |> List.iteri (fun idx item ->
                let marker =
                    if spec.Ordered then sprintf "%d. " (idx + defaultArg spec.Start 1) else "\u2022 "
                match item.Content with
                | Paragraph(inlines, _) :: rest ->
                    let p = section.AddParagraph()
                    p.Format.Font.Name <- FontBody
                    p.Format.Font.Size <- MdUnit 11.0
                    p.Format.LeftIndent <- MdUnit 14.0
                    p.AddText(marker) |> ignore
                    addInlines p inlines
                    rest |> List.iter (addBlock section contentWidth)
                | blocks -> blocks |> List.iter (addBlock section contentWidth))
        | CodeBlock(_, code) ->
            let p = section.AddParagraph()
            p.Format.Font.Name <- FontMono
            p.Format.Font.Size <- MdUnit 9.0
            p.Format.SpaceAfter <- MdUnit 6.0
            code.Replace("\r\n", "\n").Split('\n')
            |> Array.iteri (fun i ln ->
                if i > 0 then p.AddLineBreak()
                p.AddText(ln) |> ignore)
        | Quote blocks -> blocks |> List.iter (addBlock section contentWidth)
        | Media spec ->
            let p = section.AddParagraph()
            p.Format.Font.Name <- FontBody
            p.Format.Font.Italic <- true
            p.AddText("[" + defaultArg spec.Alt "media" + "]") |> ignore
            if not spec.Caption.IsEmpty then
                let c = section.AddParagraph()
                c.Format.Font.Name <- FontBody
                c.Format.Font.Size <- MdUnit 9.0
                c.Format.Font.Italic <- true
                addInlines c spec.Caption
        | Table spec ->
            let cols =
                if not spec.Columns.IsEmpty then spec.Columns.Length
                else
                    match spec.Header with
                    | Some h -> h.Cells.Length
                    | None -> spec.Rows |> List.tryHead |> Option.map (fun r -> r.Cells.Length) |> Option.defaultValue 1
            let cols = max 1 cols
            let table = section.AddTable()
            table.Borders.Width <- MdUnit 0.5
            let cw = contentWidth / float cols
            for _ in 1..cols do
                table.AddColumn(MdUnit cw) |> ignore
            let addRow (cells: TableCell list) (bold: bool) =
                let row = table.AddRow()
                cells
                |> List.iteri (fun i c ->
                    if i < cols then
                        let cell = row.Cells.[i]
                        let p = cell.AddParagraph()
                        p.Format.Font.Name <- FontBody
                        p.Format.Font.Size <- MdUnit 10.0
                        if bold then p.Format.Font.Bold <- true
                        c.Content
                        |> List.choose (function
                            | Paragraph(inl, _) -> Some inl
                            | _ -> None)
                        |> List.iteri (fun j inl ->
                            if j > 0 then p.AddLineBreak()
                            addInlines p inl))
            spec.Header |> Option.iter (fun h -> addRow h.Cells true)
            spec.Rows |> List.iter (fun r -> addRow r.Cells false)
            section.AddParagraph().Format.SpaceAfter <- MdUnit 6.0
        | Container spec -> spec.Children |> List.iter (fun c -> addBlock section contentWidth c.Content)
        | ThematicBreak ->
            let p = section.AddParagraph()
            p.Format.Borders.Bottom.Width <- MdUnit 0.5
            p.Format.SpaceBefore <- MdUnit 4.0
            p.Format.SpaceAfter <- MdUnit 8.0
        | BlockRaw(_, data) ->
            let p = section.AddParagraph()
            p.Format.Font.Name <- FontMono
            p.Format.Font.Size <- MdUnit 9.0
            p.AddText(data) |> ignore

    let private applyPageSetup (section: MigraDoc.DocumentObjectModel.Section) (setup: PageSetup) =
        section.PageSetup.PageWidth <- MdUnit setup.Width.Value
        section.PageSetup.PageHeight <- MdUnit setup.Height.Value
        section.PageSetup.TopMargin <- MdUnit setup.Margins.Top.Value
        section.PageSetup.BottomMargin <- MdUnit setup.Margins.Bottom.Value
        section.PageSetup.LeftMargin <- MdUnit setup.Margins.Left.Value
        section.PageSetup.RightMargin <- MdUnit setup.Margins.Right.Value

    let private contentWidthOf (setup: PageSetup) =
        setup.Width.Value - setup.Margins.Left.Value - setup.Margins.Right.Value

    /// Writes a `Document` as a PDF rendered by MigraDoc/PDFsharp.
    type Writer() =
        interface IDocumentWriter with
            member _.MediaTypes = [ MediaType ]
            member _.Write(doc, output, _ctx) =
                ensureFontResolver ()
                let defaultSetup = doc.DefaultPage |> Option.defaultValue PageSetup.A4

                let mdDoc = MdDocument()
                let normal = mdDoc.Styles.["Normal"]
                normal.Font.Name <- FontBody
                normal.Font.Size <- MdUnit 11.0

                match doc.Body with
                | Paged pages ->
                    pages
                    |> List.iter (fun pg ->
                        let section = mdDoc.AddSection()
                        applyPageSetup section pg.Setup
                        let cw = contentWidthOf pg.Setup
                        pg.Header @ pg.Content @ pg.Footer |> List.iter (addBlock section cw))
                | Fluid blocks ->
                    let section = mdDoc.AddSection()
                    applyPageSetup section defaultSetup
                    let cw = contentWidthOf defaultSetup
                    let titleBlocks =
                        match doc.Metadata.Title with
                        | Some t -> [ Heading(1, [ Run(t, TextStyle.Default) ]) ]
                        | None -> []
                    titleBlocks @ blocks |> List.iter (addBlock section cw)

                // Ensure at least one section/paragraph so MigraDoc emits a valid page.
                if mdDoc.Sections.Count = 0 then
                    let section = mdDoc.AddSection()
                    applyPageSetup section defaultSetup
                    section.AddParagraph(" ") |> ignore

                let renderer = MdRenderer()
                renderer.Document <- mdDoc
                renderer.RenderDocument()
                renderer.PdfDocument.Save(output, false)
