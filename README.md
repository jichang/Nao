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
- **Pluggable Tool Execution** — Tools run as processes, HTTP calls, or custom executors (gRPC, MCP, etc.)
- **Governance** — Constitution rules, permission models, audit logging, and runtime policy enforcement
- **Resource Permissions** — Deny-by-default file/web access with interactive, per-session approval prompts; tools declare the permissions they need and can request access dynamically through a `ToolContext`, with grants remembered per session or globally
- **Observability** — Distributed tracing (OpenTelemetry-style), cost metrics, circuit breakers, retries
- **Verification** — Readiness gates, execution trace capture, LLM judges, regression detection
- **Evaluation** — Test case framework with multiple evaluators, LLM judges, and dataset-level reports
- **Multi-Provider Support** — Pluggable LLM backends (OpenAI, Anthropic, Ollama, vLLM, llama.cpp)
- **Workspace Loader** — JSON definitions and assembly plugin discovery for agents, tools, and evals
- **Multi-Workspace Runtime** — Multiple isolated workspaces within a single Orleans silo with dynamic hot-reload
- **Group Directory** — Organizational multi-tenancy: groups own sessions, members, and default workspaces
- **Desktop Assistant** — Avalonia.FuncUI chat app with an embedded ASP.NET Core + Orleans server: real-time execution-trace streaming, dark/light theme switching, and a localizable UI
- **F# First** — Immutable records, discriminated unions, and functional composition throughout

## Project Structure

```
Nao.slnx
├── src/
│   ├── Nao.Core/                # Core types: Message, Role, ContentMeta, ILlmProvider
│   ├── Nao.Agents/              # Agent framework (ETCLOVG architecture)
│   │   ├── Shared/              # Cross-layer types (RetryPolicy)
│   │   ├── Core/                # IAgent, AgentId, AgentState, Tool (verify/revert), AgentAction
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
│   ├── Nao.Loader/             # Workspace loader: JSON defs, multi-mode execution, plugins
│   ├── Nao.Providers/          # LLM provider implementations
│   ├── Nao.Runtime.Orleans/    # Distributed runtime (grains, workspaces, groups)
│   │   ├── Workspace/           # WorkspaceRegistry (multi-tenant workspace isolation)
│   │   └── Grains/              # SessionGrain, SessionDirectory, GroupDirectory
│   ├── Nao.Documents/          # Unified document model + format converters (NuGet-backed)
│   └── Nao.Assistant/          # Avalonia.FuncUI desktop chat app (embedded server + UI)
│       ├── Domain/              # Contracts, AppSettings (theme/language persistence)
│       ├── Server/              # Embedded ASP.NET Core + Orleans host, WS streaming
│       ├── Client/              # NaoClient WebSocket client
│       ├── Components/          # Theme, Localization, reusable FuncUI controls
│       └── Views/               # Shell, SessionView, SettingsView, BuilderView
└── tests/
    ├── Nao.Core.Tests/
    ├── Nao.Agents.Tests/        # Unit tests for all ETCLOVG layers
    ├── Nao.Eval.Tests/
    ├── Nao.Loader.Tests/
    ├── Nao.Providers.Tests/
    ├── Nao.Runtime.Orleans.Tests/
    ├── Nao.Documents.Tests/
    ├── Nao.Assistant.Tests/
    └── Nao.E2E.Tests/           # End-to-end: orchestration + full ETCLOVG harness demos
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
dotnet build

# Run tests
dotnet test
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

The `Orchestrator` uses an abstract base class (`OrchestratorBase`) with virtual members that can be overridden via inheritance. This solves the problem that function-valued fields (like action parsers) cannot be expressed in JSON configuration:

```fsharp
type MyOrchestrator(config: OrchestratorConfig) =
    inherit OrchestratorBase(config)

    override _.TryParseAction(content) =
        // Custom parsing logic for your LLM's output format
        if content.Contains("<tool>") then
            Some (InvokeTool ("myTool", content))
        else
            None

    override _.BuildSystemPrompt() =
        "You are a domain-specific assistant. Use XML tags to invoke tools."

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

Available virtual members on `OrchestratorBase`:

| Member | Purpose |
|--------|---------|
| `BuildSystemPrompt()` | Customize system prompt generation |
| `TryParseAction(content)` | Parse LLM output into tool/agent actions |
| `OnToolResult(name, input, result)` | Hook after tool execution |
| `OnRoundComplete(round, content)` | Hook after each reasoning round |

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

The pieces fit together so the runtime layer never has to reference the server:

- **`PermissionGate.Prompt`** — a process-wide hook in `Nao.Agents` that the server registers at startup. The grain calls it to resolve a request against the real decision logic (settings, persisted grants, live prompt) it cannot otherwise see.
- **`PermissionBroker`** (server) — when a request resolves to `Ask`, the broker ships a `PermissionRequestDto` over the session's WebSocket, parks the call, and resumes on the user's reply. No client or no answer within the timeout **fails closed** (deny).
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
// Register multiple workspaces in the silo
let registry = WorkspaceRegistry.fromWorkspaces [
    WorkspaceId.create "team-a", loadedDefsA
    WorkspaceId.create "team-b", loadedDefsB
]

// Sessions resolve agents/tools from their workspace
let options = { AgentName = "assistant"; WorkspaceKey = "team-a"; GroupId = Some "org-1"; ToolNames = [] }
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

## Desktop Assistant

