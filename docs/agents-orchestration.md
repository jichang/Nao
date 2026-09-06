# Agents and Orchestration

Agents are typed executable capabilities inside the broader Nao platform. They are not the ownership boundary for durable state, identity, policy, or knowledge.

## Functional agent programs

New orchestration code uses `Agent`, an immutable record containing metadata and
execution functions. Programs are ordinary values: they can be created, copied, decorated,
stored in graphs, and tested without inheritance or interface implementations.

```fsharp
let summarizer =
    Agent.createContextual
        "summarizer"
        "Summarizer"
        "Produces a concise summary"
        0
        [ "summarization" ]
        AgentContract.Text
        (fun context input -> summarizeAsync context input)
```

An `Agent` has an explicit identity, description, priority, responsibilities, transport contract,
and context-aware execution function.

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

`Router` selects one immutable `Agent`. Its strategy is data represented by the
`RoutingStrategy` union.

A router selects one specialist:

```fsharp
let router =
    Router.create
        [ weatherAgent; mathAgent ]
        (ByPrompt supervisor)

let! result =
    Router.routeAsync agentContext "What's the weather?" router
```

Supported strategies include explicit name, prompt-based selection, round robin, and custom selection.

## Pipeline

`Pipeline` composes immutable agents as a linear execution graph.

A pipeline runs agents sequentially, passing each output to the next stage:

```fsharp
let pipeline =
    Pipeline.create [ fetcher; summarizer; formatter ]

let! result =
    Pipeline.runAsync agentContext input pipeline
```

Pipelines are useful when stage order is deterministic. They should not be replaced with model planning when ordinary composition is sufficient.

## Collaborative groups

`AgentGroup` provides functional collaborative execution. It terminates when its
configured condition is met or when a complete pass produces no messages, preventing an empty
or stalled group from spinning indefinitely.

`AgentGroup` supports bounded multi-agent conversations with termination conditions such as maximum rounds, content predicates, or custom checks.

```fsharp
let group =
    AgentGroup.create [ analyst; critic ] (MaxRounds 5)

let! history =
    AgentGroup.runAsync agentContext "Analyze this data" group
```

A collaborative agent group is different from an organizational tenant/group directory. Current source supports the former; organizational group lifecycle is roadmap work.

## Agent-as-tool delegation

An agent can be exposed as a functional `Tool` value so a parent orchestrator can delegate specialist work through the same action pipeline. Delegated calls should inherit identity, permissions, remaining budgets, causation, and trace context.

## Custom orchestrators

`Orchestrator.create` owns the iterative loop and consumes an `OrchestratorDefinition`:

1. Generate model messages.
2. Call the provider.
3. record traces, reasoning progress, latency, and usage.
4. Validate and repair structured output.
5. Parse model output into actions.
6. Execute tools or delegate agents.
7. Append results to the running conversation.
8. Stop with a response or bounded fallback.

A definition supplies prompting and response interpretation. `PrepareRound` can atomically bind
the prompt, parser, validation, and repair behavior to one tool-catalog snapshot:

```fsharp
open Nao.Protocols

let prepareRound conversation =
    task {
        let! selection = selector.SelectAsync taskDescription tokenBudget toolProtocol
        let responseProtocol = createResponseProtocol selection.Available
        let system = buildSystemPrompt selection.Selected responseProtocol

        return
            { Messages = system :: conversation
              ResponseProtocol = Some responseProtocol
              ParseActions = responseProtocol.Parse >> Result.defaultValue []
              ValidateResponse = validate responseProtocol
              BuildRepairMessage = buildRepairMessage }
    }

let definition =
    { OrchestratorDefinition.create (fun conversation ->
        task {
            let! round = prepareRound conversation
            return round.Messages
        }) with
        PrepareRound = Some prepareRound }

let agent =
    Orchestrator.createWithProtocol toolProtocol config definition
```

Each orchestrator agent may capture a different `ToolProtocol`, including different middleware,
permission policy, local tools, or MCP registry. The protocol is the authoritative discovery and
execution boundary. `ToolSelector` creates a bounded candidate set from that protocol; the
orchestrator's LLM makes the final contextual choice among the advertised tools.

## Loop engineering

`LoopDefinition<'State, 'Output>` models one bounded state machine. Each step returns either
`Continue` with the next immutable state or `Complete` with an output. `Loop.runAsync` owns the
iteration limit and reports `IterationLimitReached` rather than allowing an unbounded cycle.

`LoopAgent.create` packages a domain-specific loop as an `Agent` that can be composed with
other functional orchestration primitives.

## Graph engineering

`ExecutionGraph` makes workflow topology explicit through agent nodes and conditional edges.
Edges are evaluated in declaration order, cycles are allowed only within `MaxSteps`, and every
successful run returns its ordered path. `ExecutionGraph.asAgent` packages a graph as an
`Agent`.

The ETCLOVG harness remains the outer governance, lifecycle, observability, and verification
boundary. Loops govern iteration; graphs govern topology; the harness governs execution.

## Response protocols

A response protocol packages:

- A name and media type
- Instructions and examples
- A parser
- Structured parse diagnostics
- A repair strategy

The orchestrator can use parse failures to request a corrected response. Repair calls are real provider calls and therefore contribute to budgets, traces, and metrics.

## Progress and observability

`Orchestrator` provides consistent signals around:

- Reasoning rounds
- Planning spans
- Provider calls
- Tool invocation and completion
- Sub-agent invocation and completion
- Repair and fallback calls

Planner definitions do not need to reimplement those operational concerns.

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

- Idempotent checkpoint and replay
- Capability-based dynamic selection without arbitrary code loading
- Richer consensus or bidding protocols where validated by use cases
- Continuous evaluation of orchestration quality, cost, and failure behavior

See [Harness, security, and governance roadmap](roadmap/01-harness-security-governance.md) and [Evaluation roadmap](roadmap/03-evaluation-observability.md).
