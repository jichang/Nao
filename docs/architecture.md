# Architecture and ETCLOVG

Nao's current execution architecture is organized around ETCLOVG: Execution, Tool Protocol, Context and Memory, Lifecycle, Observability, Verification, and Governance.

## Platform vocabulary

These terms are normative across Nao public APIs and documentation:

| Term | Definition | Owner | Lifetime |
|---|---|---|---|
| Agent | An immutable executable capability consisting of metadata and a context-aware run function. An agent does not own durable state. | Workspace registration; invoked by an orchestrator, runtime, or host | Registration version |
| Orchestrator | A bounded planning loop that asks a provider for actions, validates them, and invokes agents or tools. | Agent or host that creates it | One agent invocation unless retained by its owner |
| Harness | The outer ETCLOVG execution path that applies governance, verification, lifecycle, observability, and result handling around an agent. | Runtime or host | One execution |
| Tool | A named executable capability with explicit input/output schemas, permissions, and optional compensation. | Tool protocol supplied by a workspace or host | Registration version; each invocation belongs to one execution |
| Provider | A model completion or streaming capability. It supplies model output but does not enforce orchestration, permissions, or persistence. | Host configuration | Configuration lifetime |
| Workspace | A versioned, compiled registration of agents, tools, governance, and orchestration configuration. It is not user working memory. | Host control plane; resolved by the runtime | Until retired according to host policy |
| Session | A durable conversation boundary that selects a workspace and orders turns for a user-facing interaction. | Runtime, scoped by the host's tenant and user identity | Until retention expiry or explicit destruction |
| Turn | One ordered request/outcome pair within a session, including its correlation and status. | Session runtime | Session retention lifetime |
| Execution | One resource-bounded attempt to run the harness, including nested agent, provider, and tool activity. A retry is a distinct execution linked by correlation and causation. | Harness/runtime | Operational retention lifetime |

`AgentGroup` means collaborative agent execution. An organizational group is an identity and authorization scope owned by a host; the two meanings must not be conflated.

## Data ownership and lifetime

Identity scopes are ordered from broad to narrow: tenant, organizational group, user, workspace, session, turn, and execution. A narrower identifier never authorizes access by itself. The authenticated host supplies tenant, group, and user identity; Nao currently propagates only part of that context and does not yet provide a canonical security principal.

Nao public contracts use distinct `TenantId`, `GroupId`, `UserId`, `WorkspaceId`, `SessionId`, `TurnId`, `ExecutionId`, `ArtifactId`, `SourceId`, `TraceId`, and `SpanId` types. Host-assigned text identifiers are non-blank, preserve case and exact content, and reject surrounding whitespace; their canonical serialization is the wrapped text. Runtime-generated identifiers use random UUIDs and serialize in lowercase hyphenated `D` format. Parsing never trims, normalizes, or substitutes an identifier. Callers must compare the typed value appropriate to the owning scope rather than compare unrelated serialized strings.

Every root execution creates an execution ID and correlation ID. Delegation creates a distinct execution ID, retains the correlation ID, records the parent execution as causation, and starts at attempt 1. A retry also creates a distinct execution ID, retains correlation, records the previous attempt as causation, and increments the attempt number. This correlation model is implemented by `CorrelationContext`; propagation beyond execution, audit, and telemetry remains in progress.

| Scope | Owner | Lifetime and closure |
|---|---|---|
| Tenant | Host identity/control plane | Host-defined; closure must revoke access and apply tenant retention/deletion policy to every narrower scope |
| Organizational group | Tenant administrator | Membership interval; removal revokes future access but does not rewrite historical attribution |
| User | Tenant identity provider | Identity lifetime; disablement revokes access while retained records follow tenant policy |
| Workspace | Host control plane | Version registration lifetime; retirement prevents new sessions while retained sessions preserve the referenced version |
| Session | Runtime under tenant/user scope | Until retention expiry or explicit destruction; closure prevents new turns |
| Turn | Session runtime | Immutable attribution for the session retention lifetime; corrections append new facts rather than changing identity |
| Execution | Harness/runtime | One bounded attempt; completion, failure, or cancellation closes it, after which operational records follow telemetry/audit policy |

`SessionDeletion` coordinates the implemented session destruction path. It deletes the conversation directory, session turn records, the `session:<grain-key>` memory owner, session-owned metrics, and the execution journal before removing the per-user directory entry and clearing Orleans state. Owner deletion stops on the first structured failure, preserving directory and runtime identity for retry. Isolation tests verify that another session is preserved.

