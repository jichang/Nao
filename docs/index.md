# Nao — F# Agent Framework

Nao is an F# framework for building, orchestrating, and evaluating LLM-powered agents with production-grade governance, observability, and verification.

## Projects

| Project | Description |
|---------|-------------|
| [Nao.Agents](reference/nao-agents.html) | Agent framework — ETCLOVG harness, tools (verify/revert), execution journal, orchestration |
| [Nao.Providers](reference/nao-providers.html) | LLM provider implementations (Ollama, OpenAI, Anthropic, vLLM, llama.cpp) |
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
│  Agent · Tool · Prompt · ContentMeta · CompletionOptions · ILlmProvider│
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
- `ToolSchema` — Rich discovery metadata projected from a compiled `Tool` (parameters, examples, cost category)
- `IToolMiddleware` — Pre/post-processing (rate limiting, auditing, transformation)
- `ToolRouter` — Pattern-based or name-based tool selection
- `ContentMeta` — Generic content-type tag on tool outputs (text, JSON, PDF, images, etc.)
- `Tool.Verify` — Optional function to check output correctness
- `Tool.Revert` — Optional function to undo side-effects (with `RevertContext`)
- `ExecutionJournal` — Immutable log of tool executions; supports bulk revert of revertible operations

### C — Context & Memory

Tiered memory management and context compaction:

- `MemoryTier` — ShortTerm, MidTerm, LongTerm with promotion policies
- `ContextCompaction` — DropOldest, Summarize, RelevanceFilter, Hierarchical strategies
- `ConversationWindow` — LastN, TokenBudget, SummarizeAfter windowing
- `ISemanticMemory` — Embedding-based retrieval

### L — Lifecycle & Orchestration

Agent lifecycle state machine and multi-stage pipelines:

- `AgentLifecycle` — Created → Ready → Running → Suspended → Completed/Failed
- `ILifecycleHook` — OnBeforeInit, OnBeforeStep, OnCompleted, OnFailed
- `LifecyclePipeline` — Multi-stage execution with validation and `RetryPolicy`
- `Router`, `Pipeline`, `AgentGroup` — Multi-agent orchestration patterns
- `OrchestratorBase` — Abstract template that owns the run loop; subclasses supply the prompt and parser
- `IOrchestratorFactory` — DI interface to control orchestrator instantiation

#### Custom Orchestrators

`OrchestratorBase` owns the whole run loop — calling the LLM, logging the round's reasoning, tracing each step, appending the model's message, executing tools and delegations, and producing the final answer. A subclass only supplies *how to prompt* and *how to parse*. Because the base makes the LLM call, **logs and traces are captured no matter how the orchestrator is implemented** — a custom subclass cannot accidentally drop them. Hosts subclass `OrchestratorBase` or provide their own concrete implementation.

| Member | Kind | Purpose |
|--------|------|---------|
| `GenerateReasoningPrompt(conversation)` | abstract | Build the messages sent to the LLM (system prompt + running history) |
| `ParseActions(response)` | abstract | Parse LLM output into `AgentAction`s (empty = plain final answer) |
| `ValidateResponse(response)` | virtual | Return a repair error, or `None` to accept (default: accept) |
| `BuildRepairMessage(error)` | virtual | Corrective instruction sent on a repair round |
| `OnToolResult(name, input, result)` | virtual | Post-processing hook after a tool executes |
| `OnRoundComplete(round, content)` | virtual | Hook called after each reasoning round |

Users subclass `OrchestratorBase` and register an `IOrchestratorFactory` via DI to have the runtime use their custom orchestrator. For every round, regardless of subclass, the base guarantees a `ReasoningAdded` signal, an `agent.plan` trace span, and `ToolInvoked`/`ToolCompleted` (and `SubAgentInvoked`/`SubAgentCompleted`) signals with `tool.invoke` spans for each executed action.

### O — Observability & Operations

Distributed tracing, cost metrics, and resilience:

- `ITracer` — OpenTelemetry-style spans with parent/child relationships
- `IMetricsCollector` — LLM call counts, token usage, latency percentiles, cost estimation
- `RetryPolicy` — Core retry contract used by lifecycle pipelines and resilience policies
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

- `PermissionModel` — Permissive/Restrictive with per-capability grants
- `ResourceAccess` — Resource-level requests (`File`/`Web`/`ToolCall`) evaluated by the pure `ResourcePermission` engine (`Allow`/`Deny`/`Ask`, deny-by-default)
- `ToolContext` — Passed to every `Tool.Execute`; lets tools request approval dynamically and carries the session key. Tools can also declare a static `Permissions` list that `InvokeAsync` auto-requests before running
- `PermissionGate` — Process-wide host hook so the Orleans runtime can resolve permission requests without depending on a transport or application
- `Constitution` — Declarative output rules (PII detection, harm prevention, domain rules)
- `IAuditLog` — Full audit trail of all agent actions
- `PolicyEngine` — Runtime budget/rate-limit enforcement with Block/Warn/Modify actions
- `HarnessError` — Structured error DU (PermissionDenied, PolicyBlocked, NotReady, etc.)

#### Resource Permissions

The resource-permission system is the resource-level companion to the capability-level `PermissionModel`:

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
