# Providers and Distributed Runtime

This workstream turns individual provider adapters and Orleans grains into a resilient model-control plane and production multi-tenant runtime.

**Milestone:** R3
**Dependencies:** Foundations, harness/governance, and observability
**Primary owners:** `Nao.Providers`, `Nao.Runtime.Orleans`, host integrations

## Existing baseline

- [x] A common LLM provider abstraction supports completion and optional streaming.
- [x] OpenAI-compatible, Anthropic, DeepSeek, Kimi, Ollama, vLLM, and llama.cpp adapters exist.
- [x] Orleans sessions, session directory, workspace registry, and persisted state exist.
- [x] Multiple compiled workspaces can be hosted in one silo.
- [ ] Providers are pooled and selected through a capability- and policy-aware control plane.
- [ ] Multi-silo tenant isolation, upgrade, failover, and operations are comprehensively defined.

## PRV-01 — Provider capability model

- [x] Define capabilities for chat, streaming, tool calling, structured output, vision, audio, embeddings, context size, and usage reporting.
- [x] Define model metadata for quality tier, latency class, data residency, privacy, price, and deprecation.
- [x] Probe or configure endpoint capabilities without relying on model-name heuristics alone.
- [x] Version capability observations and configuration.
- [x] Expose health, readiness, quota, and maintenance status.
- [x] Distinguish temporary endpoint failure from unsupported capability.

**Acceptance criteria**

- [x] Requests can declare required capabilities and reject incompatible models before invocation.
- [x] Capability changes are observable and audited.
- [x] Unknown capability does not silently imply support.

## PRV-02 — Normalized provider semantics

- [x] Define a common error taxonomy: authentication, authorization, quota, rate limit, timeout, overload, invalid request, content rejection, protocol, and server failure.
- [x] Normalize finish reasons and partial-stream termination.
- [x] Standardize tool-call and structured-output representation.
- [x] Preserve provider-specific metadata in an extension field.
- [x] Define cancellation and timeout behavior.
- [x] Define usage reporting when providers supply full, partial, aggregate, or no token data.
- [x] Add conformance tests shared by every adapter.

**Acceptance criteria**

- [x] Equivalent buffered and streaming requests produce equivalent final semantic results.
- [x] Retry logic uses normalized error categories rather than fragile message matching.
- [x] Provider-specific additions do not break common consumers.

## PRV-03 — Routing and scheduling

- [x] Define a model-routing request with capabilities, policy, quality, latency, cost, privacy, residency, and availability constraints.
- [x] Implement static and weighted routing.
- [x] Add per-provider and per-model concurrency limits.
- [x] Add rate-limit-aware queues and backpressure.
- [x] Add tenant quotas and fair scheduling.
- [x] Support quality-, latency-, cost-, and residency-aware policies.
- [x] Record routing candidates, exclusions, decision, and policy version.
- [x] Prevent fallback from weakening privacy or capability requirements.
- [x] Support optional request hedging with strict cost controls.

**Acceptance criteria**

- [x] Routing is deterministic for a pinned policy and health snapshot where required.
- [x] One noisy tenant cannot exhaust shared provider capacity.
- [x] Fallback never selects a model violating mandatory constraints.

## PRV-04 — Resilience and failover

- [x] Maintain circuit state per endpoint and failure domain.
- [x] Define retry budgets across nested provider calls.
- [x] Honor provider retry-after guidance.
- [x] Add controlled fallback chains.
- [x] Distinguish safe pre-response retries from ambiguous partial-stream retries.
- [x] Support draining endpoints during maintenance or deployment.
- [x] Add cache contracts for eligible deterministic or embedding requests.
- [x] Test regional and multi-endpoint outage scenarios.

**Acceptance criteria**

- [x] One endpoint failure does not take down unrelated models or sessions.
- [x] Retries and fallback remain inside task budget.
- [x] Partial responses cannot be silently concatenated with fallback responses.

## PRV-05 — Provider configuration and secrets

- [x] Define validated, versioned provider configuration.
- [x] Store secret references rather than secret values.
- [x] Support controlled runtime reload and rollback.
- [x] Drain or migrate in-flight work during incompatible changes.
- [x] Audit administrative changes without logging credentials.
- [x] Add configuration health checks before activation.
- [x] Define model allow/deny policy by tenant and workspace.

