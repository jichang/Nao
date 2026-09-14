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
            let access = ResourceAccess.file FileAction.Write input.Path
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

Resources and actions are closed, typed sets rather than free-form strings. Current resource
classes are file, web, tool, agent, memory, knowledge, model, artifact, and administration. Each class
has only its applicable actions; for example, file access uses `Read`, `Write`, `Delete`, or
`List`, while model and tool access are invocation-only and agent access represents delegation. File requests canonicalize absolute
paths, web requests require absolute HTTP(S) URLs, and named resources reject blank or
whitespace-padded identifiers.

Rule actions use `PermissionActions.All` or a non-empty `PermissionActions.Only` set. An empty
list never means wildcard access. Matching is case-sensitive for paths and named resources.
Web rules use exact scheme/host/port origins; subdomains require an explicit
`https://*.example.com` pattern, and `*` is the only all-origin wildcard. Deny always wins over
allow and ask. Malformed requests fail closed even if a caller supplies an allow default.

Tool invocation permission is intrinsic to `Tool.create`; tool authors cannot accidentally publish
an ungoverned local tool by supplying an empty resource list. Declared file, web, memory, or other
resource permissions are enforced in addition to invocation itself. MCP tools use the same
executable boundary. Agent tools and direct orchestrator delegation require canonical agent
delegation access, and built-in memory search, remember, and forget tools separately require
read, write, and delete access to durable memory. All these decisions call the host-owned
`RequestPermission` capability. Model responses and prompt text can request an operation but
cannot construct, replace, or bypass that capability.

`PermissionApprovalBroker` is the host-owned integration point for interactive decisions. The
runtime injects it into the session grain; there is no process-global mutable prompt callback.
Each `PermissionApprovalRequest` carries the authenticated scope, correlation, canonical access,
reason, request time, force-confirmation flag, and the grant scopes that caller can implement. The gate rejects
an approved scope outside that set, denies when the broker is unavailable or exceeds its
configured timeout, and propagates caller cancellation.

Approved operation grants apply only to the current request. Session, workspace, and durable grants
persist the canonical typed permission target and can carry an expiry. The runtime reloads active
grants before every decision, so expiry or revocation cannot be hidden by an in-process cache.
Session grants are isolated to one session, workspace grants cross sessions in one workspace, and
durable grants cross workspaces. Every reusable grant remains bound to the tenant, selected group,
original and effective user, and service authority lineage that approved it. Agent lineage and
mutable roles or claims do not widen or fragment reusable grants, and raw authentication evidence
is never stored in grant events. Approving a read therefore cannot authorize a later write, and
impersonated or delegated service authority cannot mint a grant reusable by broader direct authority.

Every runtime permission check produces immutable `PermissionDecisionEvidence`. It records the
canonical access, final decision and basis, the exact `nao.permission` policy version, and the ID
and version of every reusable grant that authorized the request. The evidence is written to the
authenticated audit stream before the decision returns, including denial paths, and successful
turn records retain the same evidence for replay and feedback. Grant version validation fails
closed during writes and event replay; unsupported versions cannot authorize access. A runtime
without an authenticated audit capability cannot complete a protected permission decision.

## Policy engine

Policies evaluate execution context and can block, warn, request confirmation, or modify input. Typical policies include:

- Token, cost, duration, and call budgets
- Rate limits
- Data-classification restrictions
- Model or provider eligibility
- Network destination restrictions
- Tenant and workspace quotas

A modified policy result must alter the operation that actually executes. A policy or approval outage must never silently become allow.

Policy callbacks are asynchronous, receive caller-linked cancellation, and run within the
positive timeout configured on `PolicyEngine`. A callback can return a typed
`PolicyEvaluationFailure`; timeout, non-fatal callback exceptions, and modification-transform
exceptions are converted to explicit failures. Every such failure blocks regardless of whether
the policy declared warning, confirmation, or modification enforcement. Caller cancellation and
fatal runtime exceptions propagate rather than being relabeled as policy failures.

Nao provides strict built-in factories for cost and total-token budgets, process-local
sliding-window rate limits, data-classification ceilings, HTTP(S) destination allowlists, and
model eligibility allowlists. Every factory requires an explicit policy ID, host-managed
version, and hierarchical scope, so multiple independently versioned instances can coexist.
Limits must be positive; destination and model allowlists must be non-empty.

Classification, destination, and model rules consume typed `PolicyFacts`, never values parsed
from prompts or free-form tool input. Hosts attach classifications and planned model targets to
an immutable `ExecutionRequest`. Child executions inherit those facts. Tool stages additionally
derive destination origins and model targets from the tool's canonical `ResourceAccess`
declarations, so undeclared prompt text cannot manufacture eligibility. Data-classification
policies fail closed when classification is absent. Destination matching uses normalized
scheme, IDN host, and effective port; paths do not widen an origin. The built-in rate limiter
is process-local by design; distributed deployments must supply a policy backed by shared,
strongly consistent quota state.

