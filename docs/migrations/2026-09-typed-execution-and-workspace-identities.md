# 2026-09 typed execution and workspace identities

## Scope

- First incompatible version: the FND-06 development milestone.
- Affected packages and APIs: `Nao.Agents` execution, policy, audit, trace, and identity contracts; `Nao.Persistence` audit implementations; `Nao.Runtime.Orleans` workspace registry.
- Affected durable data: audit execution identifiers retain the existing canonical UUID string representation, so no stored-data transformation is required.

## Breaking changes

- `ExecutionContext.ExecutionId`, `AuditEntry.ExecutionId`, `PolicyContext.ExecutionId`, and `AuditLog.QueryByExecutionAsync` use `ExecutionId` instead of `Guid`.
- `ExecutionContext` requires a `CorrelationContext` and exposes explicit child-delegation and retry construction.
- The Orleans-local `{ Key: string }` workspace identity is removed. The registry uses the core `WorkspaceId` type.
- Harness telemetry serializes execution identifiers through `ExecutionId.serialize`.

## Before upgrade

1. Stop writers and back up audit storage according to the active backend.
2. Identify callers that construct raw execution GUIDs or access `WorkspaceId.Key`.
3. Validate that the backup can be restored.

## Migration

1. Replace `Guid.NewGuid()` execution values with `ExecutionId.generate ()`. At trusted external boundaries, use `ExecutionId.ofGuid`, `ExecutionId.parse`, or `ExecutionId.tryParse` explicitly.
2. Replace GUID formatting and parsing with `ExecutionId.serialize` and `ExecutionId.parse`.
3. Replace `WorkspaceId.Key` with `WorkspaceId.value`; construct workspace IDs with `WorkspaceId.create` or `WorkspaceId.versioned`.
4. Recompile every caller. No compatibility aliases or implicit conversions are provided.
5. Validate audit round trips, workspace registration, delegation correlation, and retry causation.

## Rollback

The audit wire format is unchanged, so stored audit data can be read by the preceding development version. Source rollback requires reverting callers to raw GUIDs and the former Orleans workspace record. Restore the backup if any unrelated current-version writes must also be discarded.