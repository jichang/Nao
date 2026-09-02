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

- [ ] Define capabilities for chat, streaming, tool calling, structured output, vision, audio, embeddings, context size, and usage reporting.
- [ ] Define model metadata for quality tier, latency class, data residency, privacy, price, and deprecation.
- [ ] Probe or configure endpoint capabilities without relying on model-name heuristics alone.
- [ ] Version capability observations and configuration.
- [ ] Expose health, readiness, quota, and maintenance status.
- [ ] Distinguish temporary endpoint failure from unsupported capability.

**Acceptance criteria**

- [ ] Requests can declare required capabilities and reject incompatible models before invocation.
- [ ] Capability changes are observable and audited.
- [ ] Unknown capability does not silently imply support.

## PRV-02 — Normalized provider semantics

- [ ] Define a common error taxonomy: authentication, authorization, quota, rate limit, timeout, overload, invalid request, content rejection, protocol, and server failure.
- [ ] Normalize finish reasons and partial-stream termination.
- [ ] Standardize tool-call and structured-output representation.
- [ ] Preserve provider-specific metadata in an extension field.
- [ ] Define cancellation and timeout behavior.
- [ ] Define usage reporting when providers supply full, partial, aggregate, or no token data.
- [ ] Add conformance tests shared by every adapter.

**Acceptance criteria**

- [ ] Equivalent buffered and streaming requests produce equivalent final semantic results.
- [ ] Retry logic uses normalized error categories rather than fragile message matching.
- [ ] Provider-specific additions do not break common consumers.

## PRV-03 — Routing and scheduling

- [ ] Define a model-routing request with capabilities, policy, quality, latency, cost, privacy, residency, and availability constraints.
- [ ] Implement static and weighted routing.
- [ ] Add per-provider and per-model concurrency limits.
- [ ] Add rate-limit-aware queues and backpressure.
- [ ] Add tenant quotas and fair scheduling.
- [ ] Support quality-, latency-, cost-, and residency-aware policies.
- [ ] Record routing candidates, exclusions, decision, and policy version.
- [ ] Prevent fallback from weakening privacy or capability requirements.
- [ ] Support optional request hedging with strict cost controls.

**Acceptance criteria**

- [ ] Routing is deterministic for a pinned policy and health snapshot where required.
- [ ] One noisy tenant cannot exhaust shared provider capacity.
- [ ] Fallback never selects a model violating mandatory constraints.

## PRV-04 — Resilience and failover

- [ ] Maintain circuit state per endpoint and failure domain.
- [ ] Define retry budgets across nested provider calls.
- [ ] Honor provider retry-after guidance.
- [ ] Add controlled fallback chains.
- [ ] Distinguish safe pre-response retries from ambiguous partial-stream retries.
- [ ] Support draining endpoints during maintenance or deployment.
- [ ] Add cache contracts for eligible deterministic or embedding requests.
- [ ] Test regional and multi-endpoint outage scenarios.

**Acceptance criteria**

- [ ] One endpoint failure does not take down unrelated models or sessions.
- [ ] Retries and fallback remain inside task budget.
- [ ] Partial responses cannot be silently concatenated with fallback responses.

## PRV-05 — Provider configuration and secrets

- [ ] Define validated, versioned provider configuration.
- [ ] Store secret references rather than secret values.
- [ ] Support controlled runtime reload and rollback.
- [ ] Drain or migrate in-flight work during incompatible changes.
- [ ] Audit administrative changes without logging credentials.
- [ ] Add configuration health checks before activation.
- [ ] Define model allow/deny policy by tenant and workspace.

**Acceptance criteria**

- [ ] Invalid configuration cannot replace the last known-good configuration.
- [ ] Secret rotation does not expose values or require unrelated runtime restart.
- [ ] Configuration changes identify affected executions and sessions.

## RUN-01 — Runtime tenancy model

