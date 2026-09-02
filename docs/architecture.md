# Architecture and ETCLOVG

Nao's current execution architecture is organized around ETCLOVG: Execution, Tool Protocol, Context and Memory, Lifecycle, Observability, Verification, and Governance.

## Execution flow

```text
G: Governance (permissions and policy pre-check)
  → V: Verification (readiness gates)
    → L: Lifecycle (initialize and start)
      → O: Observability (trace spans and LLM metrics)
        → E: Execution (agent.RunAsync)
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

- `ITool` and `TypedTool` define explicit transport and permission contracts.
- `IToolProtocol` handles listing, availability, and invocation.
- `IToolMiddleware` supplies cross-cutting behavior.
- MCP transports connect external tool servers.
- Revert operations support compensation where a tool can safely undo work.

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

- Readiness checks before execution
- Step-level execution traces
- Deterministic and model-based judges
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