**Acceptance criteria**

- [x] Invalid configuration cannot replace the last known-good configuration.
- [x] Secret rotation does not expose values or require unrelated runtime restart.
- [x] Configuration changes identify affected executions and sessions.

## RUN-01 — Runtime tenancy model

- [x] Define tenant, group, user, workspace, session, and execution hierarchy.
- [x] Implement or remove/document the currently advertised group-directory capability.
- [x] Define group membership, roles, default workspace, quotas, and lifecycle.
- [x] Enforce tenant/workspace scope in grain keys and storage partitions.
- [x] Validate authorization at grain entry points and downstream resource access.
- [x] Prevent caller-controlled IDs from selecting another tenant's grain.
- [x] Add cross-tenant isolation and enumeration tests.

**Acceptance criteria**

- [x] Every persisted state object has an explicit tenant and owner scope.
- [x] Cross-tenant access fails before protected state is loaded or disclosed.
- [x] Group behavior in documentation matches implemented source and tests.

## RUN-02 — Session durability and concurrency

- [x] Define turn idempotency and duplicate-request behavior.
- [x] Define grain reentrancy and concurrent-message policy.
- [x] Persist turn intent and terminal outcome atomically or through recoverable state transitions.
- [x] Resume interrupted harness execution safely.
- [x] Define session retention, archival, deletion, and legal-hold behavior.
- [x] Persist grants, memory references, artifacts, and trace links consistently.
- [x] Add optimistic concurrency or sequence checks where external stores participate.
- [x] Test activation, deactivation, restart, and reminder/timer behavior.

**Acceptance criteria**

- [x] Retried requests cannot duplicate turns or durable side effects.
- [x] Sessions survive silo restart without losing committed state.
- [x] Deletion removes or tombstones every owned record according to policy.

## RUN-03 — Workspace lifecycle

- [x] Define immutable workspace version identity and compatibility metadata.
- [x] Validate agents, tools, providers, policies, schemas, and migrations before registration.
- [x] Support staged rollout, canary, promotion, rollback, and retirement.
- [x] Pin sessions to a workspace version or define explicit migration.
- [x] Prevent retired code from becoming unavailable while active sessions still require it.
- [x] Audit registration and activation changes.
- [x] Define safe extension discovery without arbitrary untrusted assembly loading.

**Acceptance criteria**

- [x] Workspace rollback restores a known compatible execution environment.
- [x] Existing sessions have deterministic behavior during version changes.
- [x] Invalid registrations never become routable.

## RUN-04 — Cluster reliability and scale

- [x] Define supported clustering and persistence configurations.
- [x] Add multi-silo integration tests.
- [x] Define placement for tenant affinity, locality, resource class, and isolation.
- [x] Test silo failure, rolling restart, network partition, and storage degradation.
- [x] Add queue and grain activation backpressure.
- [x] Define overload admission control.
- [x] Add capacity metrics and scaling signals.
- [x] Document single-region recovery objectives and optional multi-region strategy.

**Acceptance criteria**

- [x] Supported failures do not lose acknowledged turns or violate tenant boundaries.
- [x] Overload produces bounded queues and explicit rejection rather than collapse.
- [x] Rolling upgrades preserve supported session compatibility.

## RUN-05 — Administration and control plane

- [x] Define authenticated APIs for provider, workspace, policy, quota, session, and health administration.
- [x] Separate control-plane authorization from ordinary agent execution.
- [x] Add optimistic concurrency and audit evidence to mutations.
- [x] Support dry-run validation and impact previews.
- [x] Add emergency disablement for models, tools, agents, connectors, and tenants.
- [x] Ensure administrative changes propagate consistently across silos.

### Exit criteria for providers and runtime

- [x] PRV-01 through PRV-05 are complete for supported providers.
- [x] RUN-01 through RUN-05 are complete for supported Orleans deployment modes.
- [x] Outage, overload, restart, upgrade, and cross-tenant tests pass.
- [x] Provider and runtime operations are observable and auditable.

[Back to roadmap](../roadmap.md)
