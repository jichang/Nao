# Capability Inventory

This inventory is the authoritative description of what Nao currently implements, what remains partial or experimental, what belongs to a host application, and what is planned. It prevents interfaces and configuration records from being mistaken for production guarantees.

## Status definitions

| Status | Meaning |
|---|---|
| **Implemented** | Usable source implementation exists and has automated test evidence for its intended current scope. |
| **Partial** | A useful implementation exists, but important correctness, lifecycle, security, scale, or integration behavior remains incomplete. |
| **Experimental** | An implementation exists for exploration or constrained use; its contract or behavior is not production-stable. |
| **Planned** | No reusable implementation currently satisfies the documented target capability. |
| **Application-owned** | The capability belongs primarily to a host or product; Nao may provide integration contracts but does not own the product workflow. |

A capability can have more than one status when, for example, a core primitive is implemented while production enforcement remains application-owned.

## Agent execution and orchestration

| Capability | Status | Source and tests | Current boundary | Roadmap |
|---|---|---|---|---|
| Functional agents and explicit contracts | **Implemented** | `Agent` and `ExecutionRuntime` in `src/Nao.Agents/src/Agent/Agent.fs`; direct-execution and agent tests in `tests/Nao.Agents.Tests` | Agents are immutable records of metadata and context-aware execution functions. `Agent.runAsync` routes through the active runtime and fails closed when a harness-required context has no dispatcher; transport schemas remain explicitly authored. | FND-03, HAR-01 |
| Router, pipeline, collaborative groups, and delegation | **Implemented** | `Router`, `Pipeline`, `AgentGroup`, `AgentTool`, `Orchestrator`, `OrchestratorDefinition`, and `OrchestratorRound` under `src/Nao.Agents/src/Orchestration`; orchestrator and end-to-end tests | Delegated agents and tools share the parent execution budget; durable replay remains incomplete. | HAR-01, HAR-02 |
| Collaborative agent groups | **Implemented** | `AgentGroup.create` and `AgentGroup.runAsync`; end-to-end tests | This is agent collaboration, not organizational tenant/group administration. | FND-03 |
| Organizational group directory | **Planned** | No `GroupDirectoryGrain` or `IGroupDirectoryGrain` exists; `GroupId` fields are metadata | Membership, roles, quotas, lifecycle, and authorization require runtime/control-plane implementation. | RUN-01 |
| ETCLOVG harness | **Partial** | Immutable `ExecutionRequest`, grouped `ExecutionResult`, `ExecutionTerminalStatus`, caller-cancellable `EtclovgHarness.runAsync`, typed durable boundary checkpoints, stable operation identities, side-effect, compensation, and quarantine transitions, and recorded provider/tool replay fixtures; harness unit, production/evaluation parity, persistence parity, and end-to-end tests | Supported execution records governed boundaries, resumes only safe incomplete attempts, and deterministically replays exact provider/tool results without live invocation. Failed executions compensate reversible calls newest-first; ambiguous external outcomes are durably quarantined, force an indeterminate result, and cannot be replayed or compensated automatically. Durable terminal-result recovery remains future work. | HAR-01, HAR-02 |
| Resource limit accounting | **Partial** | Shared `ExecutionContext` budget accounting for nested LLM and tool calls, tokens, elapsed time, and cancellation; environment and harness tests, including process-worker call, token, cost, deadline, memory, CPU, output, and caller-cancellation cases | Protocol v1 gives workers the remaining logical allowances through `WorkerExecutionBudget`, returns exact structured overages, and merges usage into the host budget. Worker code that bypasses Nao capabilities is not observable. Wall-clock deadlines cancel local work and terminate process workers, while caller cancellation remains distinct. `MaxMemoryBytes` samples worker working set and returns `LimitExceeded.Memory`; `MaxCpuTime` samples direct-worker processor time and returns `LimitExceeded.Cpu`. Both are reactive, CPU excludes descendants, and neither is a hard operating-system quota. `MaxOutputBytes` bounds combined process stdout/stderr and returns `LimitExceeded.Output`. Provider cost data is not available uniformly, and other operating-system limits remain incomplete. | HAR-01, HAR-03 |
| Process and container execution | **Partial** | `ExecutionEnvironment.processWorker`, `ExecutionEnvironment.containerWorker`, `WorkerProtocol`, `Nao.Agents.Worker`, explicit `WorkerAgentReference`, environment/harness boundary tests, and the secured `container-security` CI job | Process and Docker-compatible container isolation execute registered workers after a version handshake. Container construction probes structured daemon capabilities and requires Linux containers plus memory and PID controllers; `tryContainerWorker` returns explicit unsupported reasons. Runs require SHA-256-pinned images and mandatory host scanner or attestation verification before side effects; image evidence records verified claims with verifier and policy identity. They enforce numeric non-root identity, read-only root, all capabilities dropped, and no privilege escalation; Docker smoke validation verifies each control and protocol v1. Host paths and devices are denied unless explicitly declared through validated policies; temporary storage is bounded, hardened, and owned by the container UID. Configured memory, CPU-time, and process-count limits become Docker hard controls; the shared boundary enforces time and output. Container network access is host-denied by default independently of sandbox permission; unrestricted access requires both grants. Destination allowlists carry a host-owned broker that receives validated HTTP(S) URIs, returns a dedicated runtime network with enforcement claims, and is released after every launch path; `container.network` evidence records broker and policy identity. Runs use `--rm`, a predeclared unique name, and unconditional forced container and anonymous-volume cleanup; removal also detaches runtime-managed networking. Name-only environment forwarding and absolute container-internal working directories remain enforced. Image construction, verifier and broker implementations, trust roots, and real destination-denial validation remain host-owned. Process workers additionally support explicit Linux user namespaces and Landlock. Protocol v1 resolves only host-declared secret handles after handshake. Arbitrary local closures and unconfigured environments fail closed. Abrupt container-state classification remains incomplete. | HAR-03, HAR-04 |

