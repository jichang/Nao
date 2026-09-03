# Tools, Security, and Governance

Tools connect model-directed execution to deterministic code and external systems. Because tools can create side effects, their contracts, permissions, isolation, audit, and compensation behavior are platform concerns rather than prompt conventions.

## Typed tools

`Tool` is an immutable executable capability containing explicit metadata, schemas, required resources, execution, and optional revert behavior. `Tool.create` combines typed `ToolCodec` values with a `ToolOperation` to decode input, request permissions, run domain logic, and encode output.

```fsharp
type DeployInput =
    { Environment: string
      Release: string option }

type DeployOutput =
    { DeploymentId: string }

let deployTool =
    Tool.create
        "deploy"
        "Deploy an application."
        0
        []
        DeployContract.input
        DeployContract.output
        (ToolOperation.create (fun _context input -> task {
            let! id = deploy input.Environment input.Release
            return Ok { DeploymentId = id }
        }))
```

Explicit codecs keep transport behavior testable and prevent the framework from guessing public schemas.

## Tool protocol

`ToolProtocol` supports discovery, availability, and invocation. Middleware can add cross-cutting behavior.

```fsharp
let protocol =
    ToolProtocol.fromTools tools
    |> ToolProtocol.withMiddleware
        (ToolProtocol.rateLimitMiddleware 100)

let! available = protocol.IsAvailable "get_weather"
let! result = protocol.InvokeAsync agentContext "get_weather" "London"
```

`ToolProtocol` forwards the caller's `AgentContext`; production hosts must supply session identity,
permission, and publishing callbacks. `ToolSelector` discovers through the protocol and prepares a
bounded, schema-aware candidate set for the orchestrator prompt. The orchestrator's LLM still makes
the final tool choice.

Each orchestrator can receive its own protocol through `Orchestrator.createWithProtocol`. Nao also
supports MCP transports for external tool servers. MCP definitions are adapted to ordinary qualified
tools such as `server.tool`, so external transport does not bypass local identity, policy, timeout,
or resource controls.

## Static and dynamic permissions

A tool can declare known resources up front or request a concrete resource after decoding its input:

```fsharp
let saveTool =
    Tool.create
        "save"
        "Save a report."
        0
        []
        SaveContract.input
        SaveContract.output
        (ToolOperation.create (fun context input -> task {
            let access = ResourceAccess.File("write", input.Path)
            let! allowed =
                context.RequestPermission access "Save the report." false

            if allowed then
                return Ok(writeReport input)
            else
                return Error(ToolExecError.PermissionDenied "Write denied.")
        }))
```

Prompts cannot grant permissions. The host constructs `AgentContext` and owns the approval mechanism.

## Permission decisions

`ResourcePermission` evaluates rules with deny precedence and returns:

- `Allow`
- `Deny`
- `Ask`

`PermissionGate` is the host integration point for interactive decisions. A production host should fail closed when no client, broker, or timely answer is available.

Grant scopes may include one operation, one session, workspace, or durable policy. Durable grants require expiry, revocation, tenant ownership, and audit semantics; these are tracked in the [governance roadmap](roadmap/01-harness-security-governance.md).

## Policy engine

Policies evaluate execution context and can block, warn, request confirmation, or modify input. Typical policies include:

- Token, cost, duration, and call budgets
- Rate limits
- Data-classification restrictions
- Model or provider eligibility
- Network destination restrictions
- Tenant and workspace quotas

A modified policy result must alter the operation that actually executes. A policy or approval outage must never silently become allow.

## Constitutions and output controls

Constitutions evaluate generated output against deterministic or probabilistic rules. Remediation can include repair, redaction, rejection, escalation, or quarantine.

```fsharp
let constitution =
    Constitution.empty "safety"
    |> Constitution.addRule Constitution.noPrivateDataRule
    |> Constitution.addRule Constitution.noHarmRule

let result = Constitution.check constitution output
```

Probabilistic judges should identify model, prompt, threshold, and evidence. They must not override deterministic schema, permission, or citation failures.

## Audit and execution journal

`AuditLog` records governance decisions. `ExecutionJournal` records tool executions and outcomes. Revertible operations can be compensated in reverse order:

```fsharp
let journal = InMemoryExecutionJournal.create ()
let! failures = ExecutionJournal.revertAllAsync journal tools
```

A revert hook is not automatically a transaction. Durable execution must record side-effect intent, outcome, ambiguity, and compensation separately.

## Isolation status

Nao currently defines `SandboxConfig`, `ResourceLimits`, and `IExecutionEnvironment`, but current local execution is not a process or container security boundary. Untrusted tools require planned process/container environments with:

- Restricted identities
- Filesystem and environment allowlists
- Denied-by-default network access
- CPU, memory, time, output, call, token, and cost limits
- Child-process cleanup
- Secret references resolved only inside authorized boundaries

## Production checklist

- Never use `AgentContext.allowAll` for protected production operations.
- Validate resource identifiers after canonicalization.
- Enforce authorization inside storage/retrieval boundaries, not only in UI code.
- Keep secret values out of prompts, traces, logs, and errors.
- Bound output and error sizes.
- Apply timeout and cancellation to all external calls.
- Audit every allow, deny, confirmation, modification, and side effect.
- Isolate untrusted execution before describing it as sandboxed.

See the [harness, security, and governance roadmap](roadmap/01-harness-security-governance.md).
