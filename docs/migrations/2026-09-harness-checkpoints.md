# 2026-09 harness checkpoints

## Scope

- Current unreleased format: version 1.
- Affected packages and APIs: `Nao.Agents.ExecutionJournal` and Nao persistence observability adapters.
- Affected files and streams: `execution-journal/<identity-sha256>.jsonl` and `execution-journal/<identity-sha256>` ADO event streams.

## Breaking changes

Every execution-journal, checkpoint, and side-effect operation now requires an authenticated `AuthorizationScope`. Owner strings were removed from all three payload types. Checkpoints retain stable turn idempotency keys for cross-attempt lookup. Harness execution writes accepted, execution-started, and terminal facts, while externally side-effecting tools write intent before invocation and outcome afterward into one identity-partitioned version-1 stream.

## Before upgrade

1. Stop writers.
2. Delete the unreleased journal, checkpoint, and side-effect development files or tables.
3. Deploy the identity-partitioned implementation and begin new version-1 streams.

## Migration

Nao is unreleased, so there is no compatibility reader or retained-data migration. Do not synthesize checkpoints for discarded development executions.

## Validation

Run execution-journal identity-isolation, backend parity, and harness resume tests. Compare checkpoints by authenticated scope, execution identifier, and idempotency key. Confirm every external intent has a durable outcome before retrying interrupted work.

## Rollback

Rollback is unsupported for this unreleased breaking redesign. Never copy unpartitioned records into an authenticated identity stream.