Container image execution is gated by a mandatory host-owned scanner or attestation verifier.
The verifier receives the exact digest-pinned reference before side effects and returns verified
claims that are recorded with verifier and policy identity. Nao does not supply trust roots,
signing keys, vulnerability feeds, or vendor-specific verification tooling.
The dedicated `container-security` CI job runs Docker escape regressions against a dynamically
resolved immutable image digest, covering identity, capabilities, privilege escalation, root and
bind writes, undeclared host paths, Docker socket access, denied egress, resource controls, and
cleanup. It also builds isolated worker and upstream networks around a fixed-forward broker,
proving declared endpoint access while denying a live undeclared sibling and public destinations.

## Tools, security, and governance

| Capability | Status | Source and tests | Current boundary | Roadmap |
|---|---|---|---|---|
| Typed tools and explicit schemas | **Implemented** | Functional `Tool`, `ToolCodec`, and `ToolOperation` values under `src/Nao.Agents/src/Core`; tool and permission tests | Schemas are author-supplied; no schema inference is promised. | FND-03 |
| Tool discovery, selection, and middleware | **Implemented** primitive | `ToolProtocol`, `ToolSelector`, middleware, rate limiting, and protocol-backed orchestrator tests | Invocation requires the caller's `AgentContext`. Each orchestrator may use its own protocol; selection, per-round response parsing, and execution share one discovered catalog snapshot. | HAR-01, GOV-01 |
| MCP integration | **Experimental** | MCP contracts, registry, and stdio client under `src/Nao.Agents/src/ToolProtocol`; MCP JSON/provider tests | Stdio framing and discovery are incomplete; resources are unsupported; SSE and Streamable HTTP are placeholders. | GOV-01, DX-02 |
| Resource permission evaluator | **Implemented** | Typed file, web, tool, agent, memory, knowledge, model, artifact, and administration resources/actions; explicit wildcard matching; deny precedence; boundary tests | Malformed requests and unmatched protected access fail closed. Every local and MCP tool intrinsically requires invocation access; declared resources, direct and tool-based agent delegation, and built-in memory retrieval and mutation are additionally enforced. Prompt/model output cannot manufacture approval. | GOV-01, SEC-01 |
| Interactive permission approval | **Implemented**, **Application-owned** | Injected `PermissionApprovalBroker`, authenticated `PermissionApprovalRequest`, identity-bound event-sourced grants, and durable `PermissionDecisionEvidence` in audit and turn records | The timeout-bounded host broker supports operation, session, workspace, and durable grants with expiry, revocation, and session cleanup. Every decision records its exact permission-policy version and authorizing grant IDs/versions before returning. Unsupported scopes, versions, and unavailable brokers deny; caller cancellation propagates. | GOV-01, SEC-01 |
| Runtime policy engine | **Implemented**, **Application-owned** policy backends | Versioned asynchronous policies; typed lifecycle stages, hierarchical scopes, and host-owned `PolicyFacts`; built-in rate, token, cost, classification, destination, and model policies; intrinsic standard-orchestrator model checks; immutable `PolicyEvaluationEvidence`; authenticated audit persistence; successful-turn retention; injected timeout-bounded `PolicyConfirmationBroker`; and hierarchy, conflict, failure, evidence-reload, typed-fact boundary, and end-to-end stage-order tests | `PreInput`, `PreAction`, `PreSideEffect`, `PostOutput`, and `PreCommit` are enforced. Platform, tenant, workspace, agent, tool, and session policies evaluate broad-to-narrow with stable declaration order. Host-declared classification/model facts propagate through child execution; tool stages derive destination/model facts from canonical resource declarations; the standard orchestrator checks the actual provider name before budget consumption or invocation. Every matching outcome is recorded with exact policy version and input/effective-input digests without raw inputs or authorization coordinates. Missing or failed audit persistence, typed backend failure, callback timeout, non-fatal callback or transform exception, unlabeled data under a classification policy, block, and denied/unavailable/timed-out confirmation fail closed. Caller cancellation propagates. The built-in rate limiter is process-local; distributed quota state and custom policy backends remain application-owned. | GOV-02 |
| Constitutions and output checks | **Implemented** primitive | `Constitution.check` and harness output checks; constitution/harness tests | Complete repair, redaction, escalation, and quarantine workflows are planned. | GOV-03 |
| Audit and execution journal | **Implemented** primitive | Authenticated `AuditLog` and `ExecutionJournal`, in-memory and identity-partitioned event-stream implementations; governance, persistence, observability, recovery, and compensation tests | Audit entries, execution records, checkpoints, and side-effect transitions are owner-free and scope-bound. Append-only deletion is logical until compaction or stream replacement. Caller context is not consistently present in every direct tool path. | HAR-01, GOV-01, SEC-01 |
| Enterprise identity and delegation | **Implemented** boundary, **Application-owned** authentication | Typed `SecurityPrincipal`, `AuthenticationStrength`, `UserImpersonation`, `AuthorizationScope`, claim-free `AuthorizationIdentity`, ordered service and `AgentAuthority` lineages, `Nao.Identity.Oidc`, mandatory `EvaluationContext`, Orleans principal accessor enforcement, worker protocol v1, and identity/runtime/worker/evaluation tests | The host-authenticated principal retains the original user, explicitly authorized effective user, root service, ordered service delegations, tenant, groups, roles, allowlisted claims, and authentication strength. The configurable OIDC/OAuth adapter maps one already-validated .NET identity and fails closed on missing or ambiguous authority. Hosts remain responsible for token validation, issuer/audience trust, expiry, and revocation. | SEC-01, RUN-01 |
| Secret references and providers | **Partial**, **Application-owned** | Process-specific `ProcessSecretResolver` and `WorkerSecrets`; process-boundary allow/deny tests | Hosts own authorization and vault access. Process workers receive only explicitly declared handles after handshake. Typed references, provider adapters, rotation, revocation, comprehensive redaction, and canary leakage tests remain incomplete. | HAR-03, SEC-02, PRV-05 |