`AuditLog` includes deletion of all records for one `AgentId` owner and retention purge before a caller-supplied cutoff. In-memory, file, and ADO.NET implementations enforce the same owner isolation and reject blank owners with `PlatformErrorCategory.InvalidInput`. The governance owner chooses the cutoff according to its retention and legal-hold policy; Nao does not impose a universal audit retention period.

`SemanticMemory` likewise owns deletion of all entries for one agent and retention purge before a caller-supplied cutoff. Its in-memory, file, and ADO.NET implementations use the same strict cutoff and blank-owner validation. These operations delete the embeddings stored by the current adapters; future external vector indexes must participate in the same deletion path.

`MemoryStore` exposes counted owner deletion and retention purge before a caller-supplied cutoff. All three adapters retain entries exactly at the cutoff, reject blank owners, and isolate deletion to the requested owner.

`TraceStore` exposes the same owner and cutoff operations using `ExecutionTrace.AgentId` and `StartedAt`. The in-memory adapter physically removes matching traces; file and ADO.NET adapters append deletion tombstones that are enforced during replay. Tombstones remove records from the authoritative reconstructed view but do not physically erase historical payloads from the append-only event stream, so privacy-driven erasure still requires future compaction or stream replacement.

`TurnStore` treats `SessionId` as owner, `CreatedAt` as the retention timestamp, and `TurnId` as the identity of one authoritative logical record. ADO.NET deletion removes rows physically; JSONL deletion appends ordered typed tombstones. `FeedbackService.DeleteSessionAsync` exposes this owner operation to coordinated session destruction.

`FeedbackStore` treats `UserId` as owner because feedback is a user-provided signal that may span sessions; `SessionId` and `TurnId` remain correlation. It supports counted owner deletion and strict cutoff purge by `CreatedAt`, with physical ADO.NET deletion and ordered typed JSONL tombstones. Hosts authorize deletion and apply legal holds; deleting source feedback does not erase already-derived evaluation datasets governed by separate retention policy.

`EvalArchive` owns derived evaluation datasets and reports. Datasets, runs, results, and reports carry stable identity and explicit owner correlation; reports validate that every child result matches their owner, dataset, and run. In-memory and versioned JSONL archives provide owner-scoped retrieval, counted owner deletion, and strict cutoff retention using dataset creation and report run timestamps. File deletion is tombstone-based and survives replay.

`ExecutionJournal` treats the session workflow key as owner because compensation belongs to the workflow that caused the side effect, not to an agent definition. Every `ExecutionRecord` requires a stable ID, owner, and turn ID; orchestration rejects journal writes without that identity. In-memory, versioned-file, and indexed ADO.NET adapters physically delete matching records before a strict `ExecutedAt` cutoff. Coordinated session destruction deletes that journal before removing runtime identity; hosts must preserve compensation history while a workflow can still be reverted.

| Data | Durable authority | Projection or derived view | Trust | Retention and deletion owner |
|---|---|---|---|---|
| Conversation context | Committed session turns | Model window, compacted history, summary | Untrusted input plus model output | Session runtime applies host retention; session destruction removes the conversation records it owns |
| Working memory | Current execution state | Prompt context | Runtime-derived, not independently authoritative | Execution owner; discard at the execution boundary unless explicitly promoted |
| Episodic memory | Persisted event or memory entry with owner and provenance | Recall results | Historical evidence | Host policy for the owning tenant/user/agent scope |
| Semantic memory | Persisted text, embedding metadata, owner, and provenance | Similarity results | Derived and potentially uncertain | Host policy and storage adapter; deletion must include derived indexes |
| Knowledge | Versioned external source record with provenance | Chunks, embeddings, indexes, retrieval results | Source-backed but retrieved content remains untrusted instructions | Source owner and ingestion policy; deletion cascades through derivatives |
| Artifact | Addressable content plus lineage and producing execution | Preview or transformed representation | Depends on producer and verification state | Workflow/host policy; delete content and lineage according to retention rules |
| Audit record | Append-only accepted audit event | Query/index/report | Runtime assertion requiring authenticated attribution | Governance owner; retention and legal-hold policy may prohibit ordinary deletion |
| Trace and metric | Accepted telemetry event | Span tree, aggregate, dashboard | Runtime assertion; attributes may contain untrusted data | Observability owner; redact before export and expire by telemetry policy |

Persisted append-only events record accepted facts and are the reconstruction authority. Mutable stores, indexes, summaries, registries, and dashboards are projections or configuration views and must be rebuildable or explicitly versioned. `NaoEvent` is an in-process notification contract, not automatically a persisted event. `ConversationStore.SaveAsync` is a mutable projection boundary, while `EventStore` is append-only.

