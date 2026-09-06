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
| Functional agents and explicit contracts | **Implemented** | `Agent` in `src/Nao.Agents/src/Agent/Agent.fs`; agent tests in `tests/Nao.Agents.Tests` | Agents are immutable records of metadata and context-aware execution functions; transport schemas remain explicitly authored. | FND-03 |
| Router, pipeline, collaborative groups, and delegation | **Implemented** | `Router`, `Pipeline`, `AgentGroup`, `AgentTool`, `Orchestrator`, `OrchestratorDefinition`, and `OrchestratorRound` under `src/Nao.Agents/src/Orchestration`; orchestrator and end-to-end tests | Delegated agents and tools share the parent execution budget; durable replay remains incomplete. | HAR-01, HAR-02 |
| Collaborative agent groups | **Implemented** | `AgentGroup.create` and `AgentGroup.runAsync`; end-to-end tests | This is agent collaboration, not organizational tenant/group administration. | FND-03 |
| Organizational group directory | **Planned** | No `GroupDirectoryGrain` or `IGroupDirectoryGrain` exists; `GroupId` fields are metadata | Membership, roles, quotas, lifecycle, and authorization require runtime/control-plane implementation. | RUN-01 |
| ETCLOVG harness | **Partial** | Immutable `ExecutionRequest`, grouped `ExecutionResult`, `ExecutionTerminalStatus`, and `EtclovgHarness.runAsync`; harness unit and end-to-end tests | Request identity and terminal outcomes are explicit, but result persistence, checkpoints, and replay remain incomplete. | HAR-01, HAR-02 |
| Resource limit accounting | **Partial** | Shared `ExecutionContext` budget accounting for nested LLM and tool calls, tokens, and elapsed time; environment and harness tests | Provider cost data is not available uniformly, and in-process checks do not provide operating-system resource isolation or complete cancellation propagation. | HAR-01, HAR-03 |
| Process and container execution | **Planned** | `SandboxIsolation.Process` and `SandboxIsolation.Container` are configuration cases only | The harness currently creates local in-process execution; filesystem, environment, network, CPU, and memory isolation are not enforced. | HAR-03, HAR-04 |

## Tools, security, and governance

| Capability | Status | Source and tests | Current boundary | Roadmap |
|---|---|---|---|---|
| Typed tools and explicit schemas | **Implemented** | Functional `Tool`, `ToolCodec`, and `ToolOperation` values under `src/Nao.Agents/src/Core`; tool and permission tests | Schemas are author-supplied; no schema inference is promised. | FND-03 |
| Tool discovery, selection, and middleware | **Implemented** primitive | `ToolProtocol`, `ToolSelector`, middleware, rate limiting, and protocol-backed orchestrator tests | Invocation requires the caller's `AgentContext`. Each orchestrator may use its own protocol; selection, per-round response parsing, and execution share one discovered catalog snapshot. | HAR-01, GOV-01 |
| MCP integration | **Experimental** | MCP contracts, registry, and stdio client under `src/Nao.Agents/src/ToolProtocol`; MCP JSON/provider tests | Stdio framing and discovery are incomplete; resources are unsupported; SSE and Streamable HTTP are placeholders. | GOV-01, DX-02 |
| Resource permission evaluator | **Implemented** | `ResourcePermission` matching and precedence; resource-permission tests | This is a pure decision primitive. Authentication, persistence, expiry, revocation, and boundary enforcement remain host/runtime concerns. | GOV-01, SEC-01 |
| Interactive permission approval | **Partial**, **Application-owned** | `PermissionGate.Prompt`, `SessionGrain.resolvePermission`, and permission grant records; tool permission tests | The host owns user interaction. Current grain fallback allows when no prompt handler is installed and must be made fail-closed. | GOV-01, SEC-01 |
| Runtime policy engine | **Partial** | `PolicyEngine` and `PolicyEnforcement`; governance tests | Confirmation currently blocks rather than invoking a workflow; the harness calculates `ModifiedInput` but executes the original input. | GOV-02 |
| Constitutions and output checks | **Implemented** primitive | `Constitution.check` and harness output checks; constitution/harness tests | Complete repair, redaction, escalation, and quarantine workflows are planned. | GOV-03 |
| Audit and execution journal | **Implemented** primitive | `AuditLog`, `ExecutionJournal`, in-memory and persistent functional implementations; governance/observability tests | Caller identity and context are not consistently present in every direct tool path. | HAR-01, GOV-01 |
| Enterprise identity and delegation | **Planned**, **Application-owned** | Session/user strings exist, but no security-principal contract or identity adapter exists | Hosts authenticate users; Nao must provide transport-neutral propagation and authorization context. | SEC-01, RUN-01 |
| Secret references and providers | **Planned**, **Application-owned** | No secret-reference/provider contracts exist | Hosts or deployment systems own vaults; Nao needs safe references, resolution boundaries, rotation, and redaction. | SEC-02, PRV-05 |

## Memory and knowledge

