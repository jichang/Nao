# Nao — AI Platform Foundation

Nao is an F#-first foundation for building reliable, knowledge-grounded, governable AI platforms.

The current implementation provides agent, ETCLOVG harness, persistence, provider, evaluation, and Orleans runtime foundations. The roadmap tracks the work required for production knowledge/RAG, enforced isolation, model control-plane operations, enterprise integrations, formal reasoning, and platform operations.

## Start here

| Guide | Description |
|---|---|
| [Platform overview](platform.html) | Vision, capability status, architecture, principles, and ownership boundaries |
| [Capability inventory](capabilities.html) | Source- and test-backed implementation status, limitations, known gaps, and non-goals |
| [Getting started](getting-started.html) | Restore, build, test, create an agent, run the harness, and register a workspace |
| [Reference examples](reference-examples.html) | Executable local agent, governed tool, grounded RAG, Orleans, replay/evaluation, and formal reasoning paths |
| [Extension and adapter SDK](extension-adapter-sdk.html) | Compiled registration, lifecycle, conformance, compatibility, secrets, and isolated dynamic-adapter admission |
| [Developer tooling](developer-tooling.html) | Machine-readable scaffolding, validation, evaluation, trace/replay inspection, diagnostics, and migration planning |
| [Tested documentation snippets](tested-snippets.html) | Compiled agent, configuration, and adapter snippets executed in CI |
| [API documentation and lifecycle](api-lifecycle.html) | Strict generated references, stability markers, versioned docs, migrations, and executable snippets |
| [Release and compatibility process](release-process.html) | Reviewed release metadata, channels, gates, artifacts, clean-consumer checks, rollback, and package yanking |
| [Integration selection and support](integration-support.html) | Selected backends, support tiers, ownership, enterprise host boundaries, and formal-reasoning admission |
| [AI platform roadmap](roadmap.html) | Release milestones, detailed checklists, dependencies, and definition of done |
| [Development and contributing](development.html) | Coding, testing, compatibility, security, documentation, and roadmap workflow |
| [Supported deployment profiles](deployment-profiles.html) | Versioned local, production, clustered, and isolated-worker configuration and guarantees |
| [Container images and Kubernetes orchestration](container-orchestration.html) | Hardened worker images, clustered Helm deployment, reliability controls, and host responsibilities |
| [Data lifecycle and disaster recovery](data-lifecycle-recovery.html) | Store inventory, RPO/RTO, backup, PITR, rebuild, deletion, migration, and restore-drill procedures |
| [Configuration and administration](configuration-administration.html) | Immutable and reloadable settings, last-known-good activation, authenticated changes, audit, and rollback |
| [Service objectives and error budgets](service-objectives.html) | User-visible indicators, profile objectives, explicit exclusions, error budgets, alerts, and exercises |
| [Supply-chain security](supply-chain-security.html) | Dependency locks, vulnerability response, licenses, native dependencies, signing, SBOMs, provenance, and verification |
| [Durable formats](durable-formats.html) | Current schema markers, decode policy, fail-fast gaps, and migration rules |
| [Architecture decisions](decisions/README.html) | Decision criteria, lifecycle, and ADR template for platform contracts |

## Architecture and capabilities

| Guide | Description |
|---|---|
| [Architecture and ETCLOVG](architecture.html) | Harness execution flow, seven concerns, state ownership, extension boundaries, and known gaps |
| [Agents and orchestration](agents-orchestration.html) | Typed agents, routers, pipelines, collaborative groups, delegation, and orchestrators |
| [Tools, security, and governance](tools-governance.html) | Typed tools, MCP, permissions, policy, constitutions, audit, compensation, and isolation |
| [Security threat model](security-threat-model.html) | R1 assets, trust boundaries, threats, abuse cases, controls, and residual host responsibilities |
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
| `Nao.Knowledge` | Source-backed knowledge, lineage, citation, and retrieval contracts |
| `Nao.Protocols` | [Response protocol APIs](reference/nao-protocols.html) |
| `Nao.Persistence.*` | Persistence infrastructure, memory, observability, feedback, and [composition APIs](reference/nao-persistence.html) |
| `Nao.Providers.*` | Independent model adapters and [composition APIs](reference/nao-providers.html) |
| `Nao.Eval` | [Evaluation APIs](reference/nao-eval.html) |
| `Nao.DeveloperTools` | [Scaffolding, validation, diagnostics, local evaluation, and authenticated operational command contracts](reference/nao-developertools.html) |
| `Nao.Extensions` | [Common adapter registration, lifecycle, compatibility, conformance, and dynamic-admission contracts](reference/nao-extensions.html) |
| `Nao.Platform` | [Deployment, configuration, recovery, migration, and service-reliability contracts](reference/nao-platform.html) |
| `Nao.Runtime.Orleans` | [Distributed runtime APIs](reference/nao-runtime-orleans.html) |

## Documentation policy

Conceptual content lives under `docs/`; the root README is a concise repository guide and table of contents. Documentation distinguishes current behavior from roadmap behavior and does not treat an interface or configuration object as proof of production security, durability, or scalability.
