# Nao — F# Agent Framework

Nao is an F# framework for building, orchestrating, and evaluating LLM-powered agents with production-grade governance, observability, and verification.

## Projects

| Project | Description |
|---------|-------------|
| [Nao.Agents](reference/nao-agents.html) | Agent framework — ETCLOVG harness, tools (verify/revert), execution journal, orchestration |
| [Nao.Providers](reference/nao-providers.html) | LLM provider implementations (Ollama, OpenAI, DeepSeek, Kimi, Anthropic, vLLM, llama.cpp) |
| [Nao.Eval](reference/nao-eval.html) | Agent evaluation framework — test cases, evaluators, LLM judge, regression |
| [Nao.Runtime.Orleans](reference/nao-runtime-orleans.html) | Distributed runtime — multi-workspace registry, group directory, session grains |

## Architecture

The framework implements the **ETCLOVG** seven-layer taxonomy for structured agent execution:

```text
┌─────────────────────────────────────────────────────────────────────┐
│                     Nao.Runtime.Orleans                               │
│  WorkspaceRegistry · SessionGrain · GroupDirectoryGrain · Persistence  │
├─────────────────────────────────────────────────────────────────────┤
│  Compiled Workspace Registration · Sessions · Persistence               │
├─────────────────────────────────────────────────────────────────────┤
│                        Nao.Eval                                      │
│  EvalCase · IEvaluator · LlmJudge · EvalReport · Regression         │
├─────────────────────────────────────────────────────────────────────┤
│                        Nao.Agents (ETCLOVG)                          │
│ ┌───────┐ ┌──────┐ ┌─────────┐ ┌───────────┐ ┌─────────┐ ┌──────┐ │
│ │E:Exec │ │T:Tool│ │C:Context│ │L:Lifecycle│ │O:Observe│ │V:Veri│ │
│ │Sandbox│ │Proto │ │Memory   │ │Pipeline   │ │Trace    │ │Regres│ │
│ │Limits │ │Schema│ │Compact  │ │Hooks      │ │Metrics  │ │Judge │ │
│ └───────┘ └──────┘ └─────────┘ └───────────┘ └─────────┘ └──────┘ │
│ ┌────────────────────────────────────────────────────────────────┐  │
│ │G: Permission · ResourceAccess · Constitution · Audit · Policy  │  │
│ └────────────────────────────────────────────────────────────────┘  │
│ ┌────────────────────────────────────────────────────────────────┐  │
│ │               EtclovgHarness (integrates all layers)           │  │
│ └────────────────────────────────────────────────────────────────┘  │
├─────────────────────────────────────────────────────────────────────┤
│                        Nao.Agents                                   │
│  Agent · Tool · Prompt · CompletionOptions · ILlmProvider             │
└─────────────────────────────────────────────────────────────────────┘
```

## ETCLOVG Layers

### E — Execution Environment

Resource-bounded sandboxed agent execution. Enforces time limits, LLM call budgets, token caps, and cost ceilings.

- `ResourceLimits` — Budget constraints (duration, LLM calls, tokens, cost, tool calls)
- `ExecutionContext` — Mutable usage tracker with execution ID for correlation
- `IExecutionEnvironment` — Executes agents within sandbox limits

### T — Tool Interface & Protocol

MCP-inspired structured tool discovery and invocation with middleware:

- `IToolProtocol` — List, discover, invoke tools with structured results
- `ITool` — Explicit tool contract with author-supplied parameters, schemas, permissions, and invocation behavior
- `Tool.render` — Renders an `ITool` and its explicit transport contract for model prompts
- `IToolMiddleware` — Pre/post-processing (rate limiting, auditing, transformation)

### C — Context & Memory

Tiered memory management and context compaction:

- `MemoryTier` — ShortTerm, MidTerm, LongTerm with promotion policies
- `ContextCompaction` — DropOldest, Summarize, RelevanceFilter, Hierarchical strategies
- `ConversationWindow` — LastN, TokenBudget, SummarizeAfter windowing
- `MemoryAgent` — LLM specialist exposed as one tool for deliberate recall and memory management
- `MemoryTools` — Host-scoped search, stable-key update, and opt-in exact deletion operations
- `ISemanticMemory` — Embedding-based retrieval

### L — Lifecycle & Orchestration

Harness-owned lifecycle state transitions and multi-agent orchestration:

- `ILifecycleHook` — OnBeforeInit, OnBeforeStep, OnCompleted, OnFailed
- `AgentLifecycle` — Harness-integrated state transitions and lifecycle hooks
- `Router`, `Pipeline`, `AgentGroup` — Multi-agent orchestration patterns
- `OrchestratorBase` — Abstract template that owns the run loop; subclasses supply the prompt and parser
- `IOrchestratorFactory` — DI interface to control orchestrator instantiation

#### Custom Orchestrators

`OrchestratorBase` owns the whole run loop — calling the LLM, logging reasoning, tracing and measuring each actual provider call, validating and repairing responses, appending model messages, executing tools and delegations, and producing the final answer. A subclass supplies prompt generation and an optional response protocol. Metrics include repair, fallback, delegated, and agent-backed memory calls; split token counts are used only when the provider reports both values.

| Member | Kind | Purpose |
|--------|------|---------|
| `GenerateReasoningPrompt(conversation)` | abstract | Build the messages sent to the LLM (system prompt + running history) |
| `ResponseProtocol` | virtual | Optional descriptor, parser, diagnostics, and repair strategy |
| `ParseActions(response)` | virtual | Compatibility parser used when no response protocol is supplied |
| `ValidateResponse(response)` | virtual | Return a repair error, or `None` to accept (default: accept) |
| `BuildRepairMessage(error)` | virtual | Corrective instruction sent on a repair round |

