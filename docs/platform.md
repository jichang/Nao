# Nao AI Platform

Nao is an F#-first foundation for building reliable, knowledge-grounded, governable AI platforms.

## Vision

Nao provides reusable execution and intelligence infrastructure behind AI products—not only an agent loop. The target platform connects models, tools, knowledge, memory, deterministic reasoning, distributed execution, governance, observability, and continuous evaluation through explicit, composable contracts.

Agents remain an important execution abstraction, but they are one part of the platform. Applications own their user experience and business workflows; Nao owns reusable machinery for executing those workflows safely, durably, and measurably.

## Platform status

Nao currently provides the agent, harness, persistence, provider, evaluation, and distributed-runtime foundations. It is not yet a complete production AI platform.

| Platform capability | Status |
|---|---|
| Typed agents, tools, prompts, response protocols, and orchestration | Available |
| ETCLOVG harness, governance, verification, and audit contracts | Available; production hardening remains |
| Conversation, semantic, graph, and tiered memory abstractions | Available; production indexes remain |
| Hosted and local LLM provider adapters | Available; pooling and policy-based routing remain |
| Persistence and Orleans session/workspace runtime | Available; broader tenancy operations remain |
| Evaluation, traces, metrics, and regression primitives | Available; continuous evaluation remains |
| Knowledge ingestion, hybrid RAG, reranking, grounding, and citations | Planned |
| Enforced process/container isolation and enterprise security integrations | Planned |
| RDF/OWL, rule-engine, and constraint-solver integrations | Optional and planned |
| Deployment, administration, recovery, and extension ecosystem | Planned |

The detailed status and implementation order are maintained in the [AI platform roadmap](roadmap.md).

## Platform architecture

```text
┌─────────────────────────────────────────────────────────────────────┐
│ Applications and control plane                                     │
│ Product UX · Administration · Domain workflows · Human approval     │
├─────────────────────────────────────────────────────────────────────┤
│ Knowledge and reasoning                                             │
│ Ingestion · Hybrid RAG · Provenance · Graph · Ontology · Logic      │
├─────────────────────────────────────────────────────────────────────┤
│ Evaluation and operations                                           │
│ Datasets · Quality gates · Replay · Traces · Metrics · Audit         │
├─────────────────────────────────────────────────────────────────────┤
│ Distributed runtime                                                 │
│ Orleans sessions · Workspaces · Persistence · Isolation boundaries  │
├─────────────────────────────────────────────────────────────────────┤
│ ETCLOVG execution harness                                           │
│ Execution · Tools · Context · Lifecycle · Observe · Verify · Govern │
├─────────────────────────────────────────────────────────────────────┤
│ Intelligence and integration contracts                              │
│ Agents · Models · Tools · Protocols · Memory · Storage adapters      │
└─────────────────────────────────────────────────────────────────────┘
```

The current repository primarily implements the lower four layers. Knowledge, reasoning, control-plane, and production-operations capabilities are planned as optional contracts and adapters rather than vendor dependencies embedded in the core.

## Platform principles

- **Fail closed:** identity, permission, policy, and isolation failures must not silently broaden access.
- **Evidence before fluency:** grounded output preserves citations, provenance, and uncertainty.
- **One execution contract:** production, evaluation, replay, and debugging use the same harness semantics.
- **Deterministic where possible:** models interpret and orchestrate; deterministic systems validate, constrain, and prove.
- **Explicit data ownership:** knowledge, memory, conversation context, artifacts, and telemetry have distinct lifecycles.
- **Multi-tenant by construction:** identity and ownership flow through execution, storage, retrieval, and observability.
- **Composable and vendor-neutral:** optional integrations remain behind stable contracts and separate packages.
- **Quality is operational:** correctness, safety, latency, reliability, and cost are continuously evaluated.

## Project responsibilities

| Project | Responsibility |
|---|---|
| `Nao.Agents` | Core agent/tool contracts and ETCLOVG harness implementation |
| `Nao.Protocols` | Typed model-response protocols, parsing, diagnostics, and repair |
| `Nao.Persistence` | ADO.NET and filesystem persistence and memory implementations |
| `Nao.Providers` | Hosted and local model-provider adapters |
| `Nao.Eval` | Evaluation cases, datasets, evaluators, runners, and reports |
| `Nao.Runtime.Orleans` | Distributed sessions, session discovery, and workspace runtime |
| `Nao.Runtime.Orleans.Codegen` | Orleans serialization source-generation support |

The repository also contains focused unit, integration, evaluation, loader, assistant, and end-to-end test projects. The [foundations roadmap](roadmap/00-foundations.md) tracks reconciliation of the supported solution and CI test surface.

## Architecture boundaries

### Nao core

Core packages should contain stable, vendor-neutral contracts and reusable orchestration. They should not require a particular database, vector engine, identity provider, telemetry backend, ontology reasoner, or application UI.

### Optional adapters

Provider, persistence, knowledge, identity, telemetry, and reasoning integrations should be separately versioned packages behind core contracts. Applications select only the adapters they need.

### Host applications

Hosts configure identities, policies, workspaces, models, secrets, persistence, approval transports, and administration. Product-specific UI and workflows remain outside reusable Nao packages.

## Read next

- [Capability inventory](capabilities.md)
- [Architecture and ETCLOVG](architecture.md)
- [Agents and orchestration](agents-orchestration.md)
- [Tools, security, and governance](tools-governance.md)
- [Memory and knowledge](memory-knowledge.md)
- [Evaluation and observability](evaluation-observability.md)
- [Providers and distributed runtime](providers-runtime.md)
- [Roadmap](roadmap.md)
