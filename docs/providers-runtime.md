# Providers and Distributed Runtime

Nao separates model-provider integration from agent behavior and offers Orleans as an optional distributed session runtime.

## Provider abstraction

The immutable `LlmProvider` capability record provides common completion behavior with an optional streaming function. Current adapters cover OpenAI-compatible services, Anthropic, DeepSeek, Kimi, Ollama, vLLM, and llama.cpp. Source-backed embeddings use the separate `KnowledgeEmbeddingProvider` contract, with hosted OpenAI-compatible and local Ollama adapters.

Buffered and streaming calls require an operation `CancellationToken`. The OpenAI-compatible,
Anthropic, and Ollama implementations propagate it through HTTP sends, response bodies, streams,
and line reads. Provider adapters rethrow cancellation rather than translating it into a platform
failure, so execution-budget and caller cancellation stop underlying I/O.

Knowledge generation uses the same provider contract for both grounded-answer production and
semantic claim-support evaluation. The claim-support adapter requires structured assessments,
consistent detailed token usage, explicit pricing, and bounded repair; provider output cannot
introduce claim or citation identities that were absent from verified evidence.

The production qualification runner accepts already activated provider capabilities and
authorization-bound retrieval contexts. Endpoint selection, credential resolution, and model
pricing remain explicit host configuration. Missing live configuration cannot fall back to
reference results while claiming production qualification.

Adapters are independently consumable through `Nao.Providers.OpenAICompatible`, `Nao.Providers.Anthropic`, and `Nao.Providers.Ollama`. The OpenAI-compatible and Ollama packages also expose their protocol-specific embedding configurations and factories while depending only on the vendor-independent contracts in `Nao.Knowledge`. Applications that want closed provider selection and all adapters can opt into the aggregate `Nao.Providers` composition package. Direct adapter consumers do not inherit unrelated provider implementations.

Embedding adapters require an explicit, validated profile and an authoritative tokenizer callback.
Hosted credentials are references rather than raw configuration values and are resolved under the
caller's authenticated scope for every outbound operation. Provider-reported model identity,
usage, vector cardinality, dimensions, and finite values are validated across the adapter and
embedding executor. HTTP error bodies are not copied into platform failures because they may
echo source content. Callers dispose provider capabilities when their HTTP lifetime ends.

Applications must not select a provider by hard-coded model-name heuristics.
`ProviderCapabilityObservation` records a versioned configured or probed snapshot for:

- Chat and streaming
- Tool calling and structured output
- Vision, audio, and embeddings
- Context limits
- Usage reporting
- Privacy and data residency
- Quality, latency, and cost tiers
- Health, quota, and maintenance state

Every capability is explicitly `Supported`, `Unsupported`, or `Unknown`; omitted capabilities are
invalid. Context-window size and usage-reporting fidelity remain independently unknown when an
endpoint cannot establish them. `ProviderModelMetadata` records quality tier, latency class,
residency regions, privacy class, caller-owned pricing, and deprecation state.

`ProviderRequirements` declares required capabilities, minimum context size, and minimum usage
fidelity. `ProviderCompatibility.evaluate` rejects unknown and unsupported capabilities
distinctly and also rejects unhealthy, unready, quota-constrained, draining, or retired
endpoints. Unknown never implies support.

Production hosts activate providers with `ProviderFactory.activateEndpointAsync` and a
`ProviderEndpointConfiguration`. The configuration may return a fixed validated observation or
probe the endpoint; model names are never inspected to infer support. `ProviderEndpoint` refreshes
the observation before invocation and prevents incompatible requests from reaching the adapter.
Activation and semantic observation changes produce content-free `ProviderCapabilityAudit`
records before the new observation becomes active.

## Normalized semantics

`CompletionResult` is the provider-independent terminal contract. It contains content, a typed
`ProviderFinishReason`, normalized `ProviderUsage`, canonical tool calls, optional structured
output JSON, and an extension map. Common consumers use only these fields and ignore unknown
extension keys. Adapters preserve actual provider metadata in the extension map instead of adding
provider-specific fields to the common contract.

`ProviderUsage` records reporting fidelity explicitly:

- `Full` has separate input and output counts.
- `Partial` has one or both counts and preserves which values are missing.
- `Aggregate` has only a total.
- `None` means the provider reported no usage.

`ProviderUsage.total` returns a value only when a trustworthy total can be calculated. Negative
counts and a partial report with no counts are invalid. Consumers that require input/output
accounting, including claim-support evaluation, require `Full` rather than guessing a split.

`ProviderErrorCategory` defines authentication, authorization, quota, rate limit, timeout,
overload, invalid request, content rejection, protocol, and server failure. Provider-boundary
`PlatformFailure` values carry this category in addition to the broader platform category and
their retryable flag. HTTP bodies are never copied into failures because they can echo prompts or
other sensitive provider content. Generic resilience honors structured retryability and never
retries cancellation or a normalized permanent failure based on message text.

