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

Identity scopes are ordered from broad to narrow: tenant, organizational group, user, workspace, session, turn, and execution. A narrower identifier never authorizes access by itself. The authenticated host supplies a `SecurityPrincipal` containing typed tenant and original subject identity, optional impersonation evidence, optional service identity and ordered service delegations, groups, roles, claims, and provider-neutral authentication strength. Principal construction rejects non-canonical role and claim names; authentication details remain host evidence rather than prompt text. The minimal constructor asserts no impersonation, service, roles, claims, or authentication strength. Request data can select only a group held by that principal and cannot replace its tenant or subject.

User impersonation is a distinct host-minted principal state, not a delegation operation available to agents. It requires authenticated evidence, a different effective user, and a canonical non-blank reason. Resource ownership uses the effective user while authorization evidence retains the original user; therefore a direct target-user principal and an impersonated principal are not interchangeable capabilities. A service principal identifies its authenticated root service, and `AuthorizationScope.delegateService` appends services without changing tenant, original/effective user, roles, claims, groups, or authentication strength. Only the current service may delegate, service identities cannot repeat, direct-user scopes cannot introduce a service, and service delegation must finish before agent execution begins.

`AuthorizationScope` binds this principal to workspace and optional session identity plus an ordered `AgentAuthority` chain. Root execution can establish authority only on an unbound scope; each agent delegation must be issued by the current agent, use a distinct execution identity, and cannot delegate to itself. Containment requires identical ownership and authentication evidence plus prefix relationships for both service and agent lineages, so a delegated scope cannot replace its caller or elevate roles and claims. `AuthorizationIdentity` excludes impersonation reasons, services, agents, and authentication evidence, keeping durable partition keys stable and claim-free. Orleans session grains obtain the principal from a host-injected accessor, persist tenant/user/group/workspace/session ownership, and revalidate the current principal before every read or mutation. `AgentContext.Authorization` carries the full scope, with no separate mutable principal field. The harness replaces caller-supplied authorization before agent, delegated-agent, and tool execution. Worker protocol v1 carries impersonation, service, principal, and agent authority as transport metadata separately from model input and rejects missing, malformed, duplicate, or mismatched authority. Evaluation callbacks receive the scope in a mandatory `EvaluationContext`; protected storage and telemetry require it for every operation.

`Nao.Identity.Oidc` adapts a host-validated .NET `ClaimsIdentity` or single-identity `ClaimsPrincipal` into the transport-neutral principal. Hosts explicitly configure claim types, allowlisted retained claims, and authentication-strength resolution; no provider names or token formats enter `Nao.Agents`. The adapter rejects unauthenticated or multi-identity principals, missing or duplicate tenant/subject claims, ambiguous scalar claims, invalid identifiers, inconsistent impersonation, and service delegation without a unique root. It does not validate signatures, issuers, audiences, expiry, or revocation: the host authentication middleware must complete those checks before mapping. The local Assistant host binds its accessor to the operating-system user under a fixed local tenant.

Nao public contracts use distinct `TenantId`, `GroupId`, `UserId`, `ServiceId`, `WorkspaceId`, `SessionId`, `TurnId`, `ExecutionId`, `ArtifactId`, `SourceId`, `TraceId`, and `SpanId` types. Host-assigned text identifiers are non-blank, preserve case and exact content, and reject surrounding whitespace; their canonical serialization is the wrapped text. Runtime-generated identifiers use random UUIDs and serialize in lowercase hyphenated `D` format. Parsing never trims, normalizes, or substitutes an identifier. Callers must compare the typed value appropriate to the owning scope rather than compare unrelated serialized strings.

