# 2026-09 embedding input provenance

## Scope

- Current unreleased format: version 1.
- Affected package and APIs: `Nao.Knowledge`, `EmbeddingExecutionConfig`,
  `EmbeddingProvenanceRecord`, and `EmbeddingProvenanceRecorder`.
- Affected data: successful source-backed embedding provider batches.

## Breaking changes

Embedding execution now requires a provenance recorder. Every validated provider batch is
attributed before cost accounting and before the executor reports success. Records contain
ordered chunk IDs, canonical content hashes, character and token counts, profile and batch
identities, provider attempts and aggregate usage, and timestamps.

Raw input text, vectors, provider bodies, failure messages, principal fields, credentials, and
authentication evidence are not part of the record.

## Migration

Nao is unreleased, so no optional provenance field or silent recorder is provided. Supply an
`EmbeddingProvenanceRecorder` in every `EmbeddingExecutionConfig`. Durable implementations must:

1. Partition writes using the separately supplied `AuthorizationScope`.
2. Treat the stable batch ID as the idempotency identity.
3. Reconstruct durable records through `EmbeddingProvenanceRecord.restore`.
4. Reject conflicting reuse of a batch identity.
5. Avoid enriching records with source text, vector values, provider response bodies, or
   authentication evidence.

Recorder failure must remain visible to the caller. Do not convert it into a successful
embedding result.

## Validation

Run `dotnet test tests/Nao.Knowledge.Tests/Nao.Knowledge.Tests.fsproj -c Release`. Verify exact
ordered hash/count attribution, retry attempt counts, durable restoration, absence of raw text,
no provenance for malformed provider output, fail-closed recorder errors, and cancellation
before telemetry work.

## Rollback

Restore the executor and all configuration constructors together. Remove v1 provenance records
only if their corresponding embedding audit history is intentionally discarded.
