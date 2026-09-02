# Nao AI Platform Roadmap

This roadmap evolves Nao from a capable agent framework into a reliable, knowledge-grounded, governable AI platform. It is the planning source of truth for platform work. Detailed, executable checklists are split by workstream under [`roadmap/`](roadmap/).

## How to use this roadmap

- `[x]` means implemented, tested, and documented.
- `[ ]` means planned or incomplete. A partial implementation remains unchecked.
- Every task has a stable identifier such as `FND-01` or `KNO-12` for issues, commits, and release notes.
- Complete prerequisite work before dependent work unless a task explicitly permits parallel delivery.
- Check an acceptance criterion only when automated evidence exists where practical.
- Update this roadmap in the same pull request that completes or materially changes a task.
- Do not check a parent milestone until all required tasks and acceptance criteria in its detailed plan are checked.

## Product principles

- **Fail closed:** permission, identity, and isolation failures must not silently broaden access.
- **Evidence before fluency:** grounded answers must preserve citations and provenance.
- **Deterministic where possible:** LLMs interpret and orchestrate; deterministic engines enforce rules and prove conclusions.
- **One execution contract:** production, evaluation, replay, and debugging use the same harness path.
- **Explicit ownership:** knowledge, memory, conversation context, and artifacts have distinct lifecycles.
- **Composable core:** integrations live behind stable interfaces and optional packages.
- **Multi-tenant by construction:** identity and tenant boundaries flow through execution, storage, telemetry, and evaluation.
- **Operationally measurable:** quality, safety, latency, reliability, and cost are release criteria.

## Capability baseline

The following foundations already exist. Checked items describe current capabilities, not completion of the future workstreams.

- [x] Typed agent and tool contracts
- [x] Router, pipeline, group, delegation, and extensible orchestration patterns
- [x] ETCLOVG harness structure
- [x] MCP tool transport and middleware
- [x] Conversation compaction and tiered memory abstractions
- [x] Semantic-memory and graph-memory abstractions
- [x] ADO.NET and filesystem persistence implementations
- [x] Multiple hosted and local LLM providers
- [x] Orleans session and workspace runtime
- [x] Permission, policy, constitution, and audit abstractions
- [x] Tracing, metrics, execution journal, resilience, and evaluation primitives
- [ ] Production process and container isolation
- [ ] End-to-end knowledge ingestion and RAG
- [ ] Indexed vector and production graph backends
- [ ] Continuous production evaluation and drift management
- [ ] Standard telemetry exporters and operational dashboards
- [ ] Enterprise identity, tenancy, secrets, and deployment controls
- [ ] Optional RDF/OWL, rule-engine, and constraint-solver integrations

## Workstreams and order

| Order | Workstream | Outcome | Depends on |
|---:|---|---|---|
| 0 | [Foundations and contracts](roadmap/00-foundations.md) | Accurate capability baseline, stable contracts, complete CI surface | None |
| 1 | [Harness, security, and governance](roadmap/01-harness-security-governance.md) | Resumable, isolated, fail-closed execution | Foundations |
| 2 | [Knowledge and RAG](roadmap/02-knowledge-rag.md) | Production ingestion, retrieval, grounding, and provenance | Foundations; governance for production |
| 3 | [Evaluation and observability](roadmap/03-evaluation-observability.md) | Reproducible quality gates and end-to-end telemetry | Foundations; harness |
| 4 | [Providers and distributed runtime](roadmap/04-providers-runtime.md) | Policy-based model routing and resilient multi-tenant execution | Foundations; harness; observability |
| 5 | [Ontology and symbolic reasoning](roadmap/05-ontology-logic.md) | Optional formal knowledge and deterministic reasoning | Knowledge; governance; evaluation |
| 6 | [Platform operations and developer experience](roadmap/06-platform-operations-dx.md) | Deployable, operable, extensible platform | All production-critical workstreams |

Workstreams may overlap after their interface contracts are stable. Ontology and symbolic reasoning are optional capabilities and must not block general-purpose RAG delivery.

## Release milestones

### R0 — Baseline is trustworthy

- [ ] Complete all required tasks in [Foundations and contracts](roadmap/00-foundations.md).
- [ ] Every advertised capability maps to source and automated tests, or is marked partial/planned.
- [ ] The solution and CI execute the intended unit, integration, and end-to-end test surface.

### R1 — Execution is production-safe

- [ ] Complete the required scope in [Harness, security, and governance](roadmap/01-harness-security-governance.md).
- [ ] Execution can resume or replay without duplicating committed side effects.
- [ ] Untrusted tools execute with enforced process or container boundaries.
- [ ] Permissions and policy transformations are consistently enforced and audited.

### R2 — Knowledge-grounded agents

- [ ] Complete the required scope in [Knowledge and RAG](roadmap/02-knowledge-rag.md).
- [ ] Documents can be incrementally ingested, versioned, retrieved, cited, and deleted.
- [ ] Hybrid retrieval and reranking meet versioned quality, latency, and cost gates.
- [ ] Retrieval cannot cross tenant or authorization boundaries.

### R3 — Measurable and resilient platform

- [ ] Complete production-critical work in [Evaluation and observability](roadmap/03-evaluation-observability.md).
- [ ] Complete production-critical work in [Providers and distributed runtime](roadmap/04-providers-runtime.md).
- [ ] A turn is traceable across runtime, agent, provider, tool, storage, policy, and evaluation.
- [ ] Provider failures and model changes have controlled, observable behavior.

### R4 — Advanced reasoning and platform ecosystem

- [ ] Deliver selected integrations from [Ontology and symbolic reasoning](roadmap/05-ontology-logic.md).
- [ ] Complete production-critical work in [Platform operations and developer experience](roadmap/06-platform-operations-dx.md).
- [ ] Optional reasoning engines expose evidence and proof provenance through common contracts.
- [ ] Supported deployment targets have repeatable installation, upgrade, backup, and rollback procedures.

## Cross-cutting definition of done

A task is complete only when all applicable items are checked:

- [ ] Public contracts and ownership boundaries are documented.
- [ ] Unit tests cover normal, boundary, cancellation, timeout, and failure behavior.
- [ ] Integration tests cover real adapters where practical.
- [ ] Security and tenant-isolation tests cover fail-closed behavior.
- [ ] Telemetry identifies tenant, workspace, session, turn, execution, and component without leaking secrets.
- [ ] Persistence changes include migration, compatibility, backup, and deletion behavior.
- [ ] Performance has a baseline and an explicit regression threshold.
- [ ] Evaluation records model, prompt, tool, dataset, evaluator, and configuration versions.
- [ ] Examples and API documentation are updated.
- [ ] Upgrade and rollback effects are documented.

## Prioritization rule

When priorities conflict, use this order:

1. Security and tenant isolation
2. Correctness, durability, and reproducibility
3. Evaluation and observability
4. Knowledge quality and provenance
5. Reliability and performance
6. Developer experience and ecosystem breadth
7. Advanced optional reasoning

## Out of scope for the core framework

Nao should provide contracts and orchestration for these concerns, but avoid embedding every implementation in the core package:

- Vendor-specific vector, graph, identity, secret, and telemetry systems
- Document-format parsers with large dependency trees
- Full ontology reasoners, Prolog runtimes, or SMT solvers
- Application-specific user interfaces and business workflows
- Arbitrary dynamic code loading without an explicit trust and isolation model

These belong in optional adapters, host applications, or separately versioned integration packages.