Every root execution creates an execution ID and correlation ID. Delegation creates a distinct execution ID, retains the correlation ID, records the parent execution as causation, and starts at attempt 1. A retry also creates a distinct execution ID, retains correlation, records the previous attempt as causation, and increments the attempt number. This correlation model is implemented by `CorrelationContext`. Correlation is mandatory in `EventScope` and `AgentContext`; entry points outside an existing execution create a fresh root rather than emit untraceable work. The harness replaces its caller context's correlation with the scope correlation so execution, agent, tool, and event identity cannot diverge. A session turn propagates one root through `EventScope`, the harness `ExecutionContext`, `AgentContext`, agents, tools, provider requests, conversation and turn records, audit records, execution-journal records, and publishing observability services. Working memory is keyed by the same typed execution identity. Summarization, context compaction, memory consolidation, and task grounding require caller correlation and forward it to provider requests. Evaluation runners pass the agent invocation's correlation through every evaluator and provider request, and each result retains that execution ID. Broader persistence records still require explicit correlation.

| Scope | Owner | Lifetime and closure |
|---|---|---|
| Tenant | Host identity/control plane | Host-defined; closure must revoke access and apply tenant retention/deletion policy to every narrower scope |
| Organizational group | Tenant administrator | Membership interval; removal revokes future access but does not rewrite historical attribution |
| User | Tenant identity provider | Identity lifetime; disablement revokes access while retained records follow tenant policy |
| Workspace | Host control plane | Version registration lifetime; retirement prevents new sessions while retained sessions preserve the referenced version |
| Session | Runtime under tenant/user scope | Until retention expiry or explicit destruction; closure prevents new turns |
| Turn | Session runtime | Immutable attribution for the session retention lifetime; corrections append new facts rather than changing identity |
| Execution | Harness/runtime | One bounded attempt; completion, failure, or cancellation closes it, after which operational records follow telemetry/audit policy |

`SessionDeletion` coordinates the implemented session destruction path. Its request binds the session routing key to the authenticated user/session scope before any deletion. It deletes the conversation directory, session turn records, memory, metrics, execution traces, and the execution journal under the complete authorization scope before removing the per-user directory entry and clearing Orleans state. Scope deletion stops on the first structured failure, preserving directory and runtime identity for retry. Isolation tests verify that another session is preserved. Conversation adapters still need their own end-to-end authenticated propagation.

`AuditLog` requires an authenticated `AuthorizationScope` for recording, agent and execution queries, denied counts, retention, and deletion. `AuditEntry.AgentId` identifies the acting agent and never selects ownership. File and ADO.NET adapters derive `audit/<identity-sha256>` streams from the complete claim-free identity, keep authentication evidence outside owner-free version-1 event payloads, and replay corruption only within the requested scope. Deletion appends scope-local tombstones, so privacy-driven physical erasure still requires compaction or stream replacement. The governance owner chooses retention cutoffs and legal holds; Nao does not impose a universal audit retention period.

`SemanticMemory` likewise owns deletion of all entries for one authenticated authorization scope and retention purge before a caller-supplied cutoff. Its in-memory, file, and ADO.NET implementations use the same strict cutoff and complete claim-free ownership lineage. These operations delete the embeddings stored by the current adapters; future external vector indexes must participate in the same deletion path.

`MemoryStore` requires `AuthorizationScope` for saves, reads, individual deletes, counted owner deletion, and retention purge. All three adapters isolate the complete tenant/user/group/workspace/session lineage and retain entries exactly at the cutoff. Persistent partition keys hash a fixed-order JSON identity tuple rather than sanitize or concatenate caller strings, so optional scope components and embedded delimiters remain distinct. Memory tools derive their scope from each governed invocation, and consolidation receives it explicitly. Neither entry payloads nor partition keys contain authentication claims, roles, service identity, or strength.

`TraceStore` requires an authenticated `AuthorizationScope` for saving, baseline lookup, agent filtering, execution lookup, retention, and deletion. `ExecutionTrace.AgentId` identifies the acting agent but does not select ownership. Every trace carries complete execution correlation, and LLM-based verification judges forward that correlation to their provider requests. File and ADO.NET adapters select `trace-store/<identity-sha256>` streams from the claim-free identity and keep authorization evidence outside payloads. The in-memory adapter physically removes matching traces; persistent adapters append scope-local deletion tombstones. Tombstones remove records from the authoritative reconstructed view but do not physically erase historical payloads, so privacy-driven erasure still requires compaction or stream replacement.