`Nao.Assistant` is a cross-platform desktop chat client built with [Avalonia](https://avaloniaui.net/) and [Avalonia.FuncUI](https://github.com/fsprojects/Avalonia.FuncUI), styled with [Semi.Avalonia](https://github.com/irihitech/Semi.Avalonia). It hosts an **embedded ASP.NET Core + Orleans server** in-process, so a single executable provides both the runtime and the UI.

```bash
dotnet run --project src/Nao.Assistant
```

Highlights:

- **Live execution trace** — As a turn runs, the assistant streams what it is doing (reasoning, tool calls, sub-agent steps) over a WebSocket and renders the process *above* the final answer in real time, instead of hiding it behind a "details" toggle.
- **Unified per-session workspace** — Each conversation gets its own folder on disk under the app data directory (`<NAO_DATA_DIR or ./.nao-data>/sessions/<session>/files`). This single directory is the working directory shared by uploaded attachments, tool output, and generated files: every file tool (`read_file`, `write_file`, `list_folder`, `search_files`, `find_files`, `convert_document`) operates inside it, isolated per conversation. The file listing the UI shows is a *reconciled view* over the folder — files a tool writes directly appear as download chips automatically, and deleted files drop out. Paths are confined to the session folder (traversal-guarded).
- **Event-driven storage** — The system never decides *where* observability/feedback data lands. Producers (the `SessionGrain`) publish domain events (`TurnCompleted`, `ImplicitFeedbackCaptured`) carrying an `EventScope` of ids (user, session, conversation, action, parent), and a subscribed consumer in `Nao.Events` persists them. `FeedbackEventConsumer` files feedback under `sessions/<session>/feedback/` via a `rootFor` function; pointing that function at one shared folder (or swapping the backing `FeedbackService` for a database) moves everything with **zero producer changes**. Reads and the synchronous *submit-feedback* command stay a separate query path (CQRS), so a failing consumer never breaks a turn.
- **Observability over the same bus** — The agent harness's fine-grained observability sinks (traces, metrics, tool/LLM timings, the execution journal, regression traces, and governance audit) also flow through the bus. The harness is handed a `PublishingHarnessServices` *tee* from `ObservabilityServices`: every span/metric/journal/trace/audit **write** is broadcast as an `ObservabilityCaptured` event (each stamped with the producing turn's `EventScope`, including the per-turn action id threaded explicitly into the per-turn observability services) while **reads** — regression baselines, revert history — still hit the real backing store so behaviour is unchanged. `ObservabilityServices` files everything under `sessions/<session>/observability/` via its backing factory; pointing that factory at a shared folder or another store needs **zero producer changes** and no grain edits.
- **Conversations over the same bus** — Transcript persistence completes the event-driven trio. The `SessionGrain` still writes through plain `IConversationStore`, but that store is a `PublishingConversationStore` *tee*: every append/save/delete is persisted to the backing `FileConversationStore` (so history **reads** stay correct) **and** broadcast as a `ConversationCaptured` event carrying transport-neutral message payloads and the turn's `EventScope`. Swapping the backing store for a database or cloud transcript store needs **zero producer changes**, and any subscriber can persist or forward the transcript stream independently.
- **Per-agent tool scoping** — Each agent only sees the tools declared in its definition's `tools` array, intersected with the session's tool pool. A tool like `convert_document` can be reserved for the `converter` specialist so the top-level assistant cannot invoke it directly — it must delegate instead.
- **Prefer-agent delegation** — When both a specialist sub-agent and a raw tool could accomplish a task, the assistant is instructed to delegate to the purpose-built agent rather than call the tool itself.
- **Async specialist agents** — Agents flagged `"is_async": true` (e.g. the `converter`) run as background tasks in their own sub-session that shares the originating conversation's workspace folder. When the assistant delegates to one, it spawns the task and replies immediately with a task token instead of blocking; the user keeps chatting and can track the task's status or download its generated file from the task tag when it finishes.
- **Document conversion engine** — `convert_document` is backed by `Nao.Documents`, which maps every format onto one unified document model and converts through it. Parsing and rendering of complex formats are delegated to well-maintained NuGet libraries rather than hand-written parsers: [Markdig](https://github.com/xoofx/markdig) (Markdown), [HtmlAgilityPack](https://html-agility-pack.net/) (HTML), [DocumentFormat.OpenXml](https://github.com/dotnet/Open-XML-SDK) (`.docx`/`.xlsx`/`.pptx`) and [PDFsharp/MigraDoc](https://docs.pdfsharp.net/) (PDF). The tool reads `.md`/`.markdown`, `.txt`, `.html` and `.docx`, and writes those plus `.pdf`, `.xlsx` and `.pptx`. The target may be a destination filename (`report.pdf`) or just a format (`pdf`), in which case the output is named after the source — the source's type always picks the input format and the target the output format, so "convert markdown to pdf" can never run in reverse.
- **Attachments read on demand** — Uploaded files are saved to the session folder rather than inlined into the prompt; the model is told their names and reads them with `read_file` only when it actually needs the contents, keeping large files out of the conversation. Two uploads that share a name don't clobber each other — the later one is stored under a disambiguated name like `report (1).pdf`, and the model is told the actual stored name.
- **Opt-in knowledge base** — User-uploaded knowledge documents are *not* injected into every turn. They are searchable only through the explicit `search_knowledge` tool, and agents are instructed to ask permission before using it.
- **Theme switching** — Dark and light themes are selectable from Settings, applied instantly via centralized design tokens, and persisted across launches.
- **Localizable UI** — UI strings flow through a central `Localization` table and ship in 10 languages (English, 简体中文, हिन्दी, Español, Français, العربية, Português, Русский, 日本語, Deutsch), selectable from Settings and applied live.
- **Persisted preferences** — Theme, language, provider, orchestrator, and workspace settings are stored in `AppSettings` and reloaded on startup.

LLM turns can run well beyond Orleans' default 30s grain-response timeout, so the embedded host raises the silo/client `ResponseTimeout` accordingly.

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
