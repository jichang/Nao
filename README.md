# Nao

A multi-agent AI framework in F# with structured orchestration, memory management, the ETCLOVG seven-layer harness architecture, pluggable tool execution, and Orleans-based distributed multi-tenant runtime.

## Overview

Nao is a framework for building composable AI agents that can reason, collaborate, and persist state. It provides structured prompt engineering, tool invocation with content-type awareness and revert capabilities, multi-agent orchestration patterns, conversation history management, semantic memory, governance, observability, and verification — all running on Microsoft Orleans for scalable distributed multi-tenant execution.

The framework implements the **ETCLOVG** taxonomy from "Agent Harness Engineering: A Survey" — seven layers that govern every agent execution:

| Layer | Concern | Key Types |
|-------|---------|-----------|
| **E** — Execution | Resource-bounded sandboxed execution | `ExecutionContext`, `ResourceLimits`, `SandboxConfig` |
| **T** — Tool Protocol | Structured tool discovery, middleware, verify/revert | `IToolProtocol`, `ToolSchema`, `IToolMiddleware`, `ExecutionJournal` |
| **C** — Context & Memory | Tiered memory, context compaction | `ITieredMemory`, `ContextCompaction`, `MemoryTier` |
| **L** — Lifecycle | State-machine lifecycle, pipeline stages | `AgentLifecycle`, `LifecyclePipeline`, `RetryPolicy` |
| **O** — Observability | Distributed tracing, metrics, resilience | `ITracer`, `IMetricsCollector`, `CircuitBreaker` |
| **V** — Verification | Readiness checks, execution traces, regression | `IReadinessCheck`, `ExecutionTrace`, `IJudge` |
| **G** — Governance | Permissions, resource access, constitution, audit, policies | `PermissionModel`, `ResourceAccess`, `ToolContext`, `Constitution`, `PolicyEngine` |

## Features

- **ETCLOVG Harness** — Seven-layer execution pipeline with resource bounds, governance, observability, and verification
- **Multi-Agent Orchestration** — Router, Pipeline, and AgentGroup patterns for composing agents
- **Extensible Orchestrator** — Abstract base class with virtual members (`TryParseAction`, `BuildSystemPrompt`) for custom behavior via inheritance and DI
- **Conversation Memory** — Sliding window, token-budget, summarization, and tiered memory strategies
- **Semantic Memory** — Embedding-based retrieval for long-term agent knowledge
- **Persistent State** — Orleans grain persistence for conversation history and memories across sessions
- **Structured Prompts** — Type-safe prompt engineering with roles, constraints, examples, and output formats
- **Tool Protocol** — MCP-inspired tool discovery with middleware, rate limiting, and schemas
- **Content Metadata** — Generic `ContentMeta` type lets tools/agents declare output types (JSON, PDF, images, etc.)
- **Tool Verify & Revert** — Tools can declare verify (check correctness) and revert (undo side-effects) capabilities
- **Execution Journal** — Immutable log of all tool executions; supports bulk revert of revertible operations
- **Pluggable Tool Integrations** — Customer-defined .NET tools can call application services, HTTP APIs, MCP, or other integrations
- **Governance** — Constitution rules, permission models, audit logging, and runtime policy enforcement
- **Resource Permissions** — Deny-by-default file/web access with interactive, per-session approval prompts; tools declare the permissions they need and can request access dynamically through a `ToolContext`, with grants remembered per session or globally
- **Observability** — Distributed tracing (OpenTelemetry-style), cost metrics, circuit breakers, retries
- **Verification** — Readiness gates, execution trace capture, LLM judges, regression detection
- **Evaluation** — Test case framework with multiple evaluators, LLM judges, and dataset-level reports
- **Multi-Provider Support** — Pluggable LLM backends (OpenAI, Anthropic, Ollama, vLLM, llama.cpp)
- **Compiled Workspace Registration** — Customer-defined .NET agents and tools are registered explicitly through `WorkspaceRegistry`
- **Multi-Workspace Runtime** — Multiple isolated compiled workspaces within a single Orleans silo
- **Group Directory** — Organizational multi-tenancy: groups own sessions, members, and default workspaces
- **F# First** — Immutable records, discriminated unions, and functional composition throughout

## Project Structure

