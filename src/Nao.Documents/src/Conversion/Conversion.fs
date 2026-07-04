namespace Nao.Documents

open System
open System.IO
open System.Security.Cryptography

/// Persists extracted binary resources to local storage. Readers call `Save` to
/// extract embedded media out of a source document and get back a relative path to
/// reference from a `Resource`; writers call `TryOpen` to read the bytes back when
/// they need to embed or copy them into the target.
type IResourceStore =
    /// Save bytes and return the relative path (e.g. "resources/img-001.png") to be
    /// stored on `Resource.LocalPath`.
    abstract member Save: suggestedName: string * mediaType: string * bytes: byte[] -> string
    /// Open a previously-saved resource by its relative path, if it exists.
    abstract member TryOpen: localPath: string -> Stream option

/// A filesystem-backed resource store rooted at a bundle directory. Extracted bytes
/// are written under `<root>/resources/` and de-duplicated by SHA-256 so identical
/// media is stored once.
type DirectoryResourceStore(root: string) =

    let resourcesDir = Path.Combine(root, "resources")

    let sha256Hex (bytes: byte[]) =
        use sha = SHA256.Create()
        sha.ComputeHash(bytes)
        |> Array.map (fun b -> b.ToString("x2"))
        |> String.concat ""

    /// Absolute path to the bundle root.
    member _.Root = root

    interface IResourceStore with
        member _.Save(suggestedName, _mediaType, bytes) =
            Directory.CreateDirectory(resourcesDir) |> ignore
            let hash = sha256Hex bytes
            let ext = Path.GetExtension(suggestedName)
            // Name by content hash for stable de-duplication; keep the extension.
            let fileName = hash.Substring(0, 16) + ext
            let fullPath = Path.Combine(resourcesDir, fileName)
            if not (File.Exists fullPath) then
                File.WriteAllBytes(fullPath, bytes)
            Path.Combine("resources", fileName).Replace('\\', '/')

        member _.TryOpen(localPath) =
            let full = Path.Combine(root, localPath)
            if File.Exists full then Some(File.OpenRead full :> Stream) else None

/// A resource store that discards bytes and never returns any. Useful for
/// conversions where media should be ignored or referenced by URI only.
type NullResourceStore() =
    interface IResourceStore with
        member _.Save(suggestedName, _mediaType, _bytes) =
            // No extraction; echo a stable-ish reference so the body still has a target.
            "resources/" + suggestedName
        member _.TryOpen(_localPath) = None

/// Context threaded through a conversion: where to extract/read resources, plus a
/// free-form options bag (target DPI, image policy, page size overrides, ...).
type ConversionContext =
    { Resources: IResourceStore
      Options: Map<string, string> }

    /// A context backed by a directory bundle.
    static member ForDirectory (root: string) =
        { Resources = DirectoryResourceStore(root) :> IResourceStore
          Options = Map.empty }

    /// A context that ignores resources entirely.
    static member InMemory =
        { Resources = NullResourceStore() :> IResourceStore
          Options = Map.empty }

    /// Read an option value by key.
    member this.Option (key: string) = this.Options.TryFind key

/// Parses a concrete format INTO the unified `Document`.
type IDocumentReader =
    /// IANA media types this reader handles, e.g. ["text/markdown"; "text/x-markdown"].
    abstract member MediaTypes: string list
    /// Read a source stream into the unified model, extracting media via `ctx`.
    abstract member Read: input: Stream * ctx: ConversionContext -> Document

/// Serializes the unified `Document` INTO a concrete format.
type IDocumentWriter =
    /// IANA media types this writer can produce.
    abstract member MediaTypes: string list
    /// Write the unified model to the output stream, resolving media via `ctx`.
    abstract member Write: doc: Document * output: Stream * ctx: ConversionContext -> unit
