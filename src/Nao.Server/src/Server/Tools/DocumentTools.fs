namespace Nao.Assistant

open System
open System.IO
open Nao.Agents

/// The document-conversion tool, backed by `Nao.Documents`' unified document model. Maps a
/// source file and a target (filename or bare format) onto the registry's media types and
/// converts through it, so the source's type always picks the input format and the target
/// the output format.
module DocumentTools =

    /// Map a file extension to the IANA media type understood by the document registry.
    let private mediaTypeForExt (ext: string) =
        match ext.ToLowerInvariant() with
        | ".md" | ".markdown" -> Some Nao.Documents.Markdown.MediaType
        | ".txt" | ".text" -> Some Nao.Documents.PlainText.MediaType
        | ".html" | ".htm" -> Some Nao.Documents.Html.MediaType
        | ".pdf" -> Some Nao.Documents.Pdf.MediaType
        | ".docx" -> Some Nao.Documents.Docx.MediaType
        | ".xlsx" -> Some Nao.Documents.Xlsx.MediaType
        | ".pptx" -> Some Nao.Documents.Pptx.MediaType
        | _ -> None

    /// Map a bare format token — a format NAME ("pdf", "word", "excel") or an extension
    /// ("pdf", ".pdf") — to its canonical file extension. Lets the converter accept a target
    /// expressed as a format ("README.md|pdf") rather than a full filename, so a request like
    /// "convert README.md to pdf" doesn't get mis-saved to a file literally named "pdf".
    let private canonicalExt (token: string) : string option =
        match token.Trim().TrimStart('.').ToLowerInvariant() with
        | "md" | "markdown" -> Some ".md"
        | "txt" | "text" | "plaintext" | "plain" -> Some ".txt"
        | "html" | "htm" -> Some ".html"
        | "pdf" -> Some ".pdf"
        | "docx" | "word" -> Some ".docx"
        | "xlsx" | "excel" | "spreadsheet" -> Some ".xlsx"
        | "pptx" | "powerpoint" | "presentation" | "slides" -> Some ".pptx"
        | _ -> None

    /// Resolve the target the caller asked for into a concrete output path. If the target
    /// already carries a supported extension it is used as-is; otherwise the target is treated
    /// as a format name and the output filename is derived from the SOURCE's base name plus the
    /// format's canonical extension (e.g. "README.md" + pdf -> "README.pdf").
    let private resolveTargetName (sourceName: string) (targetToken: string) : string option =
        let ext = Path.GetExtension targetToken
        if ext <> "" && (mediaTypeForExt ext).IsSome then Some targetToken
        else canonicalExt targetToken |> Option.map (fun e -> Path.GetFileNameWithoutExtension sourceName + e)

    let private documentRegistry = Nao.Documents.Formats.fullRegistry ()

    let convertDocument: Tool =
        { Tool.Create("convert_document",
            "Convert a workspace document from one format to another via the unified document model. Input: JSON {\"source\":\"file path/name\",\"target\":\"README.pdf\" or a bare format \"pdf\"|\"docx\"|\"html\"|\"xlsx\"|\"pptx\"|\"md\"|\"txt\"}. When 'target' is a bare format the output is named after the source. Reads .md/.markdown, .txt, .html and .docx; writes those plus .pdf, .xlsx and .pptx.",
            [],
            fun _ctx input -> task {
                try
                    let a = parseArgs input
                    let sourceRaw = (a.StringOr("source", "")).Trim()
                    let targetRaw = (a.StringOr("target", "")).Trim()
                    if String.IsNullOrWhiteSpace sourceRaw || String.IsNullOrWhiteSpace targetRaw then
                        return json {| error = "Expected JSON {\"source\":\"...\",\"target\":\"...\"} (target is a filename or a format like 'pdf')" |}
                    else
                        let sourcePath = resolvePath sourceRaw
                        if not (File.Exists sourcePath) then
                            return json {| error = sprintf "Source not found: %s" sourceRaw |}
                        else
                            match resolveTargetName sourceRaw targetRaw with
                            | None ->
                                return json {| error = sprintf "Unsupported target format: %s" targetRaw |}
                            | Some targetName ->
                                let targetPath = resolvePath targetName
                                let srcMt = mediaTypeForExt (Path.GetExtension sourcePath)
                                let tgtMt = mediaTypeForExt (Path.GetExtension targetPath)
                                match srcMt, tgtMt with
                                | None, _ ->
                                    return json {| error = sprintf "Unsupported source format: %s" (Path.GetExtension sourcePath) |}
                                | _, None ->
                                    return json {| error = sprintf "Unsupported target format: %s" (Path.GetExtension targetPath) |}
                                | Some src, Some tgt ->
                                    // Authorize the SPECIFIC resources this conversion touches, in order:
                                    // read the chosen source, then write the resolved output. Either denial
                                    // aborts before any bytes are read or written.
                                    let readAccess = ResourceAccess.File("read", sourcePath)
                                    let writeAccess = ResourceAccess.File("write", targetPath)
                                    let! okRead =
                                        ToolPermissions.requestConfirmedAsync readAccess
                                            (sprintf "Convert document: read the source file '%s'." sourceRaw)
                                    if not okRead then
                                        return PermissionDenied.format readAccess None
                                    else
                                        let! okWrite =
                                            ToolPermissions.requestConfirmedAsync writeAccess
                                                (sprintf "Convert document: write the converted output '%s'." targetName)
                                        if not okWrite then
                                            return PermissionDenied.format writeAccess None
                                        else
                                            let dir = Path.GetDirectoryName(targetPath)
                                            if not (Directory.Exists dir) then Directory.CreateDirectory(dir) |> ignore
                                            Nao.Documents.Converter.convertFile documentRegistry src tgt sourcePath targetPath
                                            let bytes = (FileInfo targetPath).Length
                                            return json {| converted = targetPath.Replace("\\", "/")
                                                           from = src
                                                           ``to`` = tgt
                                                           bytes = bytes |}
                with ex ->
                    return json {| error = ex.Message |}
            }) with
            Schema =
                [ reqParam "source" "string" "Path or name of the source document to convert."
                  reqParam "target" "string" "Output filename, or a bare format: pdf | docx | html | xlsx | pptx | md | txt." ] }