```
Nao.slnx
├── src/
│   ├── Nao.Agents/              # Agent framework (core types + ETCLOVG architecture)
│   │   ├── Llm/                 # Message, Role, ContentMeta, ILlmProvider, completion types
│   │   ├── Core/                # IAgent, AgentId, Tool (verify/revert), AgentAction, RetryPolicy
│   │   ├── Prompts/             # Prompt, PromptExample, OutputFormat
│   │   ├── Messaging/           # AgentMessage for inter-agent communication
│   │   ├── Logging/             # LogLevel, LogEntry, AgentLogger
│   │   ├── Environment/         # [E] ResourceLimits, SandboxConfig, ExecutionContext
│   │   ├── ToolProtocol/        # [T] ToolSchema, IToolProtocol, ToolRouter, ExecutionJournal
│   │   ├── Memory/              # [C] ConversationWindow, MemoryStore, SemanticMemory, ContextCompaction
│   │   ├── Lifecycle/           # [L] AgentLifecycle, LifecyclePipeline
│   │   ├── Orchestration/       # [L] Router, Pipeline, AgentGroup, Orchestrator
│   │   ├── Observability/       # [O] Trace, Metrics, Resilience (CircuitBreaker)
│   │   ├── Verification/        # [V] Verification, Regression
│   │   ├── Governance/          # [G] Permission, Constitution, AuditLog, PolicyEngine
│   │   └── Harness/             # EtclovgHarness (integrates all layers)
│   ├── Nao.Eval/               # Evaluation framework: test cases, evaluators, LLM judge
│   ├── Nao.Persistence/         # Persistence and memory store implementations
│   ├── Nao.Providers/          # LLM provider implementations
│   ├── Nao.Runtime.Orleans/    # Distributed runtime (grains, workspaces, groups)
│   │   ├── Workspace/           # WorkspaceRegistry (multi-tenant workspace isolation)
│   │   └── Grains/              # SessionGrain, SessionDirectory, GroupDirectory
│   └── Nao.Runtime.Orleans.Codegen/ # Orleans source-generation support
└── tests/
    ├── Nao.Agents.Tests/        # Unit tests for all ETCLOVG layers
    ├── Nao.Eval.Tests/
    ├── Nao.Persistence.Tests/
    ├── Nao.Providers.Tests/
    └── Nao.Runtime.Orleans.Tests/
```

## Prerequisites