`TurnStore` requires `AuthorizationScope` for every operation and treats `TurnId` as the identity of one authoritative logical record within that scope. Turn payloads contain no tenant, user, group, workspace, session, role, claim, service, or authentication-strength fields. Every turn retains complete execution correlation, and `GetForExecutionAsync` reconstructs the final answer, tool calls, delegations, and published data accepted for one execution. File and ADO.NET adapters use claim-free `turn-store/<identity-sha256>` version-1 event streams with scope and retention tombstones. `FeedbackService.DeleteScopeAsync` exposes deletion to coordinated session destruction.

`ConversationStore` requires `AuthorizationScope` for append, replacement, loading, execution lookup, listing, and deletion. One claim-free `conversation/<identity-sha256>` version-1 event stream contains owner-free saved, appended, conversation-deleted, and scope-deleted facts. `LoadByExecutionAsync` reconstructs transcript messages only within the supplied scope. Publishing wrappers derive user, session, and workspace event metadata from the authenticated scope rather than a caller-selected storage key and publish only after the durable write succeeds.

`FeedbackStore` requires the same authenticated scope for writes, turn queries, enumeration, retention, and deletion. Feedback payloads retain `TurnId` correlation but contain no caller-selected owner or authentication evidence. File and ADO.NET adapters use claim-free `feedback-store/<identity-sha256>` version-1 event streams. Deletion is logical until compaction; deleting source feedback does not erase already-derived evaluation datasets governed by separate retention policy.

`EvalArchive` owns derived evaluation datasets and reports. Datasets, runs, results, and reports carry stable identity and an `AuthorizationIdentity` owner; every result also retains the `ExecutionId` of the agent invocation that produced it. Every archive operation requires the current authenticated `AuthorizationScope` and derives the complete owner lineage from it. Writes reject ownership mismatches; reports also validate that every child result matches their owner, dataset, and run. In-memory and versioned JSONL archives provide scope-isolated retrieval, counted owner deletion, and strict cutoff retention using dataset creation and report run timestamps. Authentication claims, roles, service identity, and strength are not stored in archive ownership metadata. Persistence precedes live-state mutation under a single archive-instance lock; file deletion is tombstone-based and survives replay. Independent writers to one archive file require host coordination.

`ExecutionJournal` requires an authenticated `AuthorizationScope` for records, checkpoints, side-effect transitions, replay queries, compensation marking, retention, and deletion. Execution records, checkpoints, and transitions are owner-free payloads that retain stable IDs, turn identity, complete execution correlation, and idempotency identity. File and ADO.NET adapters derive one `execution-journal/<identity-sha256>` version-1 event stream from the complete claim-free identity and replay only that scope. Record, checkpoint, side-effect, compensation, and deletion events share the stream so ordering survives restart; failed appends do not publish speculative state. Coordinated session destruction passes the full scope before removing runtime identity. Deletion is logical, so hosts must preserve compensation history while a workflow can still be reverted and compact streams when physical erasure is required.

`MetricsCollector` requires an authenticated `AuthorizationScope` for recording, aggregation, cost estimation, execution lookup, retention, and deletion. `MetricRecord` payloads are owner-free and retain complete execution correlation. Orchestrators use the active `AgentContext` authorization and correlation for provider and tool observations. File and ADO.NET adapters select `metrics/<identity-sha256>` streams from the claim-free authorization identity, replay only that partition, and never persist authentication evidence.

`Tracer` requires an authenticated `AuthorizationScope` for every operation and the active correlation when starting a root span. Every child inherits correlation from its parent, and parent lookup plus all span mutations fail closed when the span is outside the supplied scope. `GetByExecution` reconstructs the scope-local low-level span set for one typed execution ID independently of trace attributes. File and ADO.NET adapters select `tracer/<identity-sha256>` streams, replay only the requested claim-free identity partition, and keep authorization evidence outside version-1 span envelopes. Persistent mutation resolves the latest stored snapshot before applying a change, persists before publishing it, and therefore neither stale caller snapshots nor failed appends can discard or expose accumulated span state.

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

Persisted append-only events record accepted facts and are the reconstruction authority. Mutable stores, indexes, summaries, registries, and dashboards are projections or configuration views and must be rebuildable or explicitly versioned. `NaoEvent` is an in-process notification contract, not automatically a persisted event. `ConversationStore.SaveAsync` appends a replacement fact rather than rewriting durable history.

