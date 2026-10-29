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
- tenant/workspace/user-scoped `SessionDirectoryGrain` for session discovery
- tenant-scoped `GroupDirectoryGrain` for organizational membership and governance
- tenant-partitioned `WorkspaceCatalog` for immutable compiled workspace versions
- Conversation/session persistence integrations
- canonical tenant/workspace/user/session grain addresses

```fsharp
let tenantId = TenantId.parse "tenant-a"

let dependencies =
    [ { Kind = WorkspaceDependencyKind.Provider
        Id = "openai-primary"
        Version = "2026.10" }
      { Kind = WorkspaceDependencyKind.Policy
        Id = "default-model-policy"
        Version = "1" } ]

let availableDependencies =
    dependencies
    |> List.map (fun dependency -> dependency.Kind, dependency.Id, dependency.Version)
    |> Set.ofList

let catalog = WorkspaceCatalog.create availableDependencies
let identity = WorkspaceVersionId.create "team-a" "2026.1"

let registration =
    WorkspaceRegistration.create
        identity
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        "1"
        dependencies
        { WorkspaceDefinitions.Empty with
            Agents = [ teamAAgent ]
            Tools = teamATools }

catalog.Register(tenantId, registration)
```

Agents and tools are compiled registrations. Each registration has a canonical `<name>@<version>`
identity, an exact SHA-256 content digest, runtime and state-schema compatibility, and declared
provider, policy, schema, migration, and worker-extension dependencies. Registration validates
these contracts and rejects duplicate agent or tool identities before the version enters the
catalog. Catalog entries cannot be replaced or removed, so retirement cannot make code unavailable
to a pinned session. The runtime does not dynamically execute JSON definitions or load arbitrary
untrusted assemblies.

Workspace version identity is immutable within a session because it is part of the grain key;
changing versions creates a differently addressed session rather than mutating the existing grain.

## Sessions and state

Runtime-owned state keeps agents stateless per call:

1. Resolve the session and workspace.
2. Validate the caller-supplied stable turn identity and request fingerprint.
3. Persist an accepted turn intent before any governed execution.
4. Reconcile the external transcript and recover any terminal harness outcome.
5. Run the harness only when no durable outcome exists.
6. Persist the terminal outcome, transcript, lineage references, and session transition.

`ExecuteTurnAsync` is the only turn-execution entry point. A retry must reuse the same `TurnId`,
input, conversation, agent, attachments, and correlation. Reusing a turn ID with changed content is
rejected. A completed turn returns its persisted result without invoking providers or tools again.
The grain is non-reentrant for mutation; only `GetLiveStepsAsync` is explicitly interleavable.

The harness writes a redacted terminal outcome keyed by its stable idempotency key before returning.
After activation, a formerly running turn becomes interrupted. Its next identical request first
recovers that outcome; when no terminal outcome exists, existing side-effect journals decide whether
re-execution is safe or must be quarantined.

Session state records monotonic turn sequence, permission-grant evidence, artifact identities, and
trace execution identities. Memory, grants, artifacts, traces, transcripts, and journals remain
partitioned by the same authenticated owner scope. Conversation and execution-journal envelopes
carry contiguous sequence numbers; stale conversation commits fail explicitly.

Lifecycle is explicit: active, paused, or archived. Hosts configure archive and deletion
eligibility timestamps, invoke `EvaluateLifecycleAsync` from their durable scheduler, and can place
or clear a persisted legal hold. Activation also applies overdue archival. Archived sessions cannot
resume or execute, and destruction fails until the deletion timestamp is reached and no legal hold
exists. Nao deliberately does not register a destructive Orleans reminder: scheduler retries remain
host-observable and deletion still requires an authenticated grain call.

## Tenancy status

Runtime grain keys are version-1 addresses whose components are independently Base64URL encoded.
Session and directory workspace components must be canonical `<name>@<version>` identities:

- Session: `v1/<tenant>/<workspace-version>/<user>/<session>`
- Session directory: `v1/<tenant>/<workspace-version>/<user>`
- Group directory: `v1/<tenant>/<group>`
- Workspace lifecycle route: `v1/<tenant>/<workspace-name>`

The runtime accepts only the exact canonical shape; there is no legacy `user/session` alias.
Session and directory entry points derive authorization from the grain address and the
host-authenticated principal. Persisted session roots, conversations, messages, directory state,
directory entries, groups, members, and conversation streams carry explicit owner fields and are
validated against that address before protected state is returned.

`GroupDirectoryGrain` implements owner, administrator, and member roles; active, suspended, and
retired lifecycle states; an explicit default workspace version; and positive session, concurrent
execution, and monthly-token quota definitions. Group-scoped session startup requires both
authenticated group membership and a matching persisted active membership. Runtime consumption
enforcement for the quota definitions belongs to scheduling and durable execution work.

## Workspace lifecycle

The workspace lifecycle consists of an immutable compiled catalog and a durable tenant-scoped
`WorkspaceLifecycleGrain` route. Together they provide:

- Canonical immutable version identity and content digest
- Runtime, state-schema, and provider/policy/schema/migration/worker dependency metadata
- Validation against a host-supplied dependency manifest before catalog registration
- Exact session resolution of retained versions
- Code-defined or explicitly addressed process-worker extensions only
- Staged rollout, canary, promotion, rollback, and retirement
- Deterministic canary selection from a stable session stickiness key
- Optimistic route revisions for every mutation
- Rollback only to a previously active, retained version
- Durable lifecycle audit delivery through a persisted idempotent outbox