- .NET 10.0+
- [Paket](https://fsprojects.github.io/Paket/) (installed as a local tool)

## Getting Started

```bash
# Restore tools
dotnet tool restore

# Install dependencies
dotnet paket install

# Build
dotnet build Nao.slnx

# Run tests
dotnet test Nao.slnx

```

## Architecture

### ETCLOVG Harness

The `EtclovgHarness` integrates all seven layers into a unified execution pipeline. Every agent execution flows through:

```
G: Governance (permissions + policy pre-check)
  → V: Verification (readiness gates)
    → L: Lifecycle (initialize + start)
      → O: Observability (trace spans + metrics)
        → E: Execution (sandboxed agent.RunAsync)
      → G: Constitution (output validation)
    → L: Lifecycle (complete)
  → V: Verification (trace store + regression + judge)
→ G: Audit (record)
```

```fsharp
let config =
    { EtclovgConfig.Default with
        Execution = SandboxConfig.Restricted (ResourceLimits.Constrained 60 50 100000)
        ToolProtocol = Some (ToolProtocol.fromTools myTools)
        Tracer = Some (Tracer.inMemory ())
        Metrics = Some (MetricsCollector.inMemory ())
        Constitution = Some (Constitution.empty "safety" |> Constitution.addRule Constitution.noPrivateDataRule)
        Permissions = Some (PermissionModel.Permissive agentId)
        PolicyEngine = Some (PolicyEngine.create [ PolicyEngine.costBudgetPolicy 10.0m ])
        ReadinessChecks = [ myReadinessCheck ]
        TraceStore = Some traceStore
        AuditLog = Some (AuditLog.inMemory ())
        Lifecycle = [ myHook ] }

let! result = EtclovgHarness.runAsync config agent "What is the stock price?"
// result.Success, result.Response, result.Metrics, result.Trace, result.HarnessError, ...
```

Structured errors via `HarnessError` DU:
```fsharp
match result.HarnessError with
| Some HarnessError.PermissionDenied -> ...
| Some (HarnessError.PolicyBlocked violations) -> ...
| Some (HarnessError.NotReady reasons) -> ...
| Some (HarnessError.ResourceLimitExceeded limit) -> ...
| Some (HarnessError.ConstitutionViolation ruleIds) -> ...
| None -> // success
```

### Agent Model

Every agent implements `IAgent`:

```fsharp
type IAgent =
    abstract member Id: AgentId
    abstract member RunAsync: string -> Task<string>
    abstract member HandleMessageAsync: AgentMessage -> Task<AgentMessage option>
    abstract member State: AgentState
```

Agents can invoke tools, delegate to sub-agents, or respond directly:

```fsharp
type AgentAction =
    | Respond of string
    | InvokeTool of toolName: string * input: string
    | DelegateToAgent of agentName: string * input: string
    | Think of string
```

### Orchestration Patterns

**Router** — A central agent decides which specialist handles the request:

```fsharp
let router = Router.create [ weatherAgent; mathAgent ] (ByPrompt orchestrator)
let result = Router.routeAsync "What's the weather?" router
```

Routing strategies: `ByName`, `ByPrompt` (LLM-decided), `RoundRobin`, `Custom`.

**Pipeline** — Sequential processing through multiple agents:

```fsharp
let pipeline = Pipeline.create [ fetcher; summarizer; formatter ]
let result = Pipeline.runAsync input pipeline
```

**AgentGroup** — Collaborative multi-agent conversation with termination conditions:

```fsharp
let group = AgentGroup.create [ analyst; critic ] (MaxRounds 5)
let history = AgentGroup.runAsync "Analyze this data" group
```

### Custom Orchestrators

`OrchestratorBase` is an abstract template: it owns the run loop — calling the LLM, logging the round's reasoning, tracing each step, appending the model's message, executing tools and delegations, and producing the final answer. A concrete orchestrator only fills in *how to prompt* and *how to parse*. Because the base makes the LLM call, **logs and traces are captured no matter how you implement your orchestrator** — a custom subclass cannot accidentally drop them.

The framework provides `OrchestratorBase` as an extensible execution template. Hosts supply the prompt format, action parser, agents, and tools as compiled .NET registrations; the runtime does not load code or definitions dynamically.

```fsharp
type MyOrchestrator(config: OrchestratorConfig) =
    inherit OrchestratorBase(config)

    // Build the messages sent to the LLM. The base passes the running conversation
    // (user input, the model's own prior messages, and tool/agent results), so you can
    // prepend your system prompt and inject anything you need.
    override this.GenerateReasoningPrompt(conversation) =
        task {
            let system = { Role = System; Content = "You are a domain agent. Use <tool> tags." }
            return system :: conversation
        }

    // Parse the LLM's raw response into actions. Empty list = plain final answer.
    override _.ParseActions(response) =
        if response.Contains("<tool>") then [ InvokeTool ("myTool", response) ] else []

    // Optional: flag an invalid response so the base asks the model to repair it.
    override _.ValidateResponse(response) =
        if response.Contains("</tool>") || not (response.Contains("<tool>")) then None
        else Some "unterminated <tool> tag"

    override _.OnToolResult(toolName, input, result) =
        printfn "Tool %s returned: %s" toolName result

    override _.OnRoundComplete(round, content) =
        printfn "Round %d complete" round
```

Register a custom factory via DI to have the runtime use your subclass:

```fsharp
type MyOrchestratorFactory() =
    interface IOrchestratorFactory with
        member _.Create(config) = MyOrchestrator(config) :> IAgent
```

Members on `OrchestratorBase`:

| Member | Kind | Purpose |
|--------|------|---------|
| `GenerateReasoningPrompt(conversation)` | abstract | Build the messages sent to the LLM (system prompt + history). |
| `ParseActions(response)` | abstract | Parse the LLM response into tool/agent actions. |
| `ValidateResponse(response)` | virtual | Return a repair error, or `None` to accept (default: accept). |
| `BuildRepairMessage(error)` | virtual | Corrective instruction sent on a repair round. |
| `TryHandleDelegationAsync(name, input)` | virtual | Intercept delegation (e.g. hand off to a background task). |
| `OnToolResult(name, input, result)` | virtual | Hook after tool execution. |
| `OnRoundComplete(round, content)` | virtual | Hook after each reasoning round. |

The base guarantees, for every round, regardless of subclass: a `ReasoningAdded` progress signal, an `agent.plan` trace span, and `ToolInvoked`/`ToolCompleted` (and `SubAgentInvoked`/`SubAgentCompleted`) signals plus `tool.invoke` spans for each action it executes.


### Memory Management

**Conversation Windowing** — Prevent token overflow:

```fsharp
type WindowStrategy =
    | LastN of int                    // Keep last N messages
    | TokenBudget of maxTokens: int  // Fit within token limit
    | SummarizeAfter of threshold: int // Summarize old messages
```

**Summarization** — LLM-powered condensation of older messages:

```fsharp
let config = SummarizationConfig.Default provider
let trimmed = Summarizer.applyAsync config conversation
```

**Key-Value Memory** — Structured fact storage per agent:

```fsharp
let store = InMemoryStore() :> IMemoryStore
store.SaveAsync agentId { Key = "user-name"; Value = "Alice"; ... }
store.RecallAsync agentId "user"
```

**Semantic Memory** — Embedding-based similarity retrieval:

```fsharp
let memory = InMemorySemanticMemory(embeddingProvider) :> ISemanticMemory
memory.StoreAsync agentId "fact-1" "The capital of France is Paris"
memory.RetrieveAsync agentId "What's the French capital?" topK=3
```

### Tool Protocol (T)

MCP-inspired tool discovery with middleware:

```fsharp
// Create protocol with rate limiting
let protocol =
    ToolProtocol.fromTools myTools
    |> ToolProtocol.withMiddleware (ToolProtocol.rateLimitMiddleware 100)

// Discovery
let! schemas = protocol.ListTools()
let! available = protocol.IsAvailable "get_weather"

// Invocation with structured result
let! result = protocol.InvokeAsync "get_weather" "London"
// result.Success, result.Output, result.DurationMs, result.Error
```

### Content Metadata

Tools and agents declare their output type via `ContentMeta`:

```fsharp
let meta = ContentMeta.Json
let custom = ContentMeta.WithMeta "image/png" [ "width", "1024"; "height", "768" ]
```

### Tool Verify & Revert

Tools can optionally verify correctness and undo side-effects:

```fsharp
let tool =
    { Tool.Create("deploy", "Deploy to staging", fun input -> task { ... }) with
        Verify = Some (fun input output -> task {
            // Check the deployment was successful
            return Ok ()
        })
        Revert = Some (fun ctx -> task {
            // Rollback the deployment
            return Ok ()
        }) }
```

### Execution Journal

Immutable audit log of all tool executions; enables bulk revert:

```fsharp
let journal = InMemoryExecutionJournal() :> IExecutionJournal

// Revert all revertible operations
let! failures = ExecutionJournal.revertAllAsync journal tools
```

### Governance (G)

**Permission Model** — Control which tools/capabilities agents can access:

```fsharp
let perms =
    PermissionModel.Permissive agentId
    |> PermissionModel.grant "tool:search" PermissionLevel.Allow
    |> PermissionModel.grant "tool:delete" PermissionLevel.Deny
```

**Constitution** — Rules that agent outputs must satisfy:

```fsharp
let constitution =
    Constitution.empty "safety"
    |> Constitution.addRule Constitution.noPrivateDataRule
    |> Constitution.addRule Constitution.noHarmRule
let result = Constitution.check constitution agentOutput
// result.Passed, result.Violations, hasHardViolations
```

**Policy Engine** — Budget enforcement, rate limiting, content policies:

```fsharp
let engine = PolicyEngine.create [
    PolicyEngine.costBudgetPolicy 5.0m
    PolicyEngine.rateLimitPolicy "tool_call" 60
]
let result = engine.Evaluate(PolicyContext.FromExecutionContext agentId "execute" input ctx)
```

**Resource Permissions** — Fine-grained, *resource-level* approval that complements the capability-level `PermissionModel`. Where `PermissionModel` asks "may this agent use tool X?", `ResourceAccess` asks "may this run touch THIS path or THIS url?". Access is **deny-by-default** (opt-in via Settings) and unresolved requests prompt the user live.

```fsharp
// A sensitive action + the specific resource it targets
type ResourceAccess =
    | File of operation: string * path: string   // "read"/"write"/"delete"/"list"
    | Web of operation: string * url: string      // HTTP method or "fetch"
    | ToolCall of toolName: string
```

The pure `ResourcePermission` engine evaluates an access against granted rules with `Deny > Allow > Ask` precedence (no IO — the testable core):

```fsharp
let decision = ResourcePermission.evaluateWith PermissionDecision.Deny rules access
// PermissionDecision.Allow | Deny | Ask
```

Tools are permission-aware through a `ToolContext` passed to `Execute`. A tool can declare the static `Permissions` it needs (auto-requested before each run) and/or request access dynamically mid-execution once it knows what resource its input targets:

```fsharp
// Declared up-front: auto-requested by InvokeAsync before Execute runs
let fetcher =
    Tool.Create("fetch", "Download a page",
        [ ResourceAccess.Web("GET", "https://example.com") ],
        fun ctx input -> task { ... })

// Or requested dynamically from inside Execute
let writer =
    Tool.Create("save", "Write a file", [],
        fun ctx input -> task {
            let! ok = ctx.RequestPermission (ResourceAccess.File("write", path)) "Save the report."
            if ok then return! doWrite input else return "[denied]"
        })

// In tests/library code with no permission system wired:
let! result = tool.InvokeAsync(ToolContext.allowAll, input)
```

The pieces fit together so the runtime layer stays independent of host-specific decision and transport logic:

- **`PermissionGate.Prompt`** — a process-wide hook in `Nao.Agents` that a host registers at startup. The grain calls it to resolve a request against host-provided decision logic.
- **Host permission broker** — when a request resolves to `Ask`, a host can route the request through its own transport and approval flow. No client or no answer within the timeout **fails closed** (deny).
- **Per-session grants** — when the user picks "remember for this session", the `SessionGrain` records the grant in its own Orleans-persisted state (`GrantedPermissions`) and never re-prompts for it; "global" grants persist to the cross-session `PermissionStore`; "once" persists nothing.
- **`PermissionOutcome`** — `{ Decision; RememberForSession }`, the value threaded from broker → gate → grain so the session knows whether to record the grant.

Settings expose a master switch (off by default) plus global allowlists:

```fsharp
{ PermissionSettings.Default with
    Enabled = true
    AllowedWebDomains = [ "example.com" ]   // matches subdomains too
    AllowedFilePaths = [ "/home/me/project" ] }
```

### Observability (O)

**Distributed Tracing** — OpenTelemetry-style spans:

```fsharp
let tracer = Tracer.inMemory ()
let root = tracer.StartTrace "user-request"
let child = tracer.StartSpan root "tool.invoke"
tracer.EndSpan child SpanStatus.Ok
```

**Metrics** — Token usage, cost tracking, latency percentiles:

```fsharp
let metrics = MetricsCollector.inMemory ()
metrics.RecordLlmCall inputTokens outputTokens latencyMs
let cost = metrics.EstimateCost MetricsCollector.gpt4o
let summary = metrics.GetMetrics() // TotalLlmCalls, AvgLatencyMs, P95, ...
```

**Resilience** — Retry with backoff, circuit breakers, fallbacks:

```fsharp
let config = { ResilienceConfig.Default with
                 RetryPolicy = RetryPolicy.ExponentialBackoff (3, 1000, 30000)
                 Fallback = FallbackStrategy.DefaultValue "cached result" }
let! result = Resilience.executeAsync config (Some circuitBreaker) myFunc input
```

### Verification (V)

**Readiness Gates** — Pre-flight checks before execution:

```fsharp
let! readiness = Verification.checkReadiness [ toolCheck; budgetCheck ] agentId input
match readiness with
| ReadinessResult.Ready -> // proceed
| ReadinessResult.NotReady reasons -> // block
```

**Execution Traces** — Full step-by-step history for analysis:

```fsharp
let trace =
    Verification.startTrace agentId input
    |> Verification.addStep (TraceAction.LlmCall "gpt-4o") input output 150L
    |> Verification.addStep (TraceAction.ToolInvocation "search") query result 25L
    |> Verification.complete finalOutput
```

**Regression Detection** — Compare against baselines:

```fsharp
let regression = Regression.detect baselineTrace currentTrace
// regression.IsRegression, regression.Regressions (latency, quality, cost)
```

### Evaluation (Nao.Eval)

Run agents against datasets with multiple evaluators:

```fsharp
let dataset = { Name = "math"; Cases = [ EvalCase.create "1" "2+2" (Some "4") ] }
let! report = EvalRunner.runDatasetAsync evaluator agent dataset EvalRunnerConfig.Default
// report.PassRate, report.AverageScore, report.TagBreakdown
```

Built-in evaluators: `ExactMatch`, `Contains`, `Regex`, `LlmJudge`, `Composite`.

### Orleans Runtime

Agents run as Orleans grains for distributed, persistent execution:

- `SessionGrain` — Full ETCLOVG-integrated session with multi-conversation support
- `SessionDirectoryGrain` — Tracks all sessions per user
- `GroupDirectoryGrain` — Organizational multi-tenancy with member/session management
- `WorkspaceRegistry` — Multiple isolated workspaces within a single silo

```fsharp
// Register multiple compiled workspaces in the silo
let registry = WorkspaceRegistry.fromWorkspaces [
    ("team-a", { WorkspaceDefinitions.Empty with Agents = [ teamAAgent ]; Tools = teamATools })
    ("team-b", { WorkspaceDefinitions.Empty with Agents = [ teamBAgent ]; Tools = teamBTools })
]

// Sessions resolve agents/tools from their workspace
let options = { AgentName = "coordinator"; WorkspaceKey = "team-a"; GroupId = Some "org-1"; ToolNames = [] }
sessionGrain.StartAsync(options)

// Switch workspace at runtime without losing conversation
sessionGrain.SwitchWorkspaceAsync("team-b")
```

#### Group Directory

Organizational isolation — groups manage members, sessions, and default workspaces:

```fsharp
let groupGrain = clusterClient.GetGrain<IGroupDirectoryGrain>("org-1")
groupGrain.InitAsync("Engineering", "team-a")
groupGrain.AddMemberAsync("user-123", "admin")
groupGrain.RegisterSessionAsync(entry)
let! sessions = groupGrain.ListUserSessionsAsync("user-123")
```

### Structured Prompts

```fsharp
let prompt =
    { Prompt.Empty with
        Role = "You are a financial analyst."
        Objective = "Analyze quarterly earnings reports."
        Constraints = [ "Use only provided data"; "Be concise" ]
        Examples = [ { Input = "Q1 revenue?"; Output = "$2.3B"; Explanation = None } ]
        OutputFormat = Json (Some """{"summary": "...", "trend": "..."}""") }
```

## Package Management

This project uses Paket for dependency management. To add a package:

1. Edit `paket.dependencies` to add the source package
2. Add the package name to the relevant project's `paket.references`
3. Run `dotnet paket install`

## Git Hooks

A pre-commit hook ensures all tests pass before commits are accepted. It runs `dotnet test` automatically.

## Coding Conventions

### File Organization

- **One type per file** — Each type, interface, or discriminated union gets its own file
- **File names match the primary type** — e.g. `AgentState` lives in `AgentState.fs`
- **Compile order matters** — Files in `.fsproj` are listed in dependency order (dependencies first)

### Naming

- **Types**: PascalCase (`CompletionResult`, `AgentGroup`)
- **Modules**: PascalCase, matching the type they operate on (`module ConversationWindow`)
- **Functions**: camelCase (`applyLastN`, `routeAsync`)
- **DU cases**: PascalCase (`LastN`, `TokenBudget`, `ByPrompt`)
- **Interfaces**: prefix with `I` (`ILlmProvider`, `IAgent`, `IMemoryStore`)

### F# Style

- Prefer discriminated unions over class hierarchies
- Prefer immutable records for data types
- Use `option` instead of null
- Use `Task<T>` for async operations (interop-friendly)
- Keep modules alongside their corresponding type for helper functions
- Use XML doc comments (`///`) for public API types and members

### Project Structure

- Source projects go under `src/`
- Test projects go under `tests/`
- Each source project has a matching `<ProjectName>.Tests` project
- Test projects use MSTest framework
- Dependencies between source projects use `<ProjectReference>`

### Testing

- Test project names: `<ProjectName>.Tests`
- Test framework: MSTest
- One test file per feature or module being tested
- Test methods should be descriptive: `OrchestratorRoutesToWeatherAgent`

## License

MIT