Deletion means deleting or tombstoning the authoritative record according to policy and then removing or rebuilding all derived views. Current adapters do not yet implement this lifecycle consistently; callers must not infer complete erasure from one store operation. Working-, episodic-, graph-, and tiered-memory plus metrics and execution-trace file and ADO.NET streams use versioned complete-identity partitions, owner-free payloads, and durable lifecycle tombstones; working memory adds execution identity to its partition. Their replay projections are not independent authorities.

The session lifecycle does not automatically purge audit records or separately owned evaluation archives because their governance retention may outlive a session. Authenticated turn data participates in coordinated session destruction; hosts invoke feedback deletion separately when policy permits. Metrics accept stable, correlated, timestamped `MetricRecord` values, aggregate only within the authenticated scope, support scope-bound execution reconstruction, and participate in coordinated session destruction.

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
| Timed out | The execution exceeded its harness deadline | Retry only under explicit policy with an appropriate deadline and idempotent dependencies | `ExecutionTerminalStatus.TimedOut` |

Expected failures cross public boundaries as structured values with category, diagnostic, retryability, and correlation. Agents, tools, providers, storage, and Orleans hosts use `PlatformErrorCategory`; task APIs without an error result transport the same `PlatformFailure` through `PlatformFailureException`.

## Execution flow

```text
G: Governance (permissions and policy pre-check)
  → V: Verification (readiness gates)
    → L: Lifecycle (initialize and start)
      → O: Observability (trace spans and LLM metrics)
        → E: Execution (`ExecutionEnvironment.ExecuteAsync` invokes the agent program)
      → G: Constitution (output validation)
    → L: Lifecycle (complete or fail)
  → V: Verification (trace store, regression, and judge)
→ G: Audit (record)
```

`EtclovgHarness` integrates these concerns and returns a structured result rather than relying on exceptions for expected platform outcomes.

```fsharp
match result.Status, result.Outputs.Response with
| ExecutionTerminalStatus.Succeeded, Some response -> ...
| ExecutionTerminalStatus.Denied error, _ -> ...
| ExecutionTerminalStatus.LimitExceeded limit, _ -> ...
| ExecutionTerminalStatus.Failed error, _ -> ...
| ExecutionTerminalStatus.Cancelled, _ -> ...
| ExecutionTerminalStatus.TimedOut, _ -> ...
| ExecutionTerminalStatus.Indeterminate message, _ -> ...
| ExecutionTerminalStatus.Succeeded, None -> ...
```

`ExecutionResult` groups user-visible outputs and published artifacts, resource usage,
verification evidence, and governance decisions. Its terminal status is the sole execution
outcome; callers do not reconcile separate success and error fields.

## E — Execution

Execution contracts model resource bounds and isolation intent:

- `ResourceLimits` defines duration, call, token, cost, and tool limits.
- `ExecutionContext` tracks usage and correlation.
- `SandboxConfig` selects unrestricted or restricted execution intent.
- `ExecutionEnvironment` runs work inside an execution environment.

The harness runs `SandboxIsolation.None` locally and routes `SandboxIsolation.Process` and `SandboxIsolation.Container` to distinct host-configured environments. Worker-capable agents carry a stable `WorkerAgentReference`; arbitrary F# closures cannot cross process or container boundaries. Each execution starts a fresh worker, completes a versioned JSON-line handshake, sends explicit execution/correlation fields, and accepts one structured result. Missing configuration, local-only agents, and protocol mismatches fail closed.

The current worker protocol is v1 and includes isolation, budget, secret, and identity capabilities. Its execution envelope contains a `principal` and an explicit `requirePrincipal` flag. Process and container adapters reject a missing principal before launching protected work; workers reject missing required identity, duplicate identity fields, invalid typed identifiers, malformed roles/claims, and unknown authentication strength before invoking handlers. Valid identity is reconstructed through the core principal constructor and exposed as `WorkerExecutionRequest.Principal`. The private stdin channel carries it separately from `Input`; the framework does not copy it into prompts, command-line arguments, environment variables, response frames, or captured artifacts. Handlers must preserve that separation when calling models or returning output. Explicitly unrestricted test contexts may send a null principal, but malformed supplied identity still fails closed.

