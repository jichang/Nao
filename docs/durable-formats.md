# Durable formats

Nao supports only the current durable schema during active development. Until a durable format has a released compatibility contract, its current schema marker remains version 1 when the shape changes. Breaking changes require a migration guide with external transformation or reset instructions; runtime libraries do not carry legacy readers by default.

## Decode policy

- Unknown JSON object fields are ignored so additive fields do not break current readers.
- Unknown discriminated-union, event-kind, and enum-like values are rejected because their semantics cannot be inferred safely.
- Explicitly versioned documents and events reject unsupported versions before append or rewrite.
- Corrupt data must fail with an actionable location or storage diagnostic and must not be treated as empty state.
- New durable formats must define a current schema marker before they are added to this inventory.

## Inventory

| Durable format | Backend | Current marker | Current behavior and remaining work |
|---|---|---|---|
| Conversation messages, metadata, and index | File | `schemaVersion = 1` | Messages retain full execution correlation and support execution lookup; unsupported, unversioned, or corrupt documents reject before append. |
| Turn lifecycle events | JSONL | `schemaVersion = 1` | Every turn retains full execution correlation; unsupported versions and unknown kinds reject with line diagnostics. |
| Feedback lifecycle events | JSONL | `schemaVersion = 1` | Unsupported versions and unknown kinds reject with line diagnostics. |
| Turn records | ADO.NET | Component schema version 1 | Every turn retains full execution correlation; schema and all payload rows are validated before mutation. |
| Feedback records | ADO.NET | Component schema version 1 | Schema and all payload rows are validated before mutation; invalid payloads identify their row. |
| Working-memory events | JSONL or ADO.NET event stream | Event envelope version 1; ADO component version 1 | The full stream is validated before replay and every mutation; diagnostics identify the stream and event position. |
| Episodic, graph, and tiered memory events | JSONL or ADO.NET event stream | Event envelope version 1; ADO component version 1 | The full stream is validated before replay and every mutation; diagnostics identify the stream and event position. |
| Metrics events | JSONL or ADO.NET event stream | Event envelope version 1; ADO component version 1 | Every metric retains full execution correlation; the full stream is validated before replay and every mutation. |
| Trace-store events | JSONL or ADO.NET event stream | Event envelope version 1; ADO component version 1 | Every trace retains full execution correlation and supports execution lookup; the full stream is validated before replay and every mutation. |
| Tracer spans | JSONL or ADO.NET event stream | Event envelope version 1; ADO component version 1 | Every root and child span retains full execution correlation and supports execution lookup; incompatible streams reject before mutation. |
| Key/value and semantic memory | File | `schemaVersion = 1` | Unsupported, unversioned, empty, or corrupt documents reject before mutation; migration requires external wrapping or reset. |
| Key/value and semantic memory | ADO.NET | Component schema version 1 | Fresh schemas initialize atomically; schema and all existing rows are validated before mutation with record diagnostics. |
| Audit log | File | `schemaVersion = 1` | Unsupported, unversioned, empty, or corrupt documents reject before mutation; unknown action kinds reject during decoding. |
| Audit log | ADO.NET | Component schema version 1 | Schema and all existing rows are validated before mutation; invalid actions identify their row. |
| Execution journal | File | `schemaVersion = 1` | Records retain full execution correlation and support execution lookup; unsupported or corrupt documents reject before mutation. |
| Execution journal | ADO.NET | Component schema version 1 | Indexed execution lookup is supported; schema and all existing rows are validated before mutation with record diagnostics. |
| Evaluation archive | JSONL | Event envelope version 1 | Results support owner-scoped execution lookup; unsupported versions reject before directory creation or append. |
| Orleans session state | Orleans provider | Nao schema version 1 plus Orleans field IDs | Conversation messages retain full execution correlation; persisted missing or unsupported versions reject before state access or mutation. |
| Orleans session-directory state | Orleans provider | Nao schema version 1 plus Orleans field IDs | New state is stamped during activation; persisted missing or unsupported versions reject before state access or mutation. |

Knowledge-record persistence is not implemented and therefore has no durable format yet. New durable capabilities must define ownership, deletion, current schema version, rejection behavior, and a migration-guide path with their first implementation.

## Change process

1. Change the current schema directly.
2. Reject old or incompatible state before mutation.
3. Add a guide under `docs/migrations/` describing backup, external transformation or reset, validation, and rollback limits.
4. Update this inventory and affected tests.

See the [migration policy](migrations/README.md) for the required guide structure.