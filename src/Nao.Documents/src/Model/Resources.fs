namespace Nao.Documents

/// Stable identifier for an extracted media/binary resource within a document.
type ResourceId =
    | ResourceId of string

    member this.Value = let (ResourceId v) = this in v
    static member Of (id: string) = ResourceId id

/// The broad category of a referenced asset.
type ResourceKind =
    | Image
    | Video
    | Audio
    | Font
    /// An embedded sub-document or attached file (e.g. an .xlsx inside a .docx).
    | Embedded
    /// Any other binary blob.
    | Binary

/// A media / binary asset referenced by the document body.
///
/// To keep the unified document small and portable, binary bytes are NOT inlined.
/// Instead a reader extracts them to local storage (via `IResourceStore`) and records
/// a relative `LocalPath` here; the body references the asset by `Id`. The consumer
/// decides whether and how to load the bytes.
type Resource =
    { Id: ResourceId
      Kind: ResourceKind
      /// IANA media type, e.g. "image/png", "video/mp4", "font/woff2".
      MediaType: string
      /// Relative path to the extracted bytes inside the document bundle
      /// (e.g. "resources/img-001.png"). `None` when only `Uri` is known.
      LocalPath: string option
      /// External location, when the asset is referenced rather than extracted.
      Uri: string option
      /// SHA-256 of the bytes (lower-case hex), enabling de-duplication and integrity checks.
      Sha256: string option
      /// Intrinsic pixel width, for images / video.
      PixelWidth: int option
      /// Intrinsic pixel height, for images / video.
      PixelHeight: int option
      /// Duration in milliseconds, for audio / video.
      DurationMs: int option
      /// Free-form extra metadata (codec, alt-text source, original filename, ...).
      Metadata: Map<string, string> }

    /// A locally-extracted resource with no extra intrinsic metadata.
    static member Local (id: string, kind: ResourceKind, mediaType: string, localPath: string) =
        { Id = ResourceId id
          Kind = kind
          MediaType = mediaType
          LocalPath = Some localPath
          Uri = None
          Sha256 = None
          PixelWidth = None
          PixelHeight = None
          DurationMs = None
          Metadata = Map.empty }