The standard orchestrator also submits the actual `LlmProvider` name through the governed model
eligibility capability immediately before every live or replayed provider invocation. A denial
occurs before LLM-call budget consumption or provider access. Custom agents that invoke provider
capabilities outside the standard orchestrator must expose the same host-governed boundary rather
than treating a declared planned model as proof of the model actually used.

Policy evaluation identifies a typed lifecycle stage: `PreInput`, `PreAction`,
`PreSideEffect`, `PostOutput`, or `PreCommit`. Agent input executes through `PreInput`;
tool invocation executes through `PreAction`; externally side-effecting and reversible tools
also pass `PreSideEffect` before replay lookup, intent recording, or execution. Successful
agent output passes `PostOutput` and then `PreCommit` before constitution validation,
observability storage, lifecycle completion, or publication. Applied modifications flow into
the next stage and become the actual agent input, tool input, or returned response. The same
effective value is supplied to confirmation as `EffectiveInput`.

Policies have an explicit hierarchical scope. Platform policies match every execution; tenant
policies match the authenticated tenant; workspace policies additionally match the selected
group and workspace; agent policies add the logical agent identity; tool policies add the agent
and current tool identity; and session policies match the complete claim-free authorization
identity. Agent and tool scopes therefore cannot leak across tenants or workspaces, and tool
policies do not run during agent-only stages.

Matching policies are evaluated from broadest to narrowest: platform, tenant, workspace, agent,
tool, then session. Declaration order is stable among policies at the same level. Each modifying
policy receives the value produced by every broader and earlier matching modification. Every
matching violation and warning is retained. A block at any level is absolute and cannot be
overridden by a narrower modification, warning, or confirmation; multiple matching confirmation
violations are delivered together in one broker request. Duplicate policy identifiers and
session scopes without a session-bound authorization are rejected.

Every policy definition carries a mandatory host-managed version. Each matching policy emits
immutable `PolicyEvaluationEvidence`, including policies that pass. Evidence records the policy
ID and version, scope level, lifecycle stage, agent and optional tool, action, outcome, optional
failure code, optional confirmation decision, evaluation time, and SHA-256 digests of the input
and effective input.
It deliberately excludes raw inputs, policy messages, and tenant/workspace/session coordinates.
The harness writes each record to the authenticated audit partition before continuing and
retains the same records in `ExecutionResult` and successful `TurnRecord` values. A configured
policy engine without an audit capability fails closed, and audit-write failures propagate
instead of allowing execution without evidence.

`Confirm` is not a block alias. Matching confirmation policies produce an explicit request for
the injected, timeout-bounded `PolicyConfirmationBroker`. The request carries authenticated
authorization, correlation, typed policy context, only the matching confirmation violations,
the effective modified input, and request time. Approval continues execution; denial, timeout,
or an unavailable broker blocks it. A matching block policy wins without invoking confirmation,
and caller cancellation propagates.

## Constitutions and output controls

Constitutions separate deterministic validators from probabilistic judges at the type level.
`Constitution.checkDeterministic` invokes only reproducible validators, while
`Constitution.evaluateAsync` runs deterministic validation first and then invokes
cancellation-aware probabilistic judges. Deterministic rules identify versions for every
external validator dependency. Probabilistic rules identify their model, prompt version, and
violation threshold; their result supplies a bounded score and rationale. Invalid thresholds,
scores, or missing rationales are rejected rather than interpreted as safe.
Hard deterministic violations short-circuit evaluation before any probabilistic dependency is
invoked.

Every rule declares a typed severity (`Advisory`, `Warning`, `Error`, or `Critical`), a
hierarchical `PolicyScope`, and a requested remediation (`Observe`, `Redact`, `Repair`,
`Reject`, `Escalate`, or `Quarantine`). Constitution scope matching uses the same authenticated
tenant, selected-group, workspace, agent, and session coordinates as runtime policy evaluation;
tool scopes do not match final agent-output evaluation. Error and critical violations currently
must request an enforcing remediation rather than observation.

When multiple rules fail, the harness resolves remediation by restrictive precedence:
`Quarantine`, `Escalate`, `Reject`, `Repair`, `Redact`, then `Observe`. Reject returns no output.
Escalation and quarantine invoke distinct host callbacks and return no output. Redaction and
repair invoke the injected `ConstitutionRemediationBroker`, then send the transformed value
through `PostOutput`, `PreCommit`, and complete constitution evaluation again. A transformation
that is rejected by policy or still requires remediation is rejected rather than recursively
retried. Callback errors and cancellation propagate, so unavailable remediation cannot become
an implicit allow.

```fsharp
let constitution =
    Constitution.create "safety" "2026-09-14"
    |> Constitution.addRule Constitution.noPrivateDataRule
    |> Constitution.addRule Constitution.noHarmRule

let deterministicResult = Constitution.checkDeterministic constitution output
let! completeResult = Constitution.evaluateAsync constitution output cancellationToken
```

Probabilistic judges cannot override deterministic violations. Every matching rule, including a
pass, produces immutable `ConstitutionEvaluationEvidence` with constitution identity and version,
rule category, severity, scope kind, requested remediation, outcome, output SHA-256 digest, and
evaluator-specific evidence. Deterministic evidence contains pinned dependency versions.
Probabilistic evidence contains model, prompt version, threshold, score, and a rationale digest,
never the raw output or rationale. The harness writes every evaluation to authenticated audit
storage before continuing; a missing or failed audit capability prevents governed execution.

