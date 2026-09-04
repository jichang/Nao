# FND-03 lifecycle cleanup

This development-stage change intentionally breaks earlier persistence and API shapes in favor of explicit ownership and lifecycle contracts. Nao does not provide dual reads or an in-process migration path.

## Breaking changes

- `ExecutionRecord` now requires `Id`, `Owner`, and `TurnId`.
- `ExecutionJournal.MarkRevertedAsync` accepts the stable record ID.
- ADO execution journals use `nao_execution_journal` with dedicated identity, owner, and turn columns.
- File execution journals use a versioned document with `schemaVersion = 1` and a `records` collection.
- Turn and feedback JSONL files accept typed lifecycle events only; raw record lines are unsupported.
- `MemoryStore.ClearAsync` was removed. `DeleteOwnerAsync` is the sole owner-wide deletion operation.
- `WorkingMemoryItem` now requires `ExecutionId`; all working-memory operations are execution-scoped and `ClearAsync` was replaced by counted `DeleteOwnerAsync` and `DeleteExpiredAsync` operations.
- Working-memory file and ADO.NET streams now contain version-1 scoped event envelopes. Default and unpin expiry times are normalized before persistence; owner and expiry deletion events survive replay.
- `Episode` now requires `Owner`; all episodic-memory reads and mutations are owner-scoped, and the capability exposes counted owner and strict timestamp-cutoff deletion.
- Episodic-memory file and ADO.NET streams now contain version-1 owner-scoped event envelopes with durable owner and cutoff tombstones.
- `GraphNode` and `GraphRelation` now require `Owner`; graph queries and mutations are owner-scoped, relation removal is effective, and node deletion cascades incident relations.
- Graph-memory file and ADO.NET streams now contain version-1 owner-scoped event envelopes with durable node, relation, owner, and cutoff tombstones.
- `TieredMemoryEntry` now requires `Owner`; retrieval, access recording, promotion, capacity, eviction, and deletion are owner-scoped. Retrieval no longer mutates records implicitly, and eviction requires an effective time.
- Tiered-memory file and ADO.NET streams now contain version-1 owner-scoped event envelopes with explicit access and eviction times plus durable owner and cutoff tombstones.
- `MetricsCollector` accepts complete `MetricRecord` values with stable identity, owner, timestamp, and typed payload. Aggregation and cost estimation require an owner, and owner/cutoff deletion is counted.
- Metrics file and ADO.NET streams now contain version-1 accepted-record events and durable owner and strict timestamp-cutoff tombstones. Aggregates are rebuilt from retained records, and duration is derived from accepted timestamps rather than collector lifetime.
- `FeedbackService` now exposes session-turn deletion through its owning facade.
- Session deletion coordinates conversation, turn, memory, metric, and execution-journal cleanup. Owner deletion returns the first structured failure and leaves directory/runtime identity intact for retry.
- `EvalDataset`, `EvalRun`, `EvalResult`, and `EvalReport` now require stable identity and explicit owner correlation. Runner APIs require a run identity for standalone cases and propagate one run through dataset reports.
- `EvalArchive` provides in-memory and version-1 JSONL persistence for datasets and reports, including owner and strict timestamp-cutoff tombstones. No ownerless evaluation archive format is accepted.
- Provider failures are no longer returned as `CompletionResult` values with `FinishReason = "error"`. Malformed output, HTTP failures, timeouts, and transport failures raise `PlatformFailureException` with the canonical category and retryability.
- Orleans host operations preserve structured harness, storage, and lifecycle failures instead of converting them to display strings or `InvalidOperationException`.

## Upgrade action

Development deployments must delete existing `nao_journal` tables and old `execution-journal.json`, `turns.jsonl`, `feedback.jsonl`, `working.jsonl`, `episodic.jsonl`, `graph.jsonl`, `tiered.jsonl`, and `metrics.jsonl` files, plus the previous ADO.NET `working`, `episodic`, `graph`, `tiered`, and `metrics` event streams, before running the new version. Export data externally first if it must be retained.

No old record is assigned an inferred owner. Re-imported records must be transformed explicitly into the current schema with trustworthy owner and identity values.
