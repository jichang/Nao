namespace Nao.Documents

/// Errors returned when an edit cannot be applied to the current document shape.
[<RequireQualifiedAccess>]
type DocumentEditError =
    | RequiresFluidBody
    | RequiresPagedBody
    | InvalidHeadingLevel of int
    | InvalidBlockIndex of int
    | InvalidPageIndex of int
    | DuplicateResourceId of ResourceId
    | ResourceNotFound of ResourceId

/// Typed, immutable editing operations over the canonical document model.
[<RequireQualifiedAccess>]
module DocumentEditor =

    let private updateAt invalidIndex index update items =
        if index < 0 || index >= List.length items then
            Error(invalidIndex index)
        else
            Ok(
                items
                |> List.mapi (fun current item -> if current = index then update item else item)
            )

    let setMetadata metadata (document: Document) = { document with Metadata = metadata }

    let updateMetadata update (document: Document) =
        { document with
            Metadata = update document.Metadata }

    let setBody body (document: Document) = { document with Body = body }
    let setDefaultPage setup (document: Document) = { document with DefaultPage = setup }

    let setTitle (title: string option) =
        updateMetadata (fun metadata -> { metadata with Title = title })

    let setAuthors authors =
        updateMetadata (fun metadata -> { metadata with Authors = authors })

    let setLanguage language =
        updateMetadata (fun metadata -> { metadata with Language = language })

    let setCreated created =
        updateMetadata (fun metadata -> { metadata with Created = created })

    let setModified modified =
        updateMetadata (fun metadata -> { metadata with Modified = modified })

    let setProperties properties =
        updateMetadata (fun metadata ->
            { metadata with
                Properties = properties })

    let setProperty name value =
        updateMetadata (fun metadata ->
            { metadata with
                Properties = metadata.Properties |> Map.add name value })

    let removeProperty name =
        updateMetadata (fun metadata ->
            { metadata with
                Properties = metadata.Properties |> Map.remove name })

    let setResources resources (document: Document) = { document with Resources = resources }

    let addResource resource (document: Document) =
        if document.Resources |> List.exists (fun candidate -> candidate.Id = resource.Id) then
            Error(DocumentEditError.DuplicateResourceId resource.Id)
        else
            Ok
                { document with
                    Resources = document.Resources @ [ resource ] }

    let upsertResource resource (document: Document) =
        let retained =
            document.Resources |> List.filter (fun candidate -> candidate.Id <> resource.Id)

        { document with
            Resources = retained @ [ resource ] }

    let updateResource id update (document: Document) =
        match document.Resources |> List.tryFind (fun resource -> resource.Id = id) with
        | None -> Error(DocumentEditError.ResourceNotFound id)
        | Some _ ->
            Ok
                { document with
                    Resources =
                        document.Resources
                        |> List.map (fun resource -> if resource.Id = id then update resource else resource) }

    let removeResource id (document: Document) =
        if document.Resources |> List.exists (fun resource -> resource.Id = id) then
            Ok
                { document with
                    Resources = document.Resources |> List.filter (fun resource -> resource.Id <> id) }
        else
            Error(DocumentEditError.ResourceNotFound id)

    let setBlocks blocks (document: Document) =
        match document.Body with
        | Fluid _ -> Ok { document with Body = Fluid blocks }
        | Paged _ -> Error DocumentEditError.RequiresFluidBody

    let appendBlock (block: Block) (document: Document) =
        match document.Body with
        | Fluid blocks ->
            Ok
                { document with
                    Body = Fluid(blocks @ [ block ]) }
        | Paged _ -> Error DocumentEditError.RequiresFluidBody

    let appendBlocks blocks (document: Document) =
        match document.Body with
        | Fluid existing ->
            Ok
                { document with
                    Body = Fluid(existing @ blocks) }
        | Paged _ -> Error DocumentEditError.RequiresFluidBody

    let insertBlock index block (document: Document) =
        match document.Body with
        | Paged _ -> Error DocumentEditError.RequiresFluidBody
        | Fluid blocks when index < 0 || index > blocks.Length -> Error(DocumentEditError.InvalidBlockIndex index)
        | Fluid blocks ->
            let before, after = List.splitAt index blocks

            Ok
                { document with
                    Body = Fluid(before @ block :: after) }

    let updateBlock index update (document: Document) =
        match document.Body with
        | Paged _ -> Error DocumentEditError.RequiresFluidBody
        | Fluid blocks ->
            updateAt DocumentEditError.InvalidBlockIndex index update blocks
            |> Result.map (fun updated -> { document with Body = Fluid updated })

    let removeBlock index (document: Document) =
        match document.Body with
        | Paged _ -> Error DocumentEditError.RequiresFluidBody
        | Fluid blocks when index < 0 || index >= blocks.Length -> Error(DocumentEditError.InvalidBlockIndex index)
        | Fluid blocks ->
            Ok
                { document with
                    Body = Fluid(List.removeAt index blocks) }

    let setPages pages (document: Document) =
        match document.Body with
        | Paged _ -> Ok { document with Body = Paged pages }
        | Fluid _ -> Error DocumentEditError.RequiresPagedBody

    let appendPage page (document: Document) =
        match document.Body with
        | Paged pages ->
            Ok
                { document with
                    Body = Paged(pages @ [ page ]) }
        | Fluid _ -> Error DocumentEditError.RequiresPagedBody

    let updatePage index update (document: Document) =
        match document.Body with
        | Fluid _ -> Error DocumentEditError.RequiresPagedBody
        | Paged pages ->
            updateAt DocumentEditError.InvalidPageIndex index update pages
            |> Result.map (fun updated -> { document with Body = Paged updated })

    let removePage index (document: Document) =
        match document.Body with
        | Fluid _ -> Error DocumentEditError.RequiresPagedBody
        | Paged pages when index < 0 || index >= pages.Length -> Error(DocumentEditError.InvalidPageIndex index)
        | Paged pages ->
            Ok
                { document with
                    Body = Paged(List.removeAt index pages) }

    let appendHeading (level: int) (text: string) (style: TextStyle) (document: Document) =
        if level < 1 || level > 6 then
            Error(DocumentEditError.InvalidHeadingLevel level)
        else
            appendBlock (Heading(level, [ Run(text, style) ])) document

    let appendParagraph (text: string) (style: TextStyle) (align: TextAlign option) (document: Document) =
        appendBlock (Paragraph([ Run(text, style) ], align)) document