- [ ] Define tenant, group, user, workspace, session, and execution hierarchy.
- [ ] Implement or remove/document the currently advertised group-directory capability.
- [ ] Define group membership, roles, default workspace, quotas, and lifecycle.
- [ ] Enforce tenant/workspace scope in grain keys and storage partitions.
- [ ] Validate authorization at grain entry points and downstream resource access.
- [ ] Prevent caller-controlled IDs from selecting another tenant's grain.
- [ ] Add cross-tenant isolation and enumeration tests.

**Acceptance criteria**

- [ ] Every persisted state object has an explicit tenant and owner scope.
- [ ] Cross-tenant access fails before protected state is loaded or disclosed.
- [ ] Group behavior in documentation matches implemented source and tests.

## RUN-02 — Session durability and concurrency

- [ ] Define turn idempotency and duplicate-request behavior.
- [ ] Define grain reentrancy and concurrent-message policy.
- [ ] Persist turn intent and terminal outcome atomically or through recoverable state transitions.
- [ ] Resume interrupted harness execution safely.
- [ ] Define session retention, archival, deletion, and legal-hold behavior.
- [ ] Persist grants, memory references, artifacts, and trace links consistently.
- [ ] Add optimistic concurrency or sequence checks where external stores participate.
- [ ] Test activation, deactivation, restart, and reminder/timer behavior.

**Acceptance criteria**

- [ ] Retried requests cannot duplicate turns or durable side effects.
- [ ] Sessions survive silo restart without losing committed state.
- [ ] Deletion removes or tombstones every owned record according to policy.

## RUN-03 — Workspace lifecycle

- [ ] Define immutable workspace version identity and compatibility metadata.
- [ ] Validate agents, tools, providers, policies, schemas, and migrations before registration.
- [ ] Support staged rollout, canary, promotion, rollback, and retirement.
- [ ] Pin sessions to a workspace version or define explicit migration.
- [ ] Prevent retired code from becoming unavailable while active sessions still require it.
- [ ] Audit registration and activation changes.
- [ ] Define safe extension discovery without arbitrary untrusted assembly loading.

**Acceptance criteria**

- [ ] Workspace rollback restores a known compatible execution environment.
- [ ] Existing sessions have deterministic behavior during version changes.
- [ ] Invalid registrations never become routable.

## RUN-04 — Cluster reliability and scale

- [ ] Define supported clustering and persistence configurations.
- [ ] Add multi-silo integration tests.
- [ ] Define placement for tenant affinity, locality, resource class, and isolation.
- [ ] Test silo failure, rolling restart, network partition, and storage degradation.
- [ ] Add queue and grain activation backpressure.
- [ ] Define overload admission control.
- [ ] Add capacity metrics and scaling signals.
- [ ] Document single-region recovery objectives and optional multi-region strategy.

**Acceptance criteria**

- [ ] Supported failures do not lose acknowledged turns or violate tenant boundaries.
- [ ] Overload produces bounded queues and explicit rejection rather than collapse.
- [ ] Rolling upgrades preserve supported session compatibility.

## RUN-05 — Administration and control plane

- [ ] Define authenticated APIs for provider, workspace, policy, quota, session, and health administration.
- [ ] Separate control-plane authorization from ordinary agent execution.
- [ ] Add optimistic concurrency and audit evidence to mutations.
- [ ] Support dry-run validation and impact previews.
- [ ] Add emergency disablement for models, tools, agents, connectors, and tenants.
- [ ] Ensure administrative changes propagate consistently across silos.

### Exit criteria for providers and runtime

- [ ] PRV-01 through PRV-05 are complete for supported providers.
- [ ] RUN-01 through RUN-05 are complete for supported Orleans deployment modes.
- [ ] Outage, overload, restart, upgrade, and cross-tenant tests pass.
- [ ] Provider and runtime operations are observable and auditable.

[Back to roadmap](../roadmap.md)
