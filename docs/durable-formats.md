# Durable formats

Nao is unreleased. All Nao-owned protocol and durable-schema markers remain version 1 while their current shapes evolve in place; Nao package metadata uses 1.0.0. No earlier released format or backward-compatibility contract is implied. External protocol standards, dependencies, host-supplied version identifiers, and deliberately unsupported test values retain their own versions. Runtime libraries do not carry legacy readers.

## Decode policy

- Unknown JSON object fields are ignored so additive fields do not break current readers.
- Unknown discriminated-union, event-kind, and enum-like values are rejected because their semantics cannot be inferred safely.
- Explicitly versioned documents and events reject unsupported versions before append or rewrite.
- Corrupt data must fail with an actionable location or storage diagnostic and must not be treated as empty state.
- New durable formats must define a current schema marker before they are added to this inventory.

## Inventory

| Durable format | Backend | Current marker | Current behavior and remaining work |
|---|---|---|---|
| Conversation events | JSONL | Event envelope version 1 | Complete identity SHA-256 partitions select `conversation/<key>.jsonl`. Owner-free save, append, conversation-deletion, and scope-deletion events reconstruct messages and metadata. Execution lookup is scope-bound; corrupt streams and failed appends do not affect another scope or publish speculative state. |
| Turn lifecycle events | JSONL or ADO.NET event stream | Event envelope version 1; ADO component version 1 | Complete identity SHA-256 partitions select `turn-store/<key>.jsonl` files or `turn-store/<key>` ADO streams. Owner-free turn payloads retain execution correlation; scope and strict-cutoff deletion append tombstones. |
| Feedback lifecycle events | JSONL or ADO.NET event stream | Event envelope version 1; ADO component version 1 | Complete identity SHA-256 partitions select `feedback-store/<key>.jsonl` files or `feedback-store/<key>` ADO streams. Owner-free feedback payloads retain turn correlation; scope and strict-cutoff deletion append tombstones. |
| Working-memory events | JSONL or ADO.NET event stream | Event envelope version 1; ADO component version 1 | Authenticated execution addresses select `working/<identity-key>/<execution-id>.jsonl` files or equivalent ADO streams. Owner-free payloads record capacity/TTL/decay policy, never authentication or execution identity. Scoped replay rejects policy mismatch or corruption, reads are pure, and failed appends do not publish speculative state. |
| Tiered memory events | JSONL or ADO.NET event stream | Event envelope version 1; ADO component version 1 | Full identity SHA-256 partitions select `tiered/<key>.jsonl` files or `tiered/<key>` ADO streams. Owner-free events record the capacity/promotion/TTL policy and explicit access/eviction times. Scoped replay rejects policy mismatch or corruption before reads or mutations, never invokes embedding callbacks, and failed appends do not publish speculative state. |
| Episodic memory events | JSONL or ADO.NET event stream | Event envelope version 1; ADO component version 1 | Full identity SHA-256 partitions select `episodic/<key>.jsonl` files or `episodic/<key>` ADO streams. Owner-free record payloads include computed embeddings, not authentication evidence. Every read and mutation replays only the scoped stream without calling embedding providers; failures do not publish speculative state. |
| Graph memory events | JSONL or ADO.NET event stream | Event envelope version 1; ADO component version 1 | Complete identity SHA-256 partitions select `graph/<key>.jsonl` files or `graph/<key>` ADO streams. Owner-free payloads contain no authentication evidence. Every read and mutation validates and replays only its scoped stream; extraction persists a single batch event, and failed appends do not publish speculative state. |
| Metrics events | JSONL or ADO.NET event stream | Event envelope version 1; ADO component version 1 | Complete identity SHA-256 partitions select `metrics/<key>.jsonl` files or `metrics/<key>` ADO streams. Owner-free payloads retain full execution correlation and exclude authentication evidence. Every operation validates and replays only its authenticated partition; failed appends do not publish speculative state. |
| Trace-store events | JSONL or ADO.NET event stream | Event envelope version 1; ADO component version 1 | Complete identity SHA-256 partitions select `trace-store/<key>.jsonl` files or `trace-store/<key>` ADO streams. Owner-free events retain full execution correlation; `AgentId` remains actor metadata rather than ownership. Every operation validates and replays only its authenticated partition, and failed appends do not publish speculative state. |
| Tracer spans | JSONL or ADO.NET event stream | Event envelope version 1; ADO component version 1 | Complete identity SHA-256 partitions select `tracer/<key>.jsonl` files or `tracer/<key>` ADO streams. Owner-free spans retain full execution correlation and exclude authentication evidence. Every operation replays only its authenticated partition; cross-scope parent use and mutation fail closed, stale snapshots preserve accumulated state, and incompatible streams or failed appends cannot publish mutation. |
| Key/value memory | File | `schemaVersion = 1` | Documents are partitioned by the complete authorization identity's SHA-256 key, without authentication evidence; unsupported or corrupt current documents reject before mutation. |
| Key/value memory | ADO.NET | Component schema version 1 | `owner_key` partitions every query and mutation by the complete authorization lineage; incompatible table layouts and corrupt payload rows reject before mutation. |
| Semantic memory | File | `schemaVersion = 1` | Documents are partitioned by the complete authorization identity's SHA-256 key without authentication evidence; unsupported, unversioned, empty, or corrupt documents reject before mutation. |
| Semantic memory | ADO.NET | Component schema version 1 | `owner_key` partitions every query and mutation by the complete authorization lineage; incompatible table layouts and corrupt payload rows in the caller's scope reject before mutation. |
| Audit events | JSONL or ADO.NET event stream | Event envelope version 1; ADO component version 1 | Complete identity SHA-256 partitions select `audit/<key>.jsonl` files or `audit/<key>` ADO streams. Owner-free entries retain agent actor metadata and optional execution identity but exclude authentication evidence. Every operation validates and replays only its authenticated partition; retention and scope deletion append tombstones, and failed appends do not publish speculative state. |
| Execution-journal events | JSONL or ADO.NET event stream | Event envelope version 1; ADO component version 1 | Complete identity SHA-256 partitions select `execution-journal/<key>.jsonl` files or `execution-journal/<key>` ADO streams. Owner-free events contain execution records, harness checkpoints, side-effect transitions, compensation marks, and deletion tombstones. Replay preserves correlation and idempotency semantics within one authenticated scope; corrupt streams and failed appends cannot affect another scope or publish speculative state. |
| Evaluation archive | JSONL | Event envelope version 1 | Results support owner-scoped execution lookup; unsupported versions reject before directory creation or append. |
| Knowledge evaluation datasets | JSON fixture | Nao schema version 1 plus positive dataset revision and SHA-256 content hash | Owner-neutral reviewed fixtures retain typed document/chunk relevance, fact-to-evidence judgments, and prohibited targets. DTO deserialization reconstructs domain contracts and rejects unsupported schemas, malformed judgments, and content-hash mismatch. |
| Orleans session state | Orleans provider | Nao schema version 1 plus Orleans field IDs | Conversation messages retain full execution correlation; persisted missing or unsupported versions reject before state access or mutation. |
| Orleans session-directory state | Orleans provider | Nao schema version 1 plus Orleans field IDs | New state is stamped during activation; persisted missing or unsupported versions reject before state access or mutation. |

Knowledge-record persistence is not implemented and therefore has no durable format yet. Version-1 grounded audit records are storage-neutral contracts delivered to a host-supplied authenticated recorder; Nao does not yet own their encoding or backend, so they are not a durable-format inventory entry. New durable capabilities must define ownership, deletion, current schema version, rejection behavior, and a migration-guide path with their first implementation.

## Change process

1. Change the current schema directly.
2. Reject old or incompatible state before mutation.
3. Add a guide under `docs/migrations/` describing backup, external transformation or reset, validation, and rollback limits.
4. Update this inventory and affected tests.

See the [migration policy](migrations/README.md) for the required guide structure.