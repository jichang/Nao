# 2026-09 execution-journal failures

## Scope

- First incompatible version: next unreleased Nao package version.
- Affected packages and APIs: `Nao.Agents.ExecutionRecord` and Nao persistence observability adapters.
- Affected files, streams, tables, or Orleans state: execution-journal file records and `nao_execution_journal` rows.

## Breaking changes

Execution records now carry an optional typed tool failure. File records add `failureKind`, `failureMessage`, and `failureRetryable`; ADO.NET rows add nullable `failure_kind`, `failure_message`, and `failure_retryable` columns. The three values must all be present or all be absent. Failed records can produce deterministic replay fixtures but are excluded from compensation candidates.

## Before upgrade

1. Stop writers.
2. Back up execution-journal files and databases.
3. Validate that the backup can be restored.

## Migration

Nao does not read the previous execution-journal shape. Reset existing execution-journal data, or externally transform every record by adding absent failure fields to file records and nullable failure columns to the ADO.NET table. Do not infer failures for historical records.

For ADO.NET storage, the clean development path is to drop the existing execution-journal component and let Nao create the current schema. Preserve other component tables when they share the same database.

## Validation

Run the execution-journal backend parity tests and deterministic replay tests. Confirm successful records remain compensation candidates, failed records round-trip their typed failure, and failed records do not appear in `GetRevertibleAsync` results.

## Rollback

The new execution-journal shape is not readable by older binaries. Stop writers and restore the pre-upgrade backup before rolling back. Failed outcomes recorded after upgrade cannot be retained through rollback without an external reverse transformation.