## Memory and knowledge

| Capability | Status | Source and tests | Current boundary | Roadmap |
|---|---|---|---|---|
| Key/value memory | **Implemented** scoped primitive | `MemoryStore`, in-memory/file/ADO adapters, scope-isolation and lifecycle tests | Every operation requires authenticated `AuthorizationScope`; complete lineage determines the storage partition without persisting principal evidence. Consolidation and Orleans session memory/deletion use the same scope. Host permission and retention policy remain required. | MEM-01, SEC-01 |
| Working memory | **Partial** | Opaque authenticated execution address; in-memory/file/ADO implementations; full-lineage/execution isolation, pure-read, lifecycle, policy mismatch, corruption and failed-write tests | Items carry no address fields. Explicit focus, decay, pinning, expiry, rendering and deletion replay under the recorded policy. Reference persistence requires one writer per stream and retains historical payloads. Resource-level policy, physical erasure, distributed coordination, and host lifecycle integration remain incomplete. | SEC-01, MEM-01, RUN-01 |
| Tiered memory | **Partial** | Authenticated-scope in-memory/file/ADO implementations; capacity, promotion, TTL, retrieval privacy, scoped replay, policy mismatch, corruption and failed-write tests | Owner-free entries cannot widen scope through promotion. Retrieval is pure; access and eviction use explicit effective times. V1 replay requires the recorded policy, and reference persistence requires one writer per stream. Resource-level policy, distributed coordination, physical erasure, and coordinated host lifecycle remain incomplete. | SEC-01, MEM-01 |
| Episodic memory | **Partial** | Authenticated-scope in-memory/file/ADO implementations; scope/query/link/deletion parity, embedding replay/privacy, corruption, provider and append failure tests | Owner-free episodes and stored embeddings replay only within the caller's full identity partition. Reference persistence requires one writer per stream and replays on each access. Resource-level policy, embedding-model version isolation, indexing, physical erasure, and coordinated host lifecycle remain incomplete. | SEC-01, MEM-01 |
| Deliberate memory tools and memory agent | **Implemented** primitive | `MemoryTools.create`, search/remember/forget tools, and `MemoryAgent`; missing-scope, model-owner substitution, and delegated harness tests | Tools derive ownership from each invocation's authenticated scope, not a captured owner callback. The host must authorize deletion; an input confirmation flag is not authenticated approval. | MEM-01, GOV-01, SEC-01 |
| Semantic memory | **Partial** | Functional `SemanticMemory` and `EmbeddingProvider` records, simple embedding provider, authenticated-scope in-memory/file/ADO implementations, scope/cutoff deletion parity; semantic-memory tests | File and ADO stores scan embeddings in process; no indexed vector backend, model-version isolation, or re-embedding migration exists. | KNO-05, KNO-06 |
| Graph memory | **Partial** | Functional `GraphMemory`, authenticated-scope in-memory/file/ADO implementations, traversal/lifecycle isolation, extraction privacy, corrupt-stream and failed-write tests | Scope/cutoff deletion, relation removal, and node cascade survive scoped replay. Owner-free payloads cannot select partitions. Reference persistence requires one writer per stream and replays on each access. Richer provenance, indexing, resource-level policy, physical erasure, and parallel assertions remain incomplete. | SEC-01, GRA-01 |
| Source-backed knowledge lifecycle | **Planned** | No source, document-version, chunk, citation, or provenance subsystem exists | Semantic and graph memory do not substitute for ingestion, versioning, retention, and source ownership. | KNO-01 through KNO-06 |
| Hybrid RAG and grounded generation | **Planned** | No lexical/vector fusion, reranker, context assembler, grounded-answer, or citation validator exists | Product-specific presentation is application-owned; reusable retrieval and evidence contracts belong in Nao. | RAG-01 through RAG-05 |
| Automated cross-session memory synthesis | **Planned** | No consent-aware background extraction pipeline exists | Requires policy, provenance, confidence, contradiction, correction, expiry, and forgetting semantics. | MEM-01 |

