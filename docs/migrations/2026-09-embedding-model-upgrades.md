# 2026-09 embedding model upgrades

## Scope

- Current unreleased format: version 1.
- Affected package and APIs: `Nao.Knowledge`, `EmbeddingMigration`, and
  `EmbeddingUpgradeReads`.
- Affected data: embedding cache entries and embedding-migration checkpoints.

## Breaking changes

Embedding model changes now use an explicit source profile, target profile, canonical
content-hash plan, and read mode. A migration checkpoint contains profile metadata, content
hashes, cursor, revision, and timestamps, but no source text. Read results always identify the
single profile that produced the complete result.

## Before upgrade

1. Stop writers that can change the selected content plan.
2. Ensure every planned content hash has a source-profile cache entry.
3. Provision a separate index compatible with the complete target profile.
4. Retain the source index and cache until target-only reads have been validated.

## Migration

Nao is unreleased, so no implicit model fallback or mixed-profile adapter is provided. Create a
target `CachedEmbeddingExecutor`, run `EmbeddingMigration`, and durably implement
`KnowledgeEmbeddingMigrationStore` with authorization partitioning and compare-and-swap
revisions. Keep reads source-only before backfill, use target-first while validating the target
index, and switch explicitly to target-only after the complete target is ready.

The target cache write precedes each checkpoint update. If checkpoint persistence fails, rerun
the same plan: already-generated target vectors resolve as cache hits. Do not change source,
target, or content plan under an existing migration identity.

## Validation

Run `dotnet test tests/Nao.Knowledge.Tests/Nao.Knowledge.Tests.fsproj -c Release`. Verify
interruption and resume, target cache population, source fallback, complete target selection,
target-only fail-closed behavior, profile dimension separation, and authorization isolation.

## Rollback

Switch reads explicitly back to source-only while the source index and cache remain intact.
Discard the target index, target-profile cache entries, and matching migration checkpoints
together. Never copy vectors between profiles or reinterpret one profile under another.
