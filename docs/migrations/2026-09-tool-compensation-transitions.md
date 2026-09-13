# 2026-09 tool compensation transitions

## Scope

- Current unreleased format: version 1.
- Affected packages and APIs: `Nao.Agents.ToolSideEffectPhase`, the ETCLOVG harness, and Nao persistence observability adapters.
- Affected files and streams: identity-partitioned `execution-journal/<identity-sha256>` version-1 streams.

## Breaking changes

Tool side-effect transitions require `AuthorizationScope` and no longer contain owner strings. Failed harness executions compensate successful reversible tool calls in reverse execution order. Each outcome retains the original operation identity and correlation; only a durably recorded successful compensation marks its execution record reverted. Records, checkpoints, side effects, compensation marks, and deletion tombstones share one version-1 identity stream. Reversible tools require an execution journal.

## Before upgrade

1. Stop writers.
2. Delete unreleased execution-journal development data.
3. Deploy the identity-partitioned implementation.

## Migration

Nao is unreleased, so there is no compatibility reader or retained-data migration.

## Validation

Run harness compensation and execution-journal identity-isolation tests. Confirm successful reversible calls unwind newest-first, compensation outcomes retain the original execution and idempotency identities, failed compensation remains revertible, and file and ADO.NET streams replay all version-1 phases.

## Rollback

Rollback is unsupported for this unreleased breaking redesign.
