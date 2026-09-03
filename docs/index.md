# Nao — AI Platform Foundation

Nao is an F#-first foundation for building reliable, knowledge-grounded, governable AI platforms.

The current implementation provides agent, ETCLOVG harness, persistence, provider, evaluation, and Orleans runtime foundations. The roadmap tracks the work required for production knowledge/RAG, enforced isolation, model control-plane operations, enterprise integrations, formal reasoning, and platform operations.

## Start here

| Guide | Description |
|---|---|
| [Platform overview](platform.html) | Vision, capability status, architecture, principles, and ownership boundaries |
| [Capability inventory](capabilities.html) | Source- and test-backed implementation status, limitations, known gaps, and non-goals |
| [Getting started](getting-started.html) | Restore, build, test, create an agent, run the harness, and register a workspace |
| [AI platform roadmap](roadmap.html) | Release milestones, detailed checklists, dependencies, and definition of done |
| [Development and contributing](development.html) | Coding, testing, compatibility, security, documentation, and roadmap workflow |

## Architecture and capabilities

| Guide | Description |
|---|---|
| [Architecture and ETCLOVG](architecture.html) | Harness execution flow, seven concerns, state ownership, extension boundaries, and known gaps |
| [Agents and orchestration](agents-orchestration.html) | Typed agents, routers, pipelines, collaborative groups, delegation, and orchestrators |
| [Tools, security, and governance](tools-governance.html) | Typed tools, MCP, permissions, policy, constitutions, audit, compensation, and isolation |
| [Memory and knowledge](memory-knowledge.html) | Context, agent memory, semantic/graph memory, knowledge lifecycle, and RAG direction |
| [Evaluation and observability](evaluation-observability.html) | Evaluators, replay, quality gates, traces, metrics, external telemetry, and drift |
| [Providers and distributed runtime](providers-runtime.html) | Provider semantics and routing, Orleans sessions, tenancy, workspaces, and cluster operations |

## Roadmap workstreams

| Workstream | Target outcome |
|---|---|
| [Foundations and contracts](roadmap/00-foundations.html) | Trustworthy baseline, stable contracts, package boundaries, and CI surface |
| [Harness, security, and governance](roadmap/01-harness-security-governance.html) | Resumable, isolated, fail-closed execution |
| [Knowledge and RAG](roadmap/02-knowledge-rag.html) | Production ingestion, retrieval, grounding, citations, and provenance |
| [Evaluation and observability](roadmap/03-evaluation-observability.html) | Reproducible quality gates and end-to-end operational telemetry |
| [Providers and distributed runtime](roadmap/04-providers-runtime.html) | Policy-based model routing and resilient multi-tenant runtime |
| [Ontology and symbolic reasoning](roadmap/05-ontology-logic.html) | Optional formal knowledge and deterministic reasoning engines |
| [Platform operations and developer experience](roadmap/06-platform-operations-dx.html) | Deployable, recoverable, administrable, extensible platform |

## API reference

| Project | Reference |
|---|---|
| `Nao.Agents` | [Agent, tool, memory, governance, observability, and harness APIs](reference/nao-agents.html) |
| `Nao.Protocols` | [Response protocol APIs](reference/nao-protocols.html) |
| `Nao.Persistence` | [Persistence and memory implementation APIs](reference/nao-persistence.html) |
| `Nao.Providers` | [Model provider APIs](reference/nao-providers.html) |
| `Nao.Eval` | [Evaluation APIs](reference/nao-eval.html) |
| `Nao.Runtime.Orleans` | [Distributed runtime APIs](reference/nao-runtime-orleans.html) |

## Documentation policy

Conceptual content lives under `docs/`; the root README is a concise repository guide and table of contents. Documentation distinguishes current behavior from roadmap behavior and does not treat an interface or configuration object as proof of production security, durability, or scalability.