## Providers and protocols

| Capability | Status | Source and tests | Current boundary | Roadmap |
|---|---|---|---|---|
| Common completion and streaming capability | **Implemented** | Functional `LlmProvider` record with optional streaming and `CompletionResult`; provider tests | Capability metadata, normalized errors, and uniform cancellation semantics remain incomplete. | PRV-01, PRV-02 |
| OpenAI-compatible model adapters | **Implemented** | Functional `OpenAICompatibleProvider` and `ProviderFactory` modules; factory/provider tests | DeepSeek, Kimi, vLLM, and llama.cpp are configured variants of this protocol, not independent protocol implementations. | PRV-01, PRV-02 |
| Anthropic and Ollama adapters | **Implemented** | Functional `AnthropicProvider` and `OllamaProvider` factory modules; adapter tests | Provider conformance and capability discovery need expansion. | PRV-01, PRV-02 |
| Provider pools, routing, quotas, and control plane | **Planned** | Resilience primitives exist, but no pooled scheduler or policy-based router exists | Configuration UI and operational workflows are host/control-plane responsibilities. | PRV-03 through PRV-05 |
| Response protocols and repair | **Implemented** primitive | `ResponseProtocol`, parse errors, and value formats in `Nao.Protocols`; protocol tests | Durable protocol versioning and compatibility fixtures are absent. | FND-05 |

## Persistence and distributed runtime

