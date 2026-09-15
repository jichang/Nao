# 2026-09 validated embedding persistence

## Scope

- Current unreleased format: version 1.
- Affected package and APIs: `Nao.Knowledge`, `PersistedEmbedding`, and `KnowledgeEmbeddingStore`.
- Affected data: source-backed generated vectors persisted for reuse or indexing.

## Breaking changes

Storage adapters now accept private validated `PersistedEmbedding` records rather than arbitrary
vector arrays. Records bind the complete authorization identity, chunk and content hash, complete
embedding profile, exact vector, and generation time to a stable chunk/profile identity.

## Before upgrade

1. Stop embedding and index writers.
2. Back up development embedding data if it must be inspected.
3. Record each existing vector's exact model, preprocessing, dimensions, normalization, and distance metric.

## Migration

Nao is unreleased, so no permissive reader or inferred profile migration is provided. Reset old
development vectors and regenerate them through `EmbeddingPersistence.saveAsync`. Host-owned
adapters must validate complete batches before mutation and partition all records by the
authenticated authorization identity.

## Validation

Run `dotnet test tests/Nao.Knowledge.Tests/Nao.Knowledge.Tests.fsproj -c Release`. Verify that
incorrect dimensions and non-finite vectors fail before any batch write, arrays cannot mutate
stored values, equivalent retries preserve the first record, conflicting vectors cannot rebind a
stable identity, and cross-identity reads return no data.

## Rollback

Rollback requires restoring the complete pre-change embedding store. Do not convert validated v1
records back into unprofiled arrays or mix them with pre-change data.
