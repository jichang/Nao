# Versioned tracer spans

## Scope

- Current unreleased format: version 1.
- Affected APIs: `Tracers.file` and `Tracers.ado` persisted representations.
- Affected data: `tracer/<identity-sha256>.jsonl` and rows in `tracer/<identity-sha256>` event streams.

## Breaking changes

Every tracer operation now requires an authenticated `AuthorizationScope`. Each claim-free authorization identity selects its own stream, and persisted span snapshots remain version-1 envelopes containing `schemaVersion` and `value`. Span payloads contain neither ownership nor authentication evidence. Bare spans, unsupported versions, malformed snapshots, and the former unpartitioned stream are rejected or ignored rather than read through compatibility logic.

## Before upgrade

1. Stop all processes writing tracer data.
2. Delete the unreleased unpartitioned `tracer.jsonl` file and `tracer` event stream.
3. Deploy the identity-partitioned implementation and begin new version-1 streams.

## Migration

Nao is unreleased, so there is no compatibility reader or retained-data migration. Development trace data from the former abstraction must be discarded.

## Rollback

Rollback is unsupported for this unreleased breaking redesign. Never copy unpartitioned spans into an authenticated identity stream.