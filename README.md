# Nao

An F#-first foundation for building reliable, knowledge-grounded, governable AI platforms.

Nao connects models, agents, tools, memory, knowledge, distributed execution, governance, observability, and evaluation through explicit, composable contracts.

> **Current status:** Nao provides the agent, ETCLOVG harness, persistence, provider, evaluation, and Orleans runtime foundations. Production knowledge/RAG, enforced isolation, provider control-plane operations, enterprise integrations, and formal reasoning are active roadmap areas—not completed capabilities.

## Documentation

### Start here

| Document | Purpose |
|---|---|
| [Platform overview](docs/platform.md) | Vision, current status, platform architecture, principles, and project boundaries |
| [Capability inventory](docs/capabilities.md) | Source- and test-backed status, limitations, ownership, known gaps, and non-goals |
| [Getting started](docs/getting-started.md) | Restore, build, test, define an agent, use the harness, and register a workspace |
| [AI platform roadmap](docs/roadmap.md) | Prioritized milestones, trackable tasks, dependencies, and acceptance criteria |
| [Development and contributing](docs/development.md) | Repository conventions, testing, compatibility, security, and roadmap workflow |

### Architecture and capabilities

| Document | Purpose |
|---|---|
| [Architecture and ETCLOVG](docs/architecture.md) | Execution model, seven harness concerns, state ownership, boundaries, and known gaps |
| [Agents and orchestration](docs/agents-orchestration.md) | Typed agents, routers, pipelines, collaborative groups, delegation, and custom orchestrators |
| [Tools, security, and governance](docs/tools-governance.md) | Typed tools, MCP, permissions, policies, constitutions, audit, compensation, and isolation |
| [Memory and knowledge](docs/memory-knowledge.md) | Context, memory tiers, semantic/graph memory, planned knowledge ingestion, and RAG |
| [Evaluation and observability](docs/evaluation-observability.md) | Evaluators, reproducibility, release gates, traces, metrics, telemetry, and drift |
| [Providers and distributed runtime](docs/providers-runtime.md) | Provider semantics, routing direction, Orleans sessions, tenancy, workspaces, and operations |
| [Durable formats](docs/durable-formats.md) | Durable schema inventory, decode policy, and migration rules |

### Roadmap workstreams

| Workstream | Outcome |
|---|---|
| [Foundations and contracts](docs/roadmap/00-foundations.md) | Accurate capability baseline, stable contracts, package boundaries, and complete CI surface |
| [Harness, security, and governance](docs/roadmap/01-harness-security-governance.md) | Durable, isolated, fail-closed execution |
| [Knowledge and RAG](docs/roadmap/02-knowledge-rag.md) | Ingestion, indexing, hybrid retrieval, grounding, citations, and provenance |
| [Evaluation and observability](docs/roadmap/03-evaluation-observability.md) | Reproducible quality gates, standard telemetry, and production drift management |
| [Providers and distributed runtime](docs/roadmap/04-providers-runtime.md) | Model control plane and resilient multi-tenant execution |
| [Ontology and symbolic reasoning](docs/roadmap/05-ontology-logic.md) | Optional RDF/OWL, rules, constraints, and proof-producing reasoners |
| [Platform operations and developer experience](docs/roadmap/06-platform-operations-dx.md) | Deployment, recovery, administration, extension SDK, and releases |

## Quick start

Requires .NET 10.0 or later.

```bash
dotnet tool restore
dotnet paket install
dotnet build Nao.slnx
dotnet test Nao.slnx
```

See [Getting started](docs/getting-started.md) for the first typed agent and harness configuration.

## Projects

| Project | Responsibility |
|---|---|
| `Nao.Agents` | Core agent/tool contracts and ETCLOVG harness |
| `Nao.Protocols` | Typed response protocols and repair |
| `Nao.Persistence.*` | Persistence capability packages and opt-in composition |
| `Nao.Providers.*` | Hosted and local model-provider adapter packages and opt-in composition |
| `Nao.Eval` | Evaluation cases, runners, evaluators, and reports |
| `Nao.Runtime.Orleans` | Distributed sessions and workspace runtime |
| `Nao.Runtime.Orleans.Codegen` | Orleans serialization source-generation support |

## License

MIT
