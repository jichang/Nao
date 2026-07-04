namespace Nao.Agents

/// Generic content metadata — describes what a tool/agent produces.
/// The framework carries this tag without interpreting the content itself;
/// consumers decide how to handle each content type.
type ContentMeta =
    { /// Content type identifier (e.g. "text/plain", "application/json", "document/pdf").
      /// User-defined — framework does not restrict values.
      ContentType: string
      /// Extensible key-value metadata (e.g. "filename", "encoding", "schema-url").
      Metadata: Map<string, string> }

    static member Text = { ContentType = "text/plain"; Metadata = Map.empty }
    static member Json = { ContentType = "application/json"; Metadata = Map.empty }
    static member Of (contentType: string) = { ContentType = contentType; Metadata = Map.empty }

    static member WithMeta (contentType: string) (meta: (string * string) list) =
        { ContentType = contentType; Metadata = Map.ofList meta }