**Unreleased worker contract:** `WorkerProtocol.CurrentVersion` remains 1 as the current protocol evolves. Host and worker binaries/images must implement the same current shape; matching version markers alone do not promise compatibility with earlier development builds. Unsupported versions and malformed requests fail closed. Custom worker handlers must not treat unrestricted/test mode as authenticated authority.

Process workers inherit no host environment variables; the host supplies only the explicit `SandboxConfig.EnvironmentVariables` map. A configured working directory must be fully qualified and exist before launch, and becomes the worker's current directory. This is execution state, not filesystem confinement by itself. On Linux, hosts may select `ProcessIdentityIsolation.LinuxUserNamespace` with an existing fully qualified `unshare` executable and target namespace UID/GID. The launcher creates a fresh user namespace and maps only the host caller identity to those target IDs; invalid launcher configuration and namespace setup failures fail closed. This changes the worker-visible effective identity and scopes namespace capabilities, but the mapped identity retains the caller's host filesystem DAC permissions. It is not a separate host account and must be combined with filesystem confinement for untrusted file access.

Hosts may explicitly select `ProcessFileSystemIsolation.LinuxLandlock`; the host validates and canonicalizes existing `SandboxConfig.AllowedPaths`, protocol v1 transports them to the standard worker, and the worker installs an unprivileged Landlock ruleset before invoking its handler. Trusted application and shared .NET runtime directories receive read/list/execute access, while declared files or directories receive the supported filesystem rights. If `AllowFileSystem` is false, no data paths are granted. Setup failures fail closed, and tests prove a declared file is readable while an undeclared sibling is denied. Disabled mode and non-Linux platforms do not enforce `AllowedPaths`.

The harness applies its wall-clock deadline to worker protocol IO and kills the complete worker process tree when that token is cancelled; caller cancellation remains distinguishable from an elapsed deadline. Hosts propagate graceful shutdown through the same caller token. Protocol v1 sends remaining LLM-call, token, cost, and tool-call allowances and constructs a worker-local `WorkerExecutionBudget`. Worker handlers reserve calls and report usage through that capability; exact overages return a dedicated limit frame, and reported usage merges back into the host execution budget. Calls made outside Nao's worker budget/provider/tool capabilities are not observable and therefore are not metered. When `MaxMemoryBytes` is configured, the process environment samples the worker working set and kills its process tree on excess, returning `LimitExceeded.Memory`. `MaxCpuTime` similarly samples direct-worker processor time and returns `LimitExceeded.Cpu`. Both checks are reactive and can briefly overshoot; memory is not a hard allocation quota, and CPU accounting does not include descendant processor time. `MaxOutputBytes` counts raw stdout and stderr bytes together, drains stderr concurrently, bounds stdout protocol framing before decoding, and kills the tree with `LimitExceeded.Output` on excess.

`SecretReference` is the canonical opaque identifier used by process and provider configuration;
raw strings are rejected at those contracts. `SecretValue` binds resolved material to its exact
reference and renders as `[REDACTED]`. `ProcessSecretResolver` declares references and resolves
them for one process environment after the worker completes its version handshake. A resolver
that returns material bound to a different reference fails closed. Protocol v1 carries resolved
values over the private stdin channel, and handlers receive only typed `WorkerSecrets.Resolve`;
undeclared references fail closed without invoking the host resolver. Secret values are not
placed in worker arguments, environment variables, request input, stdout, or stderr by the
framework. The host resolver receives both execution contexts and remains responsible for
authorization and vault access. Provider configurations similarly retain only
`ApiKeyReference`; direct provider construction accepts an explicitly resolved matching
`SecretValue`.

