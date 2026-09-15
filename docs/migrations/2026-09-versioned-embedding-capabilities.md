# 2026-09 versioned embedding capabilities

## Scope

- Current unreleased format: version 1.
- Affected package and APIs: `Nao.Knowledge`, `EmbeddingCapabilityDescriptor`, and `KnowledgeIndexEntry`.
- Affected data: knowledge index entries that persist embedding metadata.

## Breaking changes

The generic `EmbeddingDescriptor` record was replaced by the versioned
`EmbeddingCapabilityDescriptor`. The new descriptor carries a stable profile identity and explicit
model version, dimensions, normalization, distance metric, preprocessing version, maximum batch
size, maximum characters and tokens per item, and maximum tokens per batch.

## Before upgrade

1. Stop ingestion and index writers.
2. Back up development knowledge catalog and index data if it must be inspected.
3. Record the exact model and preprocessing versions used by each index.

## Migration

Nao is unreleased, so no compatibility alias or fallback deserializer is provided. Reset
development index entries and regenerate them using a validated v1 embedding capability profile.
Do not infer missing model versions, dimensions, normalization, distance metrics, preprocessing
versions, or limits from old records.

## Validation

Run `dotnet test tests/Nao.Knowledge.Tests/Nao.Knowledge.Tests.fsproj -c Release`. Confirm that
profile IDs are stable for identical metadata, change for every compatibility-relevant field, and
that malformed or forged descriptors fail before catalog persistence.

## Rollback

Rollback requires restoring the complete pre-change index backup. New profile-bearing entries
cannot be safely converted to the underspecified prior shape for mixed use.