| Capability | Status | Source and tests | Current boundary | Roadmap |
|---|---|---|---|---|
| ADO.NET and filesystem persistence | **Partial** | Identity-partitioned memory, telemetry, audit, execution-journal, turn, feedback, and conversation event streams; rich-store factories; persistence tests | Append/replay exists, but optimistic concurrency, backup/restore, compaction, and coordinated physical deletion are incomplete. | FND-05, RUN-02, OPS-03, SEC-01 |
| Orleans sessions and session directory | **Implemented** foundation | `SessionGrain`, `SessionDirectoryGrain`, and state records; runtime tests | Grain entry points do not yet enforce a security principal or complete tenant isolation. | RUN-01, RUN-02 |
| Compiled workspace registry and switching | **Implemented** foundation | `WorkspaceDefinitions`, `WorkspaceRegistry`, versioned workspace IDs, and `SwitchWorkspaceAsync`; end-to-end tests | Registry is in memory; validation, staged rollout, rollback, retirement, and migration semantics are planned. | RUN-03 |
| Multi-silo operations and recovery | **Planned** | Orleans supplies runtime primitives, but Nao has no complete tested operating profile | Placement, overload, rolling upgrade, failure recovery, and disaster-recovery guarantees require implementation and tests. | RUN-04, OPS-01 through OPS-03 |
| Authenticated administration | **Planned**, **Application-owned** | No reusable control-plane API exists | Hosts own UI; Nao should define authenticated, versioned administrative contracts. | RUN-05, OPS-04 |

## Evaluation and observability

| Capability | Status | Source and tests | Current boundary | Roadmap |
|---|---|---|---|---|
| Deterministic and LLM-based evaluators | **Implemented** primitive | Exact, contains, regex, composite, verification, and LLM-judge evaluators in `Nao.Eval`; evaluator tests | Citation, policy, artifact, resource, retrieval, and tool-sequence evaluators are absent. | EVAL-01, EVAL-03 |
| Dataset runner and reports | **Partial** | Owner-identified `EvalDataset`, `EvalRun`, `EvalResult`, and `EvalReport`; harness-backed case execution with per-case deadlines and sequential stop-on-first-failure; `EvalArchive` in-memory/file lifecycle implementations; runner and generated archive parity tests | Parallel stop-on-first-failure is rejected because already-running cases cannot be recalled deterministically. ADO.NET archive support awaits an eval persistence-adapter boundary. | EVAL-01, EVAL-02, HAR-01 |
| Traces, metrics, journals, and resilience | **Implemented** primitive | Authenticated live tracer, trace-store and metric lifecycle, session-owned execution-journal deletion, coordinated session destruction, retries, circuit breaker, and fallback; observability, persistence, and runtime tests | Live-span, metric, and execution-trace operations require `AuthorizationScope`; file and ADO streams are partitioned by complete claim-free identity and exclude authentication evidence. Metric and execution-trace tombstones survive replay but do not physically erase append-only payloads. Journals and governance-retained audit still use separate caller-selected contracts, and there is no standard cross-component schema or guaranteed propagation through all external boundaries. | OBS-01 |
| Telemetry privacy boundary | **Partial** | Harness tracing exists | Raw input can enter trace attributes; centralized classification, redaction, retention, and export controls are absent. | OBS-03 |
| OpenTelemetry, OTLP, Prometheus, and dashboards | **Planned** | No standard exporter or dashboard implementation exists | Backend selection and operation are deployment-owned through future adapters. | OBS-02, OBS-04 |
| Continuous evaluation and drift detection | **Planned** | Regression primitives exist, but no production sampling and promotion workflow exists | Requires consent, redaction, dataset review, release gates, and operational alerts. | EVAL-04, EVAL-05 |

## Build and test surface

