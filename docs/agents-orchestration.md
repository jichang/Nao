# Agents and Orchestration

Agents are typed executable capabilities inside the broader Nao platform. They are not the ownership boundary for durable state, identity, policy, or knowledge.

## Agent contract

An `IAgent` has an explicit identity, description, priority, instructions, transport contract, and context-aware execution method. `TypedContextualAgent<'Input, 'Output>` decodes and encodes domain values through host-supplied codecs.

Nao deliberately avoids:

- Inferring public transport schemas from arbitrary runtime types
- Hiding serialization inside the runtime
- Giving agents ownership of tenant or session state
- Loading arbitrary executable definitions from JSON

## Agent actions

Iterative orchestrators can interpret model output as explicit actions:

```fsharp
type AgentAction =
    | Respond of string
    | InvokeTool of toolName: string * input: string
    | DelegateToAgent of agentName: string * input: string
    | Think of string
```

The action representation allows tracing, policy checks, progress events, tool execution, delegation, and repair to remain under the orchestrator and harness rather than inside opaque prompt code.

## Router

A router selects one specialist:

```fsharp
let router =
    Router.create
        [ weatherAgent; mathAgent ]
        (ByPrompt supervisor)

let! result =
    Router.routeAsync AgentContext.allowAll "What's the weather?" router
```

Supported strategies include explicit name, prompt-based selection, round robin, and custom selection.

## Pipeline

A pipeline runs agents sequentially, passing each output to the next stage:

```fsharp
let pipeline =
    Pipeline.create [ fetcher; summarizer; formatter ]

let! result =
    Pipeline.runAsync AgentContext.allowAll input pipeline
```

Pipelines are useful when stage order is deterministic. They should not be replaced with model planning when ordinary composition is sufficient.

## Collaborative groups

`AgentGroup` supports bounded multi-agent conversations with termination conditions such as maximum rounds, content predicates, or custom checks.

```fsharp
let group =
    AgentGroup.create [ analyst; critic ] (MaxRounds 5)

let! history =
    AgentGroup.runAsync AgentContext.allowAll "Analyze this data" group
```

A collaborative agent group is different from an organizational tenant/group directory. Current source supports the former; organizational group lifecycle is roadmap work.

## Agent-as-tool delegation

An agent can be exposed as an `ITool` so a parent orchestrator can delegate specialist work through the same action pipeline. Delegated calls should inherit identity, permissions, remaining budgets, causation, and trace context.

## Custom orchestrators

`OrchestratorBase` owns the iterative loop:

1. Generate model messages.
2. Call the provider.
3. record traces, reasoning progress, latency, and usage.
4. Validate and repair structured output.
5. Parse model output into actions.
6. Execute tools or delegate agents.
7. Append results to the running conversation.
8. Stop with a response or bounded fallback.

A subclass supplies prompting and response interpretation:

```fsharp
open Nao.Protocols

type MyOrchestrator(config: OrchestratorConfig) =
    inherit OrchestratorBase(config)

    override _.GenerateReasoningPrompt(conversation) =
        task {
            let system =
                { Role = System
                  Content = "Use the declared response protocol." }

            return system :: conversation
        }

    override _.ResponseProtocol =
        Some myProtocol
```

Hosts register an `IOrchestratorFactory` through dependency injection when runtime sessions should use a custom orchestrator.

## Response protocols

A response protocol packages:

- A name and media type
- Instructions and examples
- A parser
- Structured parse diagnostics
- A repair strategy

The orchestrator can use parse failures to request a corrected response. Repair calls are real provider calls and therefore contribute to budgets, traces, and metrics.

## Progress and observability

`OrchestratorBase` provides consistent signals around:

- Reasoning rounds
- Planning spans
- Provider calls
- Tool invocation and completion
- Sub-agent invocation and completion
- Repair and fallback calls

Subclasses should not need to reimplement those operational concerns.

## Design guidance

- Use a normal function when no model decision is required.
- Use a typed tool for deterministic side effects or external capabilities.
- Use a pipeline when execution order is known.
- Use a router when exactly one specialist should handle a request.
- Use delegation when a parent needs bounded specialist reasoning.
- Use a collaborative group only when iterative perspectives provide measured value.
- Keep authorization and resource enforcement in the harness and host, not in prompt instructions.
- Store durable state in runtime-owned stores rather than mutable agent instances.
- Bound every iterative model loop by rounds, time, calls, tokens, and cost.

## Planned platform improvements

- One durable execution request/result contract
- Budget inheritance across nested agents
- Idempotent checkpoint and replay
- Capability-based dynamic selection without arbitrary code loading
- Richer consensus or bidding protocols where validated by use cases
- Continuous evaluation of orchestration quality, cost, and failure behavior

See [Harness, security, and governance roadmap](roadmap/01-harness-security-governance.md) and [Evaluation roadmap](roadmap/03-evaluation-observability.md).