Deletion means deleting or tombstoning the authoritative record according to policy and then removing or rebuilding all derived views. Current adapters do not yet implement this lifecycle consistently; callers must not infer complete erasure from one store operation. Working-, episodic-, graph-, and tiered-memory plus metrics file and ADO.NET streams use versioned owner-scoped events and durable lifecycle tombstones; their replay projections and aggregates are not independent authorities.

The session lifecycle does not automatically purge audit records, user-owned feedback, or separately owned evaluation archives because their governance retention may outlive a session. Hosts invoke those deletion operations only when policy permits. Metrics accept stable, timestamped `MetricRecord` values, aggregate within their owner, and participate in coordinated session destruction.

## Trust levels

| Level | Meaning | Examples | Required handling |
|---|---|---|---|
| Untrusted | Externally supplied content or instructions | User input, retrieved text, remote tool output | Treat as data; validate contracts; never grant authority from content |
| Derived | Produced by runtime or model computation | Summaries, embeddings, inferred memory, model output | Preserve provenance and confidence; do not promote to authority implicitly |
| Source-backed | Tied to a versioned external source | Knowledge records and citations | Preserve source/version identity; still treat embedded instructions as untrusted |
| Runtime assertion | Emitted by controlled Nao execution | Lifecycle events, traces, metrics, audit candidates | Authenticate actor/context and redact before durable acceptance |
| Verified | Checked by a named deterministic or governed verification step | Validated artifact, accepted protocol response | Record verifier and version; verification does not grant permissions |

Trust and authorization are independent. Verified or source-backed content cannot expand an agent's permissions.

## Error categories and retryability

| Category | Meaning | Retry rule | Current mappings |
|---|---|---|---|
| Invalid input | Caller input violates a syntax, schema, or semantic contract | Retry only after changing input | `ToolFailureKind.InputContract`, response parse/validation errors |
| Permission denied | Authenticated context lacks authority or policy denies access | Do not retry unchanged; require a new grant or policy/context change | `ToolFailureKind.PermissionDenied`, `HarnessError.PermissionDenied`, `HarnessError.PolicyBlocked` |
| Not ready | A dependency or lifecycle prerequisite is unavailable | Retry only when the failed prerequisite can change | `HarnessError.NotReady`, `HarnessError.InitializationFailed` |
| Resource exhausted | A configured time, token, call, cost, or tool limit was reached | Do not retry with the same limits; resume or retry only under explicit budget policy | `HarnessError.ResourceLimitExceeded` |
| Transient dependency | A provider, transport, database, or runtime dependency failed temporarily | Retry when the operation is idempotent and bounded retry policy permits | Provider transport and 5xx failures; storage I/O failures; retryable tool execution failures |
| Permanent dependency | A dependency rejected a supported request or cannot satisfy it | Do not retry unchanged | Provider 404 and other non-transient HTTP failures; non-retryable tool dependency failures |
| Invalid output | Agent, provider, or tool output violates its declared contract or constitution | Repair or retry only under a bounded policy | `ToolFailureKind.OutputContract`, `HarnessError.ConstitutionViolation` |
| Internal failure | Unexpected implementation defect or invariant violation | Do not automatically retry unless classified by the owning boundary | `HarnessError.ExecutionFailed`; unclassified exceptions |
| Cancelled | Caller cancellation or execution shutdown | Do not retry automatically; the caller decides whether to start a new execution | Canonically classified cancellation exceptions |

Expected failures cross public boundaries as structured values with category, diagnostic, retryability, and correlation. Agents, tools, providers, storage, and Orleans hosts use `PlatformErrorCategory`; task APIs without an error result transport the same `PlatformFailure` through `PlatformFailureException`.

## Execution flow

```text
G: Governance (permissions and policy pre-check)
  → V: Verification (readiness gates)
    → L: Lifecycle (initialize and start)
      → O: Observability (trace spans and LLM metrics)
        → E: Execution (`Agent.runAsync agentContext input agent`)
      → G: Constitution (output validation)
    → L: Lifecycle (complete or fail)
  → V: Verification (trace store, regression, and judge)
→ G: Audit (record)
```

`EtclovgHarness` integrates these concerns and returns a structured result rather than relying on exceptions for expected platform outcomes.

