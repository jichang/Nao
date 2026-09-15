# 2026-09 content-addressed embedding cache

## Scope

- Current unreleased format: version 1.
- Affected package and APIs: `Nao.Knowledge`, `KnowledgeEmbeddingCache`, and `CachedEmbeddingExecution`.
- Affected data: reusable source-backed embedding vectors.

## Breaking changes

Embedding reuse now uses stable cache keys derived from canonical content hash and the complete
embedding profile. Entries are private validated v1 records containing owner identity, profile,
vector, and generation/cache timestamps without source text. Cached execution deduplicates equal
content across chunk identities before invoking a provider.

## Before upgrade

1. Stop embedding writers.
2. Back up development cache data only if it must be inspected.
3. Record exact model and preprocessing versions for any retained vectors.

## Migration

Nao is unreleased, so no old-cache reader or inferred-key adapter is provided. Reset development
embedding caches and repopulate them through `CachedEmbeddingExecution`. Durable adapters must
reconstruct records with `EmbeddingCacheEntry.restore`, partition by complete authenticated
identity, copy vector buffers, and validate complete batches before mutation.

## Validation

Run `dotnet test tests/Nao.Knowledge.Tests/Nao.Knowledge.Tests.fsproj -c Release`. Verify
cross-chunk content deduplication, repeat cache hits, model/preprocessing profile isolation,
authorization isolation, failed-provider behavior, defensive vector copying, and durable-record
validation.

## Rollback

Rollback requires discarding v1 cache entries or restoring the complete prior cache. Do not reuse
vectors whose model or preprocessing identity is unknown.