`SecretProvider` is the host-owned asynchronous resolution capability. Every resolution receives
the complete authenticated `AuthorizationScope`, exact `SecretReference`, and caller cancellation
token. Successful resolution returns non-secret evidence containing provider identity/version,
reference, immutable secret version, and time; missing, revoked, or mismatched material fails
closed. Providers return explicit `Available`, `Revoked`, or `Missing` lookup states. The core
in-memory adapter is partitioned by complete claim-free `AuthorizationIdentity` and intended for
tests or ephemeral hosts. It exposes compare-and-swap rotation and revocation: every mutation
requires the current `SecretVersion`, advances that version, and returns non-secret
`SecretMutationEvidence`. Stale updates and cross-owner mutations fail without changing state.
Revocation leaves a versioned tombstone, and a later rotation may restore the reference only by
presenting that tombstone version. `ProcessSecretResolver.fromProvider` resolves within the
authenticated worker launch boundary, after handshake; handshake failure cannot invoke the
provider. `ProviderFactory.activateAsync` validates provider availability but does not resolve
credential material. Its returned provider resolves the configured reference immediately before
each buffered or streaming outbound operation, creates an operation-local provider client, and
disposes that client when the operation finishes. The same activated provider therefore observes
rotation or revocation on its next call without restarting or disturbing unrelated sessions.
Concrete vault SDKs remain optional host/package adapters rather than dependencies of core
packages.

Every governed harness run owns an asynchronous execution-local secret-redaction scope. Successful
resolution registers the exact material in that scope without exposing it through public
contracts. Provider wrappers redact known values from buffered and streaming prompts before
transport and from chunks, results, and structured failures before returning them. Worker requests
similarly redact known values from ordinary input after resolution, and worker output is redacted
before it crosses back into the harness. Artifact construction and event publication are
redaction-enforcing boundaries. Tracer, execution-journal, trace-store, and audit persistence
implementations also redact independently before in-memory retention or durable serialization, so
bypassing event publication cannot bypass storage protection. Final harness responses and
verification evidence are sanitized before leaving the execution scope. Nested work shares the
same redactor; concurrent top-level executions remain isolated through `AsyncLocal` context.
Adversarial tests inject unique canary values through provider, process-worker, event, artifact,
error, trace, journal, audit, and file-backed persistence paths, then assert that returned,
emitted, captured, and serialized surfaces contain no raw canary material.

`ExecutionEnvironment.containerWorker` launches the same protocol worker through a Docker-compatible command-line runtime. Construction first runs a bounded structured `info` probe and requires a reachable Linux-container daemon that reports memory and process-count limit support. `detectContainerHost` returns parsed runtime, operating-system, and cgroup evidence; `tryContainerWorker` returns explicit unsupported reasons, while the compatibility constructor raises `ContainerHostUnsupportedException` with the same reasons. Every execution requires a `name@sha256:<digest>` image reference, publishes a `container.image` JSON artifact containing that immutable reference, digest, and host-supplied supply-chain metadata, and uses `run --rm --interactive` with a unique host-generated name and CID file. The launcher always selects numeric UID/GID 65532, a read-only root, all capabilities dropped, and `no-new-privileges`; Docker validation proves the effective UID, zero effective capabilities, no-new-privileges state, denied root writes, and a successful protocol-v1 exchange. Finalization unconditionally issues `rm --force --volumes` for the predeclared name and removes the CID file. This removes the container, anonymous volumes, and its runtime-managed network attachment after success, failure, cancellation, timeout, or resource-limit termination; the adapter creates no named volumes or networks. Sandbox environment variable names are forwarded with `--env` while values remain in the runtime process environment rather than argv. `ContainerNetworkPolicy.Denied` is the host-selected default and adds `--network=none` even when the sandbox permits network requests. Unrestricted access requires both sandbox permission and explicit host policy; a sandbox denial always wins.