```fsharp
match result.HarnessError with
| Some HarnessError.PermissionDenied -> ...
| Some (HarnessError.PolicyBlocked violations) -> ...
| Some (HarnessError.NotReady reasons) -> ...
| Some (HarnessError.ResourceLimitExceeded limit) -> ...
| Some (HarnessError.ConstitutionViolation ruleIds) -> ...
| None -> ...
```

## E — Execution

Execution contracts model resource bounds and isolation intent:

- `ResourceLimits` defines duration, call, token, cost, and tool limits.
- `ExecutionContext` tracks usage and correlation.
- `SandboxConfig` selects unrestricted or restricted execution intent.
- `IExecutionEnvironment` runs work inside an execution environment.

The current harness uses local in-process execution. Process and container isolation are planned in the [harness and security roadmap](roadmap/01-harness-security-governance.md). Until those tasks are complete, sandbox configuration must not be described as an operating-system security boundary.

## T — Tool protocol

The tool protocol separates discovery, contracts, middleware, and invocation:

- `Tool`, `ToolCodec`, and `ToolOperation` define executable capabilities with explicit transport and permission contracts.
- `ToolProtocol` handles listing, availability, and invocation.
- `ToolSelector` creates a deterministic, budgeted candidate set from the protocol catalog.
- `ToolMiddleware` supplies cross-cutting behavior.
- MCP transports connect external tool servers.
- Revert operations support compensation where a tool can safely undo work.

An orchestrator can capture its own `ToolProtocol`. For each planning round, discovery, selected
prompt details, response validation, repair, and execution use the same catalog snapshot. Agents see
ordinary tool names, descriptions, and schemas; transport and selection remain host infrastructure.

See [Tools, security, and governance](tools-governance.md).

## C — Context and memory

Nao separates short-lived conversation context from durable memory:

- Conversation windows bound model input.
- Context compaction can drop, summarize, filter, or organize history.
- Working, episodic, semantic, graph, and tiered memory support different recall semantics.
- Memory tools expose deliberate search, update, and deletion operations.

External source-backed knowledge and RAG remain a distinct planned subsystem. See [Memory and knowledge](memory-knowledge.md).

## L — Lifecycle and orchestration

Lifecycle hooks observe and influence harness state transitions. Orchestration composes agents through:

- Routing
- Sequential pipelines
- Collaborative groups
- Agent-as-tool delegation
- Extensible iterative orchestrators

See [Agents and orchestration](agents-orchestration.md).

## O — Observability

Current contracts capture:

- Parent/child traces and spans
- Provider-reported token usage
- Latency and caller-owned cost estimates
- Immutable tool execution journals
- Retry, circuit-breaker, and fallback behavior

Standard OpenTelemetry exporters, operational dashboards, and production drift pipelines remain planned. See [Evaluation and observability](evaluation-observability.md).

## V — Verification

Verification includes:

- Functional `ReadinessCheck` records run concurrently before execution
- Step-level execution traces
- Functional `Judge` records, including deterministic and model-based factories
- Regression comparison
- Dataset-level evaluation reports

Production and evaluation should ultimately share exactly the same execution contract and reproducible dependency versions.

## G — Governance

Governance contracts include:

- Resource permissions and deny/allow/ask decisions
- Host-owned interactive approval through `PermissionGate`
- Runtime policies that block, warn, or modify
- Output constitutions
- Audit records

Production safety also requires host identity, secret management, and enforced process/container boundaries. Contracts alone are not isolation.

## State ownership

Nao agents are intended to be stateless per call. Runtime components own durable state:

1. A session loads persisted conversation and state.
2. It resolves a compiled workspace definition.
3. The harness executes the selected agent and tools.
4. The runtime persists the committed outcome and correlation data.

This boundary improves restart behavior and horizontal execution, but durable checkpoint/replay semantics remain roadmap work.

## Extension boundaries

- Core contracts stay vendor-neutral.
- Provider and storage implementations live in adapters.
- Orleans is an optional distributed runtime, not required by basic agents.
- Host applications own authentication, administration, user interaction, and domain workflows.
- Knowledge and formal reasoning should be optional subsystems behind stable interfaces.

## Known gaps

The roadmap intentionally tracks gaps rather than hiding them:

- Process and container execution are not yet enforced.
- Policy modifications and confirmation flows require complete host-to-harness semantics.
- Semantic retrieval lacks a production indexed vector backend.
- Knowledge ingestion, hybrid RAG, citations, and provenance are incomplete.
- Group-directory behavior advertised by older documentation is not present in current source.
- Standard telemetry export and continuous evaluation are incomplete.

See the [complete roadmap](roadmap.md) for tasks and acceptance criteria.
