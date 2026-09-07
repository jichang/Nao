# 2026-09 harness checkpoints

## Scope

- First incompatible version: next unreleased Nao package version.
- Affected packages and APIs: `Nao.Agents.ExecutionJournal` and Nao persistence observability adapters.
- Affected files, streams, tables, or Orleans state: `harness-checkpoints.json`, `tool-side-effects.json`, `nao_harness_checkpoints`, and `nao_tool_side_effects`.

## Breaking changes

`ExecutionJournal` now includes checkpoint and side-effect transition journals. Checkpoints carry stable turn idempotency keys for cross-attempt lookup. Harness execution writes accepted, execution-started, and terminal facts, while externally side-effecting tools write intent before invocation and outcome afterward.

## Before upgrade

1. Stop writers.
2. Back up persistence directories and databases.
3. Validate that the backup can be restored.

## Migration

1. Deploy the new version; file storage creates the checkpoint and side-effect documents on first write, and ADO.NET creates their version-1 tables and indexes.
2. Do not synthesize checkpoints for executions created by earlier versions.
3. Validate that new executions have one accepted phase, at most one execution-started phase, and one terminal phase.

## Validation

Run execution-journal backend parity tests, harness resume tests, documentation validation, and compare checkpoints by owner, execution identifier, and idempotency key. Confirm every external intent has a durable outcome before retrying interrupted work.

## Rollback

Existing execution-journal data remains rollback-compatible. Stop writers before rollback, then remove the additive checkpoint file/table or restore the backup. Checkpoints written by this version are unavailable to older binaries.