Lifecycle mutation requires the tenant-scoped `nao.workspace.manage` role and multi-factor or
phishing-resistant authentication. Invalid or unavailable catalog registrations cannot enter a
route. Promotion changes only routing for new session addresses: existing sessions keep the exact
workspace version embedded in their key. Retirement removes a version from routing eligibility but
never removes its compiled catalog entry, so pinned sessions retain their execution environment.

Nao has no assembly-scanning plugin path. Extensions are trusted host-compiled values or stable
process-worker references covered by an exact validated worker dependency. Any future external
bundle discovery must additionally enforce signature, publisher trust, compatibility, dependency,
and isolation policies before it can produce such a registration.

## Cluster operations

`RuntimeClusterProfile` recognizes two fail-closed operating profiles:

- `DevelopmentSingleSilo`: exactly one localhost silo with in-memory grain storage. This profile is
  disposable and carries no durability or availability guarantee.
- `ProductionSingleRegion`: at least three silos with ADO.NET membership and ADO.NET grain storage.
  The host must supply a region, deployment compatibility identity, and explicit maximum data-loss
  and recovery-time objectives. A rolling upgrade cannot change the operating mode, region, or
  deployment compatibility identity.

No memory-storage or localhost-membership combination is accepted as production. The production
profile assumes a highly available database endpoint, provider-supported backups, tested restore,
and connection-pool health integrated into readiness.

`RuntimePlacement` filters unhealthy or full silos before considering placement. Requests declare
tenant identity, preferred region and zone, resource class, shared or dedicated-tenant isolation,
and deployment compatibility. A dedicated request can select only a silo assigned to that tenant.
Among compatible candidates, utilization is considered before deterministic tenant affinity.

`RuntimeAdmissionController` bounds session activations and turn executions independently, with
global and per-tenant active and queued capacities. Queues dispatch tenants round-robin. Full global
or tenant queues return retryable structured resource-exhaustion failures; cancellation releases
queued capacity. Session grains hold an activation lease for their activation lifetime and a turn
lease only while new execution is required. Terminal replay and durable outcome recovery do not
consume provider execution capacity.

Membership loss, durable-storage degradation, and rolling-drain state reject new work explicitly
instead of accepting work that cannot be committed. Admission snapshots produce standard Orleans
grain saturation signals for active, queued, capacity, and rejection counts. A scaling
recommendation requests scale-out at 80 percent active utilization, whenever work is queued, or
after any rejection; scale-in requires every pool below 25 percent with no queue.

The test cluster loads the real generated Orleans contract manifest. Two-silo integration tests
kill the silo hosting a tenant-keyed health activation and require that identity to reactivate on
the survivor. A separate rolling-restart test stops and restarts the serving silo and verifies
tenant routing afterward. Unit tests cover placement isolation, compatibility, bounded overload,
round-robin tenant dispatch, cancellation, membership loss, storage degradation, drain behavior,
and rolling-upgrade compatibility.

Single-region recovery preserves acknowledged turns through the RUN-02 accepted-intent, external
terminal-outcome, sequenced transcript, and terminal-ledger protocol. Recovery objectives are
deployment inputs rather than universal constants because database topology and backup cadence
determine the achievable values. Operators must qualify their configured objectives with restore
and failover drills.

Multi-region is optional and currently supports only an operator-designed active/passive strategy:
replicate the membership-independent durable stores and database backups, fence the failed write
region, restore or promote storage, then start a new single-region cluster with the same deployment
compatibility identity. Concurrent multi-region writers, cross-region Orleans membership, and
automatic conflict resolution are not supported.

## Administration

`RuntimeAdministrationGrain` is the tenant-keyed control-plane boundary for provider, workspace,
policy, quota, knowledge, evaluation, operation, session, and health administration. Hosts own UI and transport authentication, then
pass the resulting `SecurityPrincipal`; agent `AuthorizationScope` is deliberately not accepted.
Every call is tenant-scoped. Mutations reject impersonation and require multi-factor or
phishing-resistant authentication plus either `nao.control.admin` or the resource-specific
`nao.control.<kind>.manage` role.

Administrative requests carry a caller-stable operation ID, resource identity, action, reason,
expected revision, and canonical non-secret parameters. `PreviewAsync` validates the same request and
returns current and target revisions, affected resource identities, and warnings without changing
state. `MutateAsync` rejects stale revisions. Reusing an operation ID with changed content also
fails. Parameter values may contain secret references but raw password, token, API-key, or secret
fields are rejected.

Before invoking a host adapter, the grain persists a pending operation and its impact. Adapters
must use the operation ID idempotently and acknowledge both application and propagation to all
silo-facing observers. A failure leaves the operation pending for retry on the same call or grain
reactivation. Only after apply, propagation, and durable idempotent audit recording does the grain
publish the target resource revision. Audit evidence records actor, reason, prior and new
revisions, exact affected resource identities, parameter names, and warning counts, never
parameter values.

Models, tools, agents, connectors, and tenants support only explicit `disable` and `enable`
mutations and additionally require `nao.control.emergency`. Disablement is persisted in the
tenant grain, so every silo reads one authoritative state. Session execution checks tenant, agent,
and resolved tool disablement before invoking new work. Provider and connector host adapters must
perform the equivalent model or connector check at their transport boundary. Terminal turn replay
does not rerun work and remains available during disablement.

`RuntimeAdministrationClient` is the reference principal-carrying client over a host-supplied
authenticated transport. See [Configuration and administration](configuration-administration.md)
for the immutable/reloadable boundary, last-known-good activation, client flow, and host
responsibilities.

## Roadmap

See [Providers and distributed runtime](roadmap/04-providers-runtime.md) for detailed model control-plane, tenancy, durability, workspace lifecycle, cluster, and administration tasks.
