namespace Nao.Agents

/// Immutable artifact produced by an agent or tool execution.
type Artifact =
    { Id: ArtifactId
      Kind: string
      ContentType: string
      Payload: string }

[<RequireQualifiedAccess>]
module Artifact =
    let restore id kind contentType payload =
        { Id = id
          Kind = kind
          ContentType = contentType
          Payload = payload }

    let create kind contentType payload =
        restore (ArtifactId.generate ()) kind contentType payload
