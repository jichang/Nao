# 2026-09 typed execution and workspace identities

## Scope

- First incompatible version: the FND-06 development milestone.
- Affected packages and APIs: `Nao.Agents` execution, policy, audit, trace, event, agent-context, observability, and identity contracts; `Nao.Persistence` audit implementations; `Nao.Runtime.Orleans` workspace registry and session-grain service factory.
- Affected durable data: audit execution identifiers retain the existing canonical UUID string representation, so no stored-data transformation is required.

## Breaking changes

- `ExecutionContext.ExecutionId`, `AuditEntry.ExecutionId`, `PolicyContext.ExecutionId`, and `AuditLog.QueryByExecutionAsync` use `ExecutionId` instead of `Guid`.
- `ExecutionContext` requires a `CorrelationContext` and exposes explicit child-delegation and retry construction.
- The Orleans-local `{ Key: string }` workspace identity is removed. The registry uses the core `WorkspaceId` type.
- Harness telemetry serializes execution identifiers through `ExecutionId.serialize`.
- `EventScope.Create` and `AgentContext` require a `CorrelationContext`; uncorrelated values are not representable.
- `AgentContext.allowAll` is now a function that creates a fresh correlation root and must be called as `AgentContext.allowAll ()`.
- `EventScope.Empty` is replaced by `EventScope.CreateEmpty()`, which creates a fresh correlation root.
- `ObservabilityServices.ServicesFor`, `PublishingHarnessServices.create`, and the Orleans session-grain harness-services factory require correlation.
- `SecurityPrincipal` owns authenticated tenant, user, and group identity. `AuthorizationScope.tryCreate` rejects groups absent from the principal and never accepts tenant or user from request data.

## Before upgrade

1. Stop writers and back up audit storage according to the active backend.
2. Identify callers that construct raw execution GUIDs, access `WorkspaceId.Key`, create event or agent contexts, or construct harness-service factories.
3. Validate that the backup can be restored.

## Migration

1. Replace `Guid.NewGuid()` execution values with `ExecutionId.generate ()`. At trusted external boundaries, use `ExecutionId.ofGuid`, `ExecutionId.parse`, or `ExecutionId.tryParse` explicitly.
2. Replace GUID formatting and parsing with `ExecutionId.serialize` and `ExecutionId.parse`.
3. Replace `WorkspaceId.Key` with `WorkspaceId.value`; construct workspace IDs with `WorkspaceId.create` or `WorkspaceId.versioned`.
4. Pass the existing correlation to events, agent contexts, and observability services participating in an execution. Operations outside an existing execution must create a fresh root with `CorrelationContext.root ()`.
5. Replace `AgentContext.allowAll` values with `AgentContext.allowAll ()`, and replace `EventScope.Empty` with `EventScope.CreateEmpty()`.
6. Change Orleans harness-service factories to `Func<string, string, CorrelationContext, HarnessServices>`.
7. Construct authorization scopes from a host-authenticated `SecurityPrincipal`; do not reconstruct principals from request identifiers.
8. Recompile every caller. No compatibility overloads, aliases, or implicit conversions are provided.
9. Validate audit round trips, workspace registration, event propagation, delegation correlation, retry causation, and cross-tenant rejection.

## Rollback

The audit wire format is unchanged, so stored audit data can be read by the preceding development version. Source rollback requires reverting callers to raw GUIDs and the former Orleans workspace record. Restore the backup if any unrelated current-version writes must also be discarded.