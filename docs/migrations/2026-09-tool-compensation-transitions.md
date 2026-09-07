# 2026-09 tool compensation transitions

## Scope

- First incompatible version: next unreleased Nao package version.
- Affected packages and APIs: `Nao.Agents.ToolSideEffectPhase`, the ETCLOVG harness, and Nao persistence observability adapters.
- Affected files, streams, tables, or Orleans state: `tool-side-effects.json` and `nao_tool_side_effects` rows.

## Breaking changes

`ToolSideEffectPhase` adds `CompensationSucceeded` and `CompensationFailed`. Durable phase values add `compensation-succeeded` and `compensation-failed`. Failed harness executions compensate successful reversible tool calls in reverse execution order. Each outcome retains the original operation identity and correlation; only a durably recorded successful compensation marks its execution record reverted. Reversible tools now require an execution journal.

Older readers reject the new phase values because their semantics cannot be inferred safely.

## Before upgrade

1. Stop writers.
2. Back up execution-journal files and databases.
3. Validate that the backup can be restored.

## Migration

No transformation is required for existing transition rows. Deploy the new version before any writer emits compensation phases. All processes reading the same file or table must be upgraded together. Delete development journal data instead when retention is unnecessary.

## Validation

Run the harness compensation tests and execution-journal backend parity tests. Confirm successful reversible calls unwind newest-first, compensation outcomes retain the original execution and idempotency identities, failed compensation remains revertible, and file and ADO.NET readers round-trip the new phase values.

## Rollback

Rollback is not possible after compensation phases are written because older binaries reject them. Stop writers and restore the pre-upgrade backup before deploying an older version.
