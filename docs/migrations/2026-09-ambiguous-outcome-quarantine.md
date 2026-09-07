# 2026-09 ambiguous-outcome quarantine

## Scope

- First incompatible version: next unreleased Nao package version.
- Affected packages and APIs: `Nao.Agents.ToolSideEffectPhase`, the ETCLOVG harness, and Nao persistence observability adapters.
- Affected files, streams, tables, or Orleans state: `tool-side-effects.json` and `nao_tool_side_effects` rows.

## Breaking changes

`ToolSideEffectPhase` adds `Quarantined`, serialized as `quarantined`. Externally side-effecting operations are quarantined when interruption or a transient dependency failure cannot prove whether the effect committed. An incomplete execution with a durable successful side effect is also quarantined during recovery because replay could duplicate it.

Quarantine forces an indeterminate harness result, survives repeated recovery without duplicate transitions, and excludes the operation from automatic compensation. Older readers reject the new phase value.

## Before upgrade

1. Stop writers.
2. Back up execution-journal files and databases.
3. Validate that the backup can be restored.

## Migration

No transformation is required for existing transition rows. Upgrade every process sharing the side-effect journal before writers emit quarantine phases. Delete development journal data instead when retention is unnecessary. Do not convert historical interrupted operations to safe outcomes without external evidence.

## Validation

Run harness restart, network ambiguity, compensation, and execution-journal backend parity tests. Confirm incomplete intent, interruption, failed compensation, and committed-success states append quarantine before retry; existing quarantine remains stable; transient external failures produce indeterminate results; and quarantined operation keys do not appear in revertible queries.

## Rollback

Rollback is not possible after quarantine phases are written because older binaries reject them. Stop writers and restore the pre-upgrade backup before deploying an older version.
