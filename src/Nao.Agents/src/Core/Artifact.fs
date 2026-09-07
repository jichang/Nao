namespace Nao.Agents

/// Immutable artifact produced by an agent or tool execution.
type Artifact =
  { Id: ArtifactId; IdempotencyKey: IdempotencyKey; Kind: string; ContentType: string; Payload: string }

[<RequireQualifiedAccess>]
module Artifact =
    let restore id idempotencyKey kind contentType payload =
        { Id = id; IdempotencyKey = idempotencyKey; Kind = kind; ContentType = contentType; Payload = payload }

    let create idempotencyKey kind contentType payload =
        restore (ArtifactId.generate ()) idempotencyKey kind contentType payload
