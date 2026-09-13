# 2026-09 execution-journal failures

## Scope

- Current unreleased format: version 1.
- Affected packages and APIs: `Nao.Agents.ExecutionRecord` and Nao persistence observability adapters.
- Affected files and streams: identity-partitioned `execution-journal/<identity-sha256>` version-1 streams.

## Breaking changes

Owner-free execution records carry an optional typed tool failure inside authenticated version-1 journal events. Failed records can produce deterministic replay fixtures but are excluded from compensation candidates.

## Before upgrade

1. Stop writers.
2. Delete unreleased execution-journal development data.
3. Deploy the identity-partitioned implementation.

## Migration

Nao is unreleased, so there is no compatibility reader or retained-data migration. Do not infer failures for discarded historical records.

## Validation

Run the execution-journal backend parity tests and deterministic replay tests. Confirm successful records remain compensation candidates, failed records round-trip their typed failure, and failed records do not appear in `GetRevertibleAsync` results.

## Rollback

Rollback is unsupported for this unreleased breaking redesign.