Container host access is deny-by-default: only validated `ContainerMount` and `ContainerDevice` entries become runtime arguments. Mount sources must be existing absolute host paths, targets and device paths must be absolute container paths, target collisions and option delimiters are rejected, and access modes are explicit. `ContainerTemporaryStorage` creates size-bounded `tmpfs` storage with `noexec`, `nosuid`, `nodev`, mode 0700, and ownership assigned to UID/GID 65532. Working directories must be absolute container paths and are never validated or applied as host paths. Deterministic launch tests and a real Docker probe verify a declared read-only bind, declared read-only device, writable bounded temporary storage, and the absence of an undeclared sibling. Container runs translate `MaxMemoryBytes` to equal Docker memory and memory-swap ceilings, `MaxCpuTime` to a hard CPU-time `ulimit`, and `MaxProcessCount` to a pids-controller ceiling. The shared worker boundary continues to enforce wall-clock and combined-output limits. Launch tests derive and verify the exact arguments, and Docker inspection verifies that the daemon installed each hard control. Every container configuration supplies a host-owned `ContainerImageVerifier`; it must accept the exact digest-pinned reference and return non-empty claims before artifact publication or runtime launch. Image evidence records those claims with verifier and policy identity. Scanner or attestation tooling, trust roots, policy content, and key lifecycle remain host responsibilities. The dedicated `container-security` CI job resolves its test images to immutable repository digests and exercises non-root identity, zero capabilities, no-new-privileges, read-only root and bind access, absent undeclared host paths and Docker socket, denied outbound networking, hard memory/PID controls, and terminal cleanup on a Docker host. Its isolated worker/upstream topology permits a declared endpoint only through a fixed-forward broker while proving a live undeclared sibling and public destinations are unreachable. Docker OOM and signal state are not yet translated to more specific terminal results after an abrupt worker exit. A destination allowlist contains validated absolute HTTP(S) URIs and a host-owned `ContainerEgressBroker`. After image verification, the broker acquires a dedicated runtime network and returns non-empty enforcement claims; built-in, whitespace-containing, and option-like network names fail closed. Nao publishes `container.network` evidence with broker, policy, destination, and claim identity, attaches only the returned network, and releases the lease after all launch and terminal paths. Production proxy operation, routing, DNS controls, certificate handling, and trust roots remain host responsibilities.

Hosts may opt into output retention with `ProcessOutputCapture`. The process environment keeps independently bounded stdout and stderr buffers, applies the mandatory host-supplied redactor, bounds the redacted UTF-8 payload again, and publishes `process.stdout` and `process.stderr` text artifacts through the governed `AgentContext`. Unredacted buffers are not published. Capture limits retention and does not replace the combined execution output budget; stdout contains the worker protocol stream rather than a separate application log channel. The current worker protocol does not transport `AgentContext` callbacks, tools, permissions, or worker-created artifacts. Hard resource quotas remain [HAR-03](roadmap/01-harness-security-governance.md) work. Network denial and filesystem confinement outside explicit Linux Landlock mode remain unavailable. Process separation and user-namespace identity mapping must not be described as a general untrusted-code security sandbox.

Platform enforcement differs as follows:

| Control | Linux | Windows | macOS |
|---|---|---|---|
| Wall-clock, output, working-set, and direct-worker CPU monitoring | Managed `System.Diagnostics.Process` enforcement; CPU behavior is covered by Linux tests | Same managed implementation; not yet verified by platform-specific tests | Same managed implementation; not yet verified by platform-specific tests |
| Hard memory and aggregate process-tree CPU quotas | No cgroup integration | No job-object integration | No operating-system quota integration |
| Filesystem allowlists | Explicit Landlock mode; covered by allow/deny tests | Not implemented | Not implemented |
| Restricted identity | Explicit single-ID user-namespace mapping; mapped host DAC permissions remain unchanged | Not implemented | Not implemented |
| Network confinement | Not implemented | Not implemented | Not implemented |

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
- Host-owned, timeout-bounded interactive approval through an injected `PermissionApprovalBroker`
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

- HAR-04 container isolation controls and secured regressions are complete for supported Docker hosts; production egress-broker implementation and abrupt container-state classification remain host/runtime gaps.
- Policy modifications and confirmation flows require complete host-to-harness semantics.
- Semantic retrieval lacks a production indexed vector backend.
- Knowledge ingestion, hybrid RAG, citations, and provenance are incomplete.
- Group-directory behavior advertised by older documentation is not present in current source.
- Standard telemetry export and continuous evaluation are incomplete.

See the [complete roadmap](roadmap.md) for tasks and acceptance criteria.
