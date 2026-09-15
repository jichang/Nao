# 2026-09 embedding provider adapters

## Scope

- Current unreleased contract: version 1.
- Affected packages and APIs: `Nao.Knowledge`, `Nao.Providers.OpenAICompatible`,
  `Nao.Providers.Ollama`, and `KnowledgeEmbeddingProvider`.
- Affected runtime resources: provider-owned HTTP clients.

## Breaking changes

`KnowledgeEmbeddingProvider` now includes `Dispose`. Concrete hosted OpenAI-compatible and local
Ollama adapters require a complete embedding profile and an authoritative tokenizer callback.
The configured model must equal the profile's model version.

## Migration

Nao is unreleased, so no compatibility wrapper is provided. Add `Dispose = ignore` to
non-resource-owning test or in-process providers. Dispose HTTP-backed providers at the end of
their application lifetime.

For hosted providers, configure a complete embeddings endpoint and use `SecretReference` plus
`SecretProvider` rather than storing an API key in configuration. For Ollama, configure only an
HTTP(S) origin; the adapter owns the `/api/embed` path. Pin actual model versions rather than
mutable aliases, and supply the tokenizer represented by the profile preprocessing identity.

## Validation

Run both:

```text
dotnet test tests/Nao.Providers.Tests/Nao.Providers.Tests.fsproj -c Release
dotnet test tests/Nao.Knowledge.Tests/Nao.Knowledge.Tests.fsproj -c Release
```

Verify response ordering and model identity, operation-scoped secret resolution, provider usage
and hosted cost, local zero cost, cancellation, and redacted HTTP failures.

## Rollback

Remove the concrete adapters and restore every provider constructor together with the prior
contract. Do not retain undisposed HTTP-backed providers or move raw credentials into
configuration.