Constitution identity and version are mandatory at construction. Every deterministic validator
and probabilistic judge also declares a distinct evaluator identity and host-managed version;
both are copied into every evaluation record. Model identity and prompt version remain separate
from evaluator version because changing orchestration code, thresholds, or parsing can alter a
judge without changing either model or prompt. Workspace registration accepts at most one
effective constitution: hosts must explicitly compose and version a combined definition rather
than relying on implicit merging that would obscure provenance.
`ExecutionPolicyDecisions.ConstitutionDisposition` records whether output was released,
redacted, repaired, rejected, escalated, or quarantined. Quarantined content is passed only to
the host quarantine callback and is never placed in `ExecutionResult.Outputs`.
`ConstitutionQuarantineStore` provides a versioned owner-partitioned storage boundary for that
callback. Its only read path, `reviewAsync`, requests the exact
`administration/read/constitution-quarantine/{executionId}` resource from a host authorizer,
requires an allow decision for that resource, and durably audits the decision before reading.
Denied reviews return no content, authorization in another complete owner identity cannot find
the record, mismatched authorization evidence fails closed, and audit failure prevents access.

The constitution disposition is the output release gate. Rejected, escalated, quarantined, or
unsuccessfully transformed output cannot trigger successful lifecycle completion, answer
publication, trace persistence, the final successful audit record, or a successful harness
checkpoint. Session persistence already accepts only successful results with a response, so
these terminal outcomes cannot enter turn records or user-facing conversation history.

## Audit and execution journal

`AuditLog` records governance decisions under an authenticated `AuthorizationScope`; agent identifiers are actor metadata, while file and ADO streams are partitioned by the complete claim-free identity. `ExecutionJournal` uses the same scope for tool executions, checkpoints, side-effect transitions, and compensation outcomes. Revertible operations can be compensated in reverse order:

Journal payloads require stable record and turn identity but contain no owner or authentication evidence. Scope deletion and strict cutoff retention are available across in-memory, file, and ADO.NET event-stream adapters, but hosts must not purge records while compensation remains possible.

```fsharp
let journal = InMemoryExecutionJournal.create ()
let! failures = ExecutionJournal.revertAllAsync authorization journal tools
```

A revert hook is not automatically a transaction. Durable execution must record side-effect intent, outcome, ambiguity, and compensation separately.

## Isolation status

Nao defines `SandboxConfig`, `ResourceLimits`, and functional `ExecutionEnvironment` values. Worker-resolvable agents can run in a separate process through a versioned protocol, but this is not yet an operating-system security boundary. Untrusted tools still require:

- Non-Linux identity isolation and separate host-account execution
- Filesystem allowlists outside explicit Linux Landlock mode
- Denied-by-default network access
- Hard aggregate CPU limits
- Typed secret references and full secret lifecycle controls

Process workers inherit no host environment variables and receive only the explicit `SandboxConfig.EnvironmentVariables` map. A configured working directory is validated and applied before launch, but does not prevent access to paths outside it. Explicit Linux user-namespace identity mode maps only the host caller to a configured worker-visible UID/GID and fails closed when setup is unavailable; it does not change the caller's host filesystem DAC permissions. Explicit `ProcessFileSystemIsolation.LinuxLandlock` mode validates and enforces `SandboxConfig.AllowedPaths` before handler execution; trusted worker/runtime directories receive read-only baseline access, and setup fails closed. Disabled mode and non-Linux platforms do not enforce these controls. Protocol v1 gives worker handlers the remaining call, token, and cost allowances through `WorkerExecutionBudget`; usage reported through this capability is merged into the host budget, while bypass calls are not observable. A host may declare opaque handles through `ProcessSecretResolver`; values resolve after handshake and are available only through `WorkerSecrets`, while undeclared handles fail without resolver invocation. Process execution kills the complete worker process tree when its cancellation token is signalled. Configured memory and CPU limits sample direct-worker working-set and processor-time usage and kill the tree on excess. These reactive checks can overshoot, are not hard operating-system quotas, and CPU accounting excludes descendants. A configured output limit bounds combined stdout/stderr bytes and drains stderr. Optional `ProcessOutputCapture` applies a mandatory host redactor and publishes separately bounded stream artifacts through the governed execution context; capture does not replace the execution output limit. Hosts must propagate graceful shutdown through the cancellation token.

## Production checklist

- Never use `AgentContext.unrestrictedForTests ()` for protected production operations.
- Validate resource identifiers after canonicalization.
- Enforce authorization inside storage/retrieval boundaries, not only in UI code.
- Keep secret values out of prompts, traces, logs, and errors.
- Bound output and error sizes.
- Apply timeout and cancellation to all external calls.
- Audit every allow, deny, confirmation, modification, and side effect.
- Isolate untrusted execution before describing it as sandboxed.

See the [harness, security, and governance roadmap](roadmap/01-harness-security-governance.md).
