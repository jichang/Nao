# 2026-09 ambiguous-outcome quarantine

## Scope

- Current unreleased format: version 1.
- Affected packages and APIs: `Nao.Agents.ToolSideEffectPhase`, the ETCLOVG harness, and Nao persistence observability adapters.
- Affected files and streams: identity-partitioned `execution-journal/<identity-sha256>` version-1 streams.

## Breaking changes

`ToolSideEffectPhase` adds `Quarantined`, serialized as `quarantined`. Externally side-effecting operations are quarantined when interruption or a transient dependency failure cannot prove whether the effect committed. An incomplete execution with a durable successful side effect is also quarantined during recovery because replay could duplicate it.

Quarantine forces an indeterminate harness result, survives repeated recovery without duplicate transitions, and excludes the operation from automatic compensation.

## Before upgrade

1. Stop writers.
2. Delete unreleased execution-journal development data.
3. Deploy the identity-partitioned implementation.

## Migration

Nao is unreleased, so there is no compatibility reader or retained-data migration. Do not convert historical interrupted operations to safe outcomes without external evidence.

## Validation

Run harness restart, network ambiguity, compensation, and execution-journal backend parity tests. Confirm incomplete intent, interruption, failed compensation, and committed-success states append quarantine before retry; existing quarantine remains stable; transient external failures produce indeterminate results; and quarantined operation keys do not appear in revertible queries.

## Rollback

Rollback is unsupported for this unreleased breaking redesign.
