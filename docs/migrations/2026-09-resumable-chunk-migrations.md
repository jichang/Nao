# 2026-09 resumable chunk migrations

## Scope

- Current unreleased format: version 1.
- Affected package and APIs: `Nao.Knowledge.KnowledgeCatalog`, `KnowledgeCatalogSnapshot`, and chunk persistence adapters.
- Affected data: persisted chunks, index entries, retrieval-cache entries, and chunking-migration checkpoints.

## Breaking changes

`KnowledgeCatalog` now exposes chunking-migration input, checkpoint, and atomic step-commit operations.
`KnowledgeCatalogSnapshot` includes v1 chunking-migration checkpoints. A strategy change replaces
the complete chunk set for each document version instead of accumulating chunks from multiple
strategies. Index entries and retrieval-cache entries that reference replaced chunks are invalidated
in the same commit.

## Before upgrade

1. Stop knowledge ingestion and indexing writers.
2. Back up any development knowledge catalog data that must be inspected.
3. Verify that source documents can be acquired and parsed again.

## Migration

Nao is unreleased, so there is no compatibility adapter for prior catalog implementations. Reset
development catalog data, implement the new atomic migration operations in host-owned durable
catalog adapters, and re-ingest source documents. Do not copy old chunks into a v1 migration
checkpoint or retain dual reads across chunk strategies.

## Validation

Run `dotnet test tests/Nao.Knowledge.Tests/Nao.Knowledge.Tests.fsproj -c Release`. Confirm that an
interrupted migration resumes after its last committed document, stale index/cache references are
removed, completed reruns are no-ops, and chunks outside the pinned target strategy are rejected
without checkpoint advancement.

## Rollback

Rollback after a migration step requires restoring the entire catalog backup because chunks,
indexes, caches, and checkpoints change atomically. Mixing restored old chunks with new v1
checkpoints is unsupported.