Streaming has an explicit terminal state. A successful final chunk contains
`Completed finishReason`. If content was emitted before a protocol or provider failure, the
adapter first emits `Partial providerErrorCategory` and then raises the normalized failure. A
partial response therefore cannot resemble successful completion. Caller cancellation is
re-thrown unchanged; provider or internal timeout is normalized as `Timeout`.

The OpenAI-compatible, Anthropic, and Ollama adapters share conformance assertions proving that
buffered and streaming calls produce the same terminal content, finish reason, usage, tool calls,
structured output, and extensions. Contract tests separately cover all usage fidelities, tool
calls, structured output, extensions, cancellation, timeout, HTTP categories, and partial-stream
failure.

## Routing and resilience

`ProviderRoutingRequest` is the immutable input to model selection. It identifies a policy version
and deterministic selection key and combines capability requirements with allowed quality tiers,
latency classes, privacy classes, residency regions, and optional token-price ceilings. Empty
constraint sets are invalid rather than meaning unrestricted.

`ProviderRouter` supports closed static-priority policies and deterministic weighted policies.
Weighted selection uses a stable SHA-256 score over the policy version, selection key, endpoint,
and model; it does not depend on input enumeration order or process-randomized hashes. Endpoints
omitted by a policy are excluded rather than appended as an implicit fallback.

Every `ProviderRoutingDecision` contains:

- The policy version and selected endpoint/model.
- The complete ordered list of eligible fallback endpoints.
- Every considered endpoint and its typed compatibility, availability, quality, latency, privacy,
  residency, cost, or policy exclusions.

`routeAndRecordAsync` requires the content-free decision audit to complete before returning the
selection. Every fallback candidate has passed the same mandatory constraints as the primary, so
fallback cannot weaken privacy, residency, capability, or price requirements.

`ProviderScheduler` applies explicit endpoint, endpoint/model, and tenant concurrency limits after
routing. Admission queues have total and per-tenant bounds. A tenant cannot consume another
tenant's configured concurrency or fill the global queue beyond its own queue allowance.
Round-robin dispatch starts after the most recently admitted tenant, preventing a continuously
queued tenant from winning each newly available slot. Limited and exhausted endpoints retain
bounded queued work but dispatch nothing until their structured rate-limit state returns to
available. Cancellation removes queued work from admission accounting.

Optional hedging is planned by `ProviderHedgePlan`. A bounded policy specifies the maximum number
of additional requests, delay, and maximum incremental estimated cost. Only already eligible
routing candidates can be hedged, and candidates that would exceed either bound are omitted.

## Provider resilience and failover

`ProviderCircuitRegistry` isolates circuit state by explicit `ProviderFailureDomain`, not merely by
provider type. A regional endpoint can open without changing the state of another region, model,
or endpoint. Circuits admit one half-open probe after their configured interval and only retryable
provider failures contribute to opening them.

`ProviderRetryBudget` is shared across nested provider operations. It enforces both a maximum
attempt count and an absolute task deadline, so inner retries cannot reset the outer budget.
`ProviderResilience.executeAsync` consumes this budget across same-endpoint retries and the
ordered, constraint-safe fallback endpoints produced by routing.

Each attempt failure explicitly records:

- Its normalized `PlatformFailure`.
- Optional provider retry-after guidance.
- Whether no response or a partial response was observed.

Retry delay is the greater of bounded exponential delay and provider guidance. If that delay
cannot fit inside the task deadline, the executor does not violate the guidance by retrying early;
it moves to the next eligible endpoint. Permanent failures stop immediately. A partial response
also stops immediately and returns `PartialResponse`; it is never retried, concatenated with, or
silently replaced by fallback output.

`ProviderScheduler` has a separate endpoint admission state. Draining prevents new or queued
admission while allowing leases for in-flight work to finish. Returning the endpoint to active
resumes bounded queued work.

`ProviderCacheKey` allows only explicitly eligible deterministic completions or embeddings. Keys
include owner, endpoint, model, operation kind, input digest, and configuration digest.
`ProviderCache` stores typed values with explicit creation and expiration timestamps. Arbitrary
chat completion requests cannot be labeled through an unrestricted string operation.

The older generic retry and circuit helpers remain available for non-provider agent operations.
Provider calls use the structured failure, progress, budget, and failure-domain contracts above.

## Configuration and secrets

