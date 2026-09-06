# 2026-09 harness checkpoints

## Scope

- First incompatible version: next unreleased Nao package version.
- Affected packages and APIs: `Nao.Agents.ExecutionJournal` and Nao persistence observability adapters.
- Affected files, streams, tables, or Orleans state: new `harness-checkpoints.json` files and the new `nao_harness_checkpoints` table.

## Breaking changes

`ExecutionJournal` now includes a `HarnessCheckpointJournal` capability. Existing execution-journal records and schemas are unchanged. Harness execution writes accepted, execution-started, and terminal checkpoint facts when a journal is configured.

## Before upgrade

1. Stop writers.
2. Back up persistence directories and databases.
3. Validate that the backup can be restored.

## Migration

1. Deploy the new version; file storage creates `harness-checkpoints.json` on first write and ADO.NET creates the version-1 checkpoint table and index.
2. Do not synthesize checkpoints for executions created by earlier versions.
3. Validate that new executions have one accepted phase, at most one execution-started phase, and one terminal phase.

## Validation

Run execution-journal backend parity tests, harness checkpoint tests, documentation validation, and compare checkpoint counts by owner and execution identifier.

## Rollback

Existing execution-journal data remains rollback-compatible. Stop writers before rollback, then remove the additive checkpoint file/table or restore the backup. Checkpoints written by this version are unavailable to older binaries.