| Capability | Status | Source and tests | Current boundary | Roadmap |
|---|---|---|---|---|
| Key/value, working, episodic, and tiered memory | **Partial** | Memory contracts in `Nao.Agents`; owner-scoped in-memory, file, and ADO implementations; generated lifecycle parity tests | Scope is caller-supplied; stores do not independently enforce tenant or user authorization. Working memory is execution-scoped; tiered retrieval is pure and access/promotion mutation is explicit. Production indexing, authorization, and coordinated host lifecycle remain incomplete. | MEM-01, RUN-01 |
| Deliberate memory tools and memory agent | **Implemented** primitive | `MemoryTools.create`, search/remember/forget tools, and `MemoryAgent`; memory tests | The host must fix owner scope and authorize deletion; an input confirmation flag is not authenticated approval. | MEM-01, GOV-01 |
| Semantic memory | **Partial** | Functional `SemanticMemory` and `EmbeddingProvider` records, simple embedding provider, in-memory/file/ADO implementations, owner/cutoff deletion parity; semantic-memory tests | File and ADO stores scan embeddings in process; no indexed vector backend, backend ACL filters, model-version isolation, or re-embedding migration exists. | KNO-05, KNO-06 |
| Graph memory | **Partial** | Functional `GraphMemory`, owner-scoped in-memory/file/ADO implementations, and generated lifecycle parity tests | Owner/cutoff deletion, relation removal, and node cascade survive replay. Relation identity is one assertion per owner/subject/predicate/object tuple; richer provenance, indexing, authorization, and parallel assertions remain incomplete. | GRA-01 |
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
| ADO.NET and filesystem persistence | **Partial** | Memory stores, event stores, conversation stores, and rich-store factories; persistence tests | Append/replay exists, but migrations, optimistic concurrency, backup/restore, idempotency, and coordinated deletion are incomplete. | FND-05, RUN-02, OPS-03 |
| Orleans sessions and session directory | **Implemented** foundation | `SessionGrain`, `SessionDirectoryGrain`, and state records; runtime tests | Grain entry points do not yet enforce a security principal or complete tenant isolation. | RUN-01, RUN-02 |
| Compiled workspace registry and switching | **Implemented** foundation | `WorkspaceDefinitions`, `WorkspaceRegistry`, versioned workspace IDs, and `SwitchWorkspaceAsync`; end-to-end tests | Registry is in memory; validation, staged rollout, rollback, retirement, and migration semantics are planned. | RUN-03 |
| Multi-silo operations and recovery | **Planned** | Orleans supplies runtime primitives, but Nao has no complete tested operating profile | Placement, overload, rolling upgrade, failure recovery, and disaster-recovery guarantees require implementation and tests. | RUN-04, OPS-01 through OPS-03 |
| Authenticated administration | **Planned**, **Application-owned** | No reusable control-plane API exists | Hosts own UI; Nao should define authenticated, versioned administrative contracts. | RUN-05, OPS-04 |

## Evaluation and observability

| Capability | Status | Source and tests | Current boundary | Roadmap |
|---|---|---|---|---|
| Deterministic and LLM-based evaluators | **Implemented** primitive | Exact, contains, regex, composite, verification, and LLM-judge evaluators in `Nao.Eval`; evaluator tests | Citation, policy, artifact, resource, retrieval, and tool-sequence evaluators are absent. | EVAL-01, EVAL-03 |
| Dataset runner and reports | **Partial** | Owner-identified `EvalDataset`, `EvalRun`, `EvalResult`, and `EvalReport`; harness-backed case execution; `EvalArchive` in-memory/file lifecycle implementations; runner and generated archive parity tests | Each case uses host-supplied harness configuration and context; per-case timeout and sequential stop-on-first-failure remain incomplete. ADO.NET archive support awaits an eval persistence-adapter boundary. | EVAL-01, EVAL-02, HAR-01 |
| Traces, metrics, journals, and resilience | **Implemented** primitive | Tracer, trace-store owner/cutoff tombstones, session-owned execution-journal deletion, owner-scoped metric records and lifecycle parity, coordinated session destruction, retries, circuit breaker, and fallback; observability, persistence, and runtime tests | Trace and metric tombstones survive file and ADO replay but do not physically erase append-only payloads. Agent-owned traces and governance-retained audit are intentionally outside ordinary session deletion. There is no standard cross-component schema or guaranteed propagation through all external boundaries. | OBS-01 |
| Telemetry privacy boundary | **Partial** | Harness tracing exists | Raw input can enter trace attributes; centralized classification, redaction, retention, and export controls are absent. | OBS-03 |
| OpenTelemetry, OTLP, Prometheus, and dashboards | **Planned** | No standard exporter or dashboard implementation exists | Backend selection and operation are deployment-owned through future adapters. | OBS-02, OBS-04 |
| Continuous evaluation and drift detection | **Planned** | Regression primitives exist, but no production sampling and promotion workflow exists | Requires consent, redaction, dataset review, release gates, and operational alerts. | EVAL-04, EVAL-05 |

## Build and test surface

| Capability | Status | Source and tests | Current boundary | Roadmap |
|---|---|---|---|---|
| Supported solution and deterministic test surface | **Implemented** | `Nao.slnx`, `scripts/validate-test-surface.py`, and `.github/workflows/ci.yml` | Seven Nao-owned test projects run by default; CI publishes per-project TRX and Cobertura artifacts grouped by category. External-service tests require a named opt-in category and CI job before admission. | FND-02 |
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

1. `SessionGrain.resolvePermission` permits access when no `PermissionGate.Prompt` handler is installed.
2. The harness executes original input instead of `PolicyResult.ModifiedInput`.
3. `EtclovgHarness` uses local in-process execution regardless of process/container isolation intent.
4. Graph relation deletion is incomplete and node deletion can leave dangling relations.
5. MCP non-stdio transports are placeholders and stdio discovery/framing are incomplete.
6. Per-case evaluation timeout and sequential stop-on-first-failure behavior are incomplete.
7. Tenant identifiers exist as data, but security-principal validation is not enforced at grain and storage boundaries.
8. Trace attributes can contain unredacted input.
10. Persistence lacks complete migration, idempotency, concurrency, backup, restore, and coordinated-deletion guarantees.
11. No production source-to-citation knowledge lifecycle exists.

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
