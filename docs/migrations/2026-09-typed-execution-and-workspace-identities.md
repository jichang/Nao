# 2026-09 typed execution and workspace identities

## Scope

- First incompatible version: the FND-06 development milestone.
- Affected packages and APIs: `Nao.Agents` execution, policy, audit, trace, event, agent-context, observability, provider, working-memory, execution-journal, verification, and identity contracts; `Nao.Eval` evaluators and results; `Nao.Persistence` audit, working-memory, execution-journal, and trace-store implementations; `Nao.Runtime.Orleans` workspace registry, session-grain principal accessor, and harness-service factory.
- Affected durable data: audit execution identifiers retain the existing canonical UUID string representation. Current version-1 conversation, turn, working-memory, metrics, tracer, execution-journal, trace-store, and Orleans session schemas require complete execution correlation, and evaluation results gain a required execution identifier.

## Breaking changes

- `ExecutionContext.ExecutionId`, `AuditEntry.ExecutionId`, `PolicyContext.ExecutionId`, and `AuditLog.QueryByExecutionAsync` use `ExecutionId` instead of `Guid`.
- `ExecutionContext` requires a `CorrelationContext` and exposes explicit child-delegation and retry construction.
- The Orleans-local `{ Key: string }` workspace identity is removed. The registry uses the core `WorkspaceId` type.
- Harness telemetry serializes execution identifiers through `ExecutionId.serialize`.
- `EventScope.Create` and `AgentContext` require a `CorrelationContext`; uncorrelated values are not representable.
- `AgentContext.unrestrictedForTests` is a function that creates a fresh correlation root for isolated tests and must be called as `AgentContext.unrestrictedForTests ()`.
- `EventScope.Empty` is replaced by `EventScope.CreateEmpty()`, which creates a fresh correlation root.
- `ObservabilityServices.ServicesFor`, `PublishingHarnessServices.create`, and the Orleans session-grain harness-services factory require correlation.
- `LlmProvider.CompleteAsync` and `StreamAsync` require the current `CorrelationContext`; provider adapters and orchestrators must forward it unchanged.
- `Summarizer.summarizeAsync`, `Summarizer.applyAsync`, `ContextCompaction.summarizeChunkAsync`, `ContextCompaction.hierarchicalCompactAsync`, `ContextCompaction.applyAsync`, `MemoryConsolidation.summarizeClusterAsync`, `MemoryConsolidation.consolidateAsync`, and `Verification.groundTaskAsync` require `CorrelationContext` as their first argument.
- `WorkingMemoryItem.ExecutionId` and every working-memory owner operation use `ExecutionId` instead of `string`. Durable working-memory events use the current version-1 schema.
- `ExecutionRecord.Correlation` is required. File and ADO.NET execution journals use the current version-1 schema and persist execution, correlation, causation, and attempt identity.
- `ExecutionTrace.Correlation` is required, and `Verification.startTrace` requires it as its first argument. Trace-store events use the current version-1 schema; verification LLM judges forward the trace correlation unchanged.
- `MetricRecord.Correlation` is required, metric constructors require it as their first argument, and `MetricsCollector.GetByExecution` retrieves all observations for a typed execution ID. Metrics events use the current version-1 schema.
- `TurnRecord.Correlation` is required, `TurnRecorder.create` requires it after `turnId`, and `TurnStore.GetForExecutionAsync` retrieves authoritative turn outcomes for a typed execution ID. Turn JSONL events and the ADO.NET turns component use the current version-1 schema.
- `PersistedMessage` and `ConversationMessage` require complete correlation, and `ConversationStore.LoadByExecutionAsync` retrieves transcript messages for a typed execution ID. Conversation file documents, Orleans session state, and session-directory state use the current version-1 schema.
- `Span.Correlation` is required, `Tracer.StartTrace` requires correlation as its first argument, child spans inherit it, and `Tracer.GetByExecution` retrieves spans for a typed execution ID. Persistent tracer events use the current version-1 schema.
- `Evaluator.EvaluateAsync`, `Evaluator.create`, and `Evaluator.evaluateAsync` require `CorrelationContext`. Composite and LLM-backed evaluators must forward it unchanged.
- `EvalResult.ExecutionId` records the execution that produced each case result.
- `SecurityPrincipal` owns authenticated tenant, user, and group identity. `AuthorizationScope.tryCreate` rejects groups absent from the principal and never accepts tenant or user from request data.
- `SessionGrain` requires a host-injected `Func<SecurityPrincipal>`. Session start matches the principal user to the grain key, validates group membership, persists the complete authorization lineage, and every subsequent operation revalidates that lineage before state access or mutation.