Users subclass `OrchestratorBase` and register an `IOrchestratorFactory` via DI to have the runtime use their custom orchestrator. The base guarantees progress signals, planning and tool spans, LLM exchange recording, and one metric entry for every actual provider call. Use harness `ILifecycleHook` implementations for lifecycle customization.

### O — Observability & Operations

Distributed tracing, cost metrics, and resilience:

- `ITracer` — OpenTelemetry-style spans with parent/child relationships
- `IMetricsCollector` — Actual LLM call counts, provider-reported split token usage, latency percentiles, and caller-priced cost estimation
- `TokenUsage` — Explicit input/output counts present only when both values are reported; aggregate-only usage is never guessed into a split
- `IExecutionJournal` — Immutable history of tool executions and their outcomes
- `RetryPolicy` — Core retry contract used by resilience policies
- `CircuitBreaker` — Failure threshold, open duration, half-open recovery
- `FallbackStrategy` — DefaultValue, Alternative, Cached

### V — Verification & Evaluation

Pre-flight readiness, execution traces, quality judgement, and regression detection:

- `IReadinessCheck` — Validate prerequisites before execution
- `ExecutionTrace` — Full step-by-step execution history (LLM calls, tool invocations)
- `IJudge` — Automated quality judgement with criteria scores
- `Regression.detect` — Compare traces for latency, quality, cost regressions

### G — Governance & Security

Permissions, constitutional rules, audit logging, and runtime policy enforcement:

- `ResourceAccess` — Core request contract for concrete file, web, and tool access
- `PermissionRule` — Core policy contract with a typed `PermissionTarget`, decision, and scope
- `ResourcePermission` — Pure Governance evaluator with `Allow`/`Deny`/`Ask` outcomes
- `AgentContext` — Host-constructed execution context passed explicitly to agents and tools; carries session data and resource approval behavior
- `PermissionGate` — Process-wide host hook so the Orleans runtime can resolve permission requests without depending on a transport or application
- `Constitution` — Declarative output rules (PII detection, harm prevention, domain rules)
- `IAuditLog` — Full audit trail of all agent actions
- `PolicyEngine` — Runtime budget/rate-limit enforcement with Block/Warn/Modify actions
- `HarnessError` — Structured error DU (PermissionDenied, PolicyBlocked, NotReady, etc.)

#### Resource Permissions

The permission system separates Core contracts from Governance evaluation:

- **Deny-by-default & opt-in** — Enforcement is gated by a master switch in Settings (off by default). When on, file access outside the session workspace and all web access need an allow rule.
- **Interactive approval** — Hosts can route unresolved requests (`Ask`) through their own transport and approval mechanism. No client or no answer within the timeout fails closed (deny).
- **Per-session memory** — "Remember for this session" grants are recorded in the `SessionGrain`'s own Orleans-persisted state (`GrantedPermissions`) so they are never re-prompted; "global" grants persist to the cross-session `PermissionStore`; "once" persists nothing.
- **`PermissionOutcome`** — `{ Decision; RememberForSession }` threaded from broker → `PermissionGate` → grain so the session knows whether to record the grant.

## Key Concepts

### Agents are Stateless

The runtime (Orleans grains) owns all state — conversation history, memory entries, and session metadata. Customer agents and tools are compiled .NET registrations supplied by the host:

1. Grain loads persisted conversation from storage
2. Grain resolves the registered compiled agent and its tools
3. Agent processes the input and returns a response
4. Grain persists the updated conversation; agent is discarded

### Workspace Definitions

Agents and tools are registered as compiled .NET implementations through `Nao.Agents`; the runtime does not load JSON definitions or assemblies dynamically. Governance configurations remain ordinary code-defined `Constitution` values:

Customer code creates `Tool` and `IAgent` values and registers them with the host's
`WorkspaceRegistry`. Tools may call any supported .NET integration, including HTTP, MCP,
or application services, but the runtime does not interpret a JSON execution definition.

```fsharp
let workspace =
	{ WorkspaceDefinitions.Empty with
		Agents = [ orchestratorAgent; specialistAgent ]
		Tools = [ searchTool; writeTool ]
		Constitutions = [ Constitution.empty "default" ] }

let registry = WorkspaceRegistry.fromWorkspace workspace
```

### Multi-Workspace Runtime

Multiple isolated workspaces can coexist within a single Orleans silo:

- `WorkspaceRegistry` — Thread-safe registry of compiled workspace registrations
- Startup registration — Add or remove compiled workspaces through host code
- Session isolation — Each session is bound to a specific workspace key

### Group Directory

Organizational multi-tenancy for teams and enterprises:

- `GroupDirectoryGrain` — Manages members, sessions, and workspace defaults per group
- Role-based membership — Members have roles (admin, member, etc.)
- Session ownership — Track which sessions belong to which users and groups
- Default workspace — Groups can set a default workspace for new sessions

### Orchestration

The `Orchestrator` processes multi-turn interactions by:
- Parsing LLM responses into typed `AgentAction` values
- Executing tool calls and feeding results back
- Delegating to sub-agents when appropriate
- Enforcing round limits to prevent infinite loops

The `EtclovgHarness` wraps orchestration with all seven layers for production use.

## Getting Started

```bash
# Restore tools
dotnet tool restore

# Build
dotnet build

# Run tests
dotnet test

# Generate documentation
dotnet fsdocs build --output docs/output
```

## API Reference

API documentation is auto-generated from XML doc comments in the source code using [FSharp.Formatting](https://fsprojects.github.io/FSharp.Formatting/).