Provider configuration stores an optional typed `ApiKeyReference`, never raw credential text.
`ProviderFactory.activateAsync` accepts an optional host-owned `SecretProvider`. It resolves only
the reference declared by the selected configuration and requires resolved material to identify
that exact reference. Activation itself resolves nothing. Before every buffered or streaming
request, the wrapper resolves current material, creates an operation-local client, applies the
authentication header, executes the request, and disposes the client. Missing, mismatched, and
unexpected credentials fail closed. Resolutions identify both the provider-adapter version and an
opaque secret version. Explicit revoked lookups fail with `SecretRevokedException`
before an HTTP client is created. The in-memory adapter supports owner-partitioned,
compare-and-swap rotation and revocation with non-secret mutation evidence; stale or cross-owner
updates fail without changing state. Because every operation resolves again, an already activated
provider observes the next version or revocation without reactivation and unrelated sessions keep
running. The short-lived `SecretValue` wrapper renders as
`[REDACTED]`. All factory-created providers, including local providers with no credential
reference, redact secrets already known to the active execution from prompts before transport and
from buffered results, streaming chunks, finish reasons, and structured failures before returning
them. Local providers require no secret provider.

## Provider configuration lifecycle

`ProviderRuntimeConfiguration` is an immutable, versioned deployment configuration containing a
validated `ProviderType`, capability endpoint configuration, and tenant/workspace model policy.
Provider URLs must be absolute HTTP or HTTPS endpoints, model identities must be canonical,
timeouts and provider-specific token limits must be positive, and local reasoning settings must
be canonical.

`ProviderConfigurationControlPlane` activates a candidate independently and refreshes its
capability observation before any state change. Activation requires:

- The observed model identity to match the configured model.
- Healthy and ready endpoint state.
- Available quota.
- Active rather than draining or maintenance state.

An invalid candidate is disposed, receives a typed rejection audit, and cannot replace the
last-known-good configuration. For a valid candidate, the content-free administrative audit must
complete before the atomic current-version swap.

Reload and rollback are serialized. During the short audit-and-swap boundary, new operations
wait; already registered operations continue on the endpoint version they acquired. The audit
identifies affected execution, tenant, workspace, and optional session identities without prompt,
response, or credential content. The old endpoint remains as the previous known-good version for
rollback. Endpoints retired by a later reload are disposed only after their in-flight operation
set reaches zero.

`ProviderModelPolicy` is fail-closed by tenant and workspace. A missing scope rule, denied model,
or model absent from an explicit allowlist fails before provider invocation. Policy and health
checks are independent of secret lookup. Hosted providers continue resolving their configured
secret reference for every operation, so rotation or revocation takes effect without reloading
unrelated endpoints or sessions.

## Orleans runtime

`Nao.Runtime.Orleans` currently supplies:

- `SessionGrain` for persisted session execution
- `SessionDirectoryGrain` for session discovery
- `WorkspaceRegistry` for compiled workspace definitions
- Conversation/session persistence integrations
- Workspace selection and switching

```fsharp
let registry =
    WorkspaceRegistry.fromWorkspaces [
        ("team-a",
         { WorkspaceDefinitions.Empty with
             Agents = [ teamAAgent ]
             Tools = teamATools })
        ("team-b",
         { WorkspaceDefinitions.Empty with
             Agents = [ teamBAgent ]
             Tools = teamBTools })
    ]
```

Agents and tools are compiled registrations. The runtime does not dynamically execute JSON definitions or load arbitrary untrusted assemblies.

## Sessions and state

Runtime-owned state keeps agents stateless per call:

1. Resolve the session and workspace.
2. Load persisted conversation, grants, and runtime state.
3. Construct the execution context.
4. Run the harness and selected agent.
5. Persist a committed outcome.

Production completion requires explicit semantics for idempotency, concurrent turns, checkpoints, retries, deletion, archival, migration, and restart recovery.

## Tenancy status

Session records contain grouping metadata, but the organizational `GroupDirectoryGrain` described by older documentation is not present in current source. Group membership, roles, quotas, lifecycle, and authorization therefore remain planned rather than advertised current capabilities.

Tenant, user, workspace, session, and execution identity must eventually be enforced at grain entry points, storage partitions, retrieval boundaries, telemetry, and administration.

## Workspace lifecycle

A production workspace lifecycle needs:

- Immutable version identity
- Validation before registration
- Agent, tool, provider, policy, and schema compatibility
- Staged rollout, canary, promotion, rollback, and retirement
- Session pinning or explicit migration
- Safe handling of active sessions using older versions

Any future extension discovery must include signature, trust, compatibility, dependency, and isolation policies. Arbitrary assembly loading into the runtime process is not a safe plugin model.

## Cluster operations

Multi-silo production support requires tests and guidance for:

- Placement and tenant affinity
- Silo failure and rolling restart
- Network partition and storage degradation
- Admission control and backpressure
- Grain activation and queue saturation
- Scaling signals
- Recovery objectives
- Upgrade compatibility

## Administration

Provider, workspace, policy, quota, session, and health administration belongs to an authenticated control plane. Mutations require authorization, optimistic concurrency, dry-run validation, impact previews, propagation across silos, and audit evidence.

## Roadmap

See [Providers and distributed runtime](roadmap/04-providers-runtime.md) for detailed model control-plane, tenancy, durability, workspace lifecycle, cluster, and administration tasks.