## Before upgrade

1. Stop writers and back up conversation, audit, turn, working-memory, metrics, tracer, execution-journal, trace-store, evaluation, and Orleans session storage according to the active backends.
2. Identify callers that construct raw execution GUIDs, pass string working-memory owners, call providers, evaluators, memory helpers, or task grounding, access `WorkspaceId.Key`, create event or agent contexts, or host Orleans session grains and their service factories.
3. Validate that the backup can be restored.

## Migration

1. Replace `Guid.NewGuid()` execution values with `ExecutionId.generate ()`. At trusted external boundaries, use `ExecutionId.ofGuid`, `ExecutionId.parse`, or `ExecutionId.tryParse` explicitly.
2. Replace GUID formatting and parsing with `ExecutionId.serialize` and `ExecutionId.parse`.
3. Replace `WorkspaceId.Key` with `WorkspaceId.value`; construct workspace IDs with `WorkspaceId.create` or `WorkspaceId.versioned`.
4. Pass the existing correlation to events, agent contexts, and observability services participating in an execution. Operations outside an existing execution must create a fresh root with `CorrelationContext.root ()`.
5. Replace permissive test contexts with `AgentContext.unrestrictedForTests ()`, and replace `EventScope.Empty` with `EventScope.CreateEmpty()`.
6. Change Orleans harness-service factories to `Func<string, string, CorrelationContext, HarnessServices>`.
7. Register `Func<SecurityPrincipal>` for Orleans session grains from the host's authenticated identity context. Construct authorization scopes from that principal; do not reconstruct principals from grain keys, routes, or request bodies. Reset or externally rebuild retained session state with exact tenant lineage before reopening it.
8. Pass the current correlation to every provider completion, streaming request, and evaluator invocation. Populate `EvalResult.ExecutionId` from the case's agent context; do not create a second root for LLM judging.
9. Convert working-memory owners to `ExecutionId`. Rebuild retained working-memory event documents externally in the current version-1 shape after validating every execution identifier with `ExecutionId.parse`; reset the stream when retention is unnecessary.
10. Populate each retained execution-journal record with its original complete correlation and rebuild file and ADO.NET storage in the current version-1 shape. Do not invent causation or collapse retries; reset journals when exact correlation cannot be reconstructed and retention policy permits deletion.
11. Pass the active correlation to `Verification.startTrace`. Rebuild retained trace-store events externally in the current version-1 shape only when their original correlation can be reconstructed; otherwise reset the trace stream when retention policy permits deletion.
12. Pass the active correlation to summarization, context-compaction, memory-consolidation, and task-grounding helpers. Standalone hosts must create a root explicitly at their operation boundary.
13. Pass the active correlation to every `MetricRecord` constructor. Rebuild retained metrics events externally in the current version-1 shape only when the original complete correlation can be reconstructed; otherwise reset the stream when retention policy permits deletion.
14. Pass the turn root to `TurnRecorder.create` and replace `TurnRecord.Empty` with `TurnRecord.empty correlation`. Rebuild retained turn JSONL events and ADO.NET rows in the current version-1 shape only when their original complete correlation can be reconstructed; otherwise reset turn storage when retention policy permits deletion.
15. Add the turn correlation to every retained conversation message and rebuild conversation file documents and Orleans session state externally in the current version-1 shape. Reset conversation/session state when exact execution identity cannot be reconstructed and retention policy permits deletion; do not invent roots for retained messages.
16. Pass the active correlation to every `Tracer.StartTrace` call. Rebuild retained tracer events externally in the current version-1 shape only when each span's original complete correlation can be reconstructed; otherwise reset the tracer stream when retention policy permits deletion.
17. Recompile every caller. No compatibility overloads, aliases, implicit conversions, or legacy readers are provided.
18. Validate conversation, audit, turn, working-memory, metrics, tracer, execution-journal, and trace-store round trips, execution-scoped transcript, turn, metric, and span reconstruction, evaluation archive parity, provider and helper propagation, workspace registration, event propagation, delegation correlation, retry causation, and cross-tenant rejection.

## Rollback

The audit wire format is unchanged, but preceding builds cannot read current version-1 conversation documents, Orleans session state, turn records, working-memory events, metrics events, tracer events, execution journals, trace-store events, or evaluation results whose shape requires execution identity. Source rollback requires reverting callers to raw GUIDs and strings and restoring the former Orleans workspace record. Restore the conversation, session-state, turn, working-memory, metrics, tracer, execution-journal, trace-store, and evaluation backups before reopening writers with the preceding build.