| Capability | Status | Source and tests | Current boundary | Roadmap |
|---|---|---|---|---|
| Supported solution and deterministic test surface | **Implemented** | `Nao.slnx`, `scripts/validate-test-surface.py`, and `.github/workflows/ci.yml` | Eight Nao-owned test projects run by default; CI publishes per-project TRX and Cobertura artifacts grouped by category. External-service tests require a named opt-in category and CI job before admission. | FND-02 |
| Platform vocabulary, ownership, and failure taxonomy | **Implemented** | `PlatformErrorCategory`, canonical exception and HTTP mappings, coordinated `SessionDeletion`, owner-scoped durable capabilities including `EvalArchive`, backend parity/isolation tests, `docs/architecture.md`, and documentation validation | Vocabulary, ownership, trust, event authority, retention, and retry rules are defined. Durable stores expose owner-scoped lifecycle operations; session destruction coordinates session-owned cleanup with fail-fast retries. Structured category and retryability mappings are shared across agents, tools, providers, storage, and hosts. | FND-03 |
| Package and dependency architecture | **Implemented** | Independently buildable provider adapters and persistence capability packages; `scripts/validate-project-dependencies.py`; CI architecture checks | Core remains free of optional runtime and vendor dependencies. Aggregate provider and persistence packages are opt-in composition conveniences; direct consumers can reference only the adapters or capabilities they use. | FND-04 |

## Ontology and symbolic reasoning

| Capability | Status | Source and tests | Current boundary | Roadmap |
|---|---|---|---|---|
| RDF/OWL and SPARQL | **Planned** | No RDF, OWL, or SPARQL contracts or adapters exist | `GraphMemory` is a property-graph abstraction and does not imply semantic-web semantics. | ONT-01 through ONT-03 |
| Datalog/Prolog rule engines | **Planned** | No rule-engine integration exists | LLMs may formulate candidate facts and queries but must not replace deterministic rule execution. | LOG-01, LOG-02 |
| SMT and constraint solvers | **Planned** | No solver integration exists | Solvers should be isolated optional adapters with explicit `proven`, `disproven`, `unknown`, and `inconsistent` outcomes. | LOG-01, LOG-03 |

## Known correctness and production-readiness gaps

The following are current gaps, not merely future enhancements:

1. Process workers lack non-Linux identity isolation, filesystem confinement outside explicit Linux Landlock mode, network policy, and hard CPU and memory quotas. Container workers lack abrupt container-state classification and an in-repository production egress-broker implementation.
2. Graph relation deletion is incomplete and node deletion can leave dangling relations.
3. MCP non-stdio transports are placeholders and stdio discovery/framing are incomplete.
4. Tenant identifiers exist as data, but security-principal validation is not enforced at grain and storage boundaries.
5. Trace attributes can contain unredacted input when no modifying policy is configured.
6. Persistence lacks complete migration, idempotency, concurrency, backup, restore, and coordinated-deletion guarantees.
7. No production source-to-citation knowledge lifecycle exists.

Each gap maps to the roadmap and must remain described as partial until its acceptance criteria pass.

## Intentionally unsupported and non-goals

- Inferring public tool or agent schemas from arbitrary runtime objects
- Treating prompt instructions as authorization or policy enforcement
- Treating local in-process execution as a security sandbox
- Treating semantic memory as a production vector database
- Treating graph memory as RDF/OWL or as a formal reasoner
- Loading arbitrary untrusted assemblies or executable JSON definitions into the runtime process
- Embedding product-specific UI and business workflows in core packages
- Embedding every vector, graph, identity, secret, telemetry, parser, or reasoner vendor SDK in core packages
- Requiring Orleans for basic agent or tool use
- Using an LLM as the source of formal proof or deterministic policy truth

## Application-owned responsibilities

Host applications currently own:

- User authentication and transport security
- User-facing permission approval
- Provider credentials and secret storage
- Application APIs, administration UI, and business workflows
- Deployment topology and network perimeter
- Selection and configuration of persistence and providers
- Product feedback collection and consent

Nao should provide stable integration contracts without taking ownership of application-specific experiences.

## Maintenance process

This inventory is reviewed as part of every release and every pull request that changes a public capability.

1. Reference the relevant roadmap task ID in the change.
2. Update the capability row when implementation status, evidence, limitations, or ownership changes.
3. Update source and test evidence when symbols or projects move.
4. Keep a capability **Partial** until all stated production limitations in its target scope are resolved and tested.
5. Update the known-gap list when a gap is discovered, materially changed, or closed.
6. Update README and conceptual guides only after this inventory agrees with source.
7. Run documentation source-link validation and the generated documentation build before merge.
8. Review this entire inventory before publishing a stable package release.

The documentation workflow validates source links and generated pages. Product behavior remains established by implementation and tests, not by checking a documentation box.

## Related documents

- [Platform overview](platform.md)
- [Architecture and ETCLOVG](architecture.md)
- [Development and contributing](development.md)
- [AI platform roadmap](roadmap.md)
