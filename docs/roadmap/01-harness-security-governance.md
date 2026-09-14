# Harness, Security, and Governance

This workstream makes the ETCLOVG harness the single reliable execution path and turns security configuration into enforced isolation and auditable authorization.

**Milestone:** R1
**Dependencies:** Foundations and contracts
**Primary owners:** `Nao.Agents`, persistence adapters, host integrations

## Existing baseline

- [x] ETCLOVG harness composes execution, tools, context, lifecycle, observability, verification, and governance.
- [x] Resource-limit and sandbox configuration contracts exist.
- [x] Resource permissions support allow, deny, and ask outcomes.
- [x] Policy, constitution, audit, execution-journal, retry, circuit-breaker, and fallback primitives exist.
- [x] Process and container isolation are enforced by real execution environments.
- [x] Every tool path uses identical production authorization semantics.

## HAR-01 — One execution contract

- [x] Define one immutable execution request containing identity, input, budgets, policy versions, dependency versions, and correlation data.
- [x] Define an execution result containing outputs, artifacts, usage, evidence, policy decisions, and terminal status.
- [x] Route supported production and evaluation agent, tool, delegated-agent, and memory-tool execution through the harness.
- [x] Eliminate authorization bypasses created by permissive default contexts in production paths.
- [x] Define nested budget inheritance for delegated agents and tools.
- [x] Make cancellation and deadlines flow through every call boundary.
- [x] Define terminal states for success, failure, cancellation, timeout, denial, limit exceeded, and indeterminate outcomes.

**Acceptance criteria**

- [x] The same request produces equivalent harness semantics in production and evaluation.
- [x] No supported execution path bypasses governance, limits, or audit when enforcement is enabled.
- [x] Nested work cannot exceed its parent's remaining budget.

Executable replay is introduced and governed as part of HAR-02; persistence event replay only reconstructs stored state and does not execute agents or tools.

## HAR-02 — Durable execution and replay

- [x] Persist state transitions and checkpoints at defined harness boundaries.
- [x] Assign idempotency keys to turns, tool calls, provider calls, and committed artifacts.
- [x] Distinguish retry-safe operations from externally side-effecting operations.
- [x] Persist side-effect intent before execution and outcome after execution.
- [x] Resume interrupted execution from the last safe checkpoint.
- [x] Support deterministic replay with recorded provider/tool fixtures.
- [x] Represent non-replayable dependencies explicitly.
- [x] Add compensation orchestration for reversible tool operations.
- [x] Detect and quarantine ambiguous outcomes after process or network failure.

**Acceptance criteria**

- [x] Restarting after every checkpoint does not duplicate committed side effects.
- [x] A recorded execution can be replayed without live provider access.
- [x] Compensation results are durable, correlated, and auditable.

## HAR-03 — Process isolation

- [x] Implement a process-backed `IExecutionEnvironment`.
- [x] Define the worker protocol and version handshake.
- [x] Run workers under a restricted operating-system identity where supported.
- [x] Validate and apply the process working directory.
- [x] Enforce filesystem allowlists.
- [x] Enforce environment-variable allowlists.
- [x] Resolve secret handles only inside authorized worker boundaries.
- [x] Enforce wall-clock timeout and propagate cancellation.
- [x] Enforce a combined process stdout/stderr byte budget.
- [x] Enforce worker tool-call, token, and cost budgets.
- [x] Enforce a sampled process working-set memory limit.
- [x] Enforce CPU limits on supported operating systems.
- [x] Kill complete child-process trees after timeout, cancellation, or host shutdown.
- [x] Capture bounded stdout/stderr as redacted artifacts.
- [x] Document platform-specific enforcement differences.

**Acceptance criteria**

- [x] A worker cannot read undeclared test files.
- [x] A worker cannot read undeclared environment variables.
- [x] A worker cannot retain child processes after cancellation.
- [x] Every configured limit has a deterministic test and structured result.

## HAR-04 — Container isolation

- [x] Implement a container-backed `IExecutionEnvironment`.
- [x] Pin images by digest and record supply-chain metadata.
- [x] Use non-root users, read-only roots, dropped capabilities, and no privilege escalation by default.
- [x] Define explicit mounts, working directories, devices, and temporary storage.
- [x] Deny network access by default.
- [x] Support destination allowlists through an enforced egress broker.
- [x] Apply CPU, memory, process-count, time, and output limits.
- [x] Scan or attest supported worker images.
- [x] Clean up containers, volumes, and networks after all terminal states.
- [x] Add host capability detection and a clear unsupported result.

**Acceptance criteria**

- [x] Container escape-oriented regression tests run in the appropriate secured CI environment.
- [x] No undeclared mount or network destination is reachable.
- [x] Image and policy identity appear in execution evidence.

## SEC-01 — Identity and security principal

- [x] Define a transport-neutral principal containing tenant, subject, service identity, roles, claims, and authentication strength.
- [x] Propagate the principal through sessions, agents, tools, knowledge retrieval, storage, telemetry, and evaluation.
- [x] Distinguish user authority from agent-delegated authority.
- [x] Define impersonation and service-to-service delegation rules.
- [x] Reject missing or ambiguous tenant context on protected operations.
- [x] Provide host adapters for OIDC/OAuth claims without coupling core packages to one identity provider.

**Acceptance criteria**

- [x] Authorization decisions can identify the original user and every delegation.
- [x] Cross-tenant principal substitution fails closed.
- [x] Authentication details are not leaked into prompts or model-visible context.

**Propagation progress:** `AgentContext.Authorization` carries the complete scope and is rebound at agent, delegation, and tool-dispatch boundaries. The principal retains the original user separately from an optional host-authorized effective user. Impersonation requires authenticated evidence, a distinct user, and a reason; it cannot be initiated from an agent scope. A service-authenticated principal retains its root service and an ordered service lineage. Only the current service can append a distinct service, direct-user scopes cannot introduce services, and service delegation closes before agent execution. The scope also retains an ordered `AgentAuthority` chain. Root binding rejects pre-existing authority; child delegation requires the current agent, rejects self-delegation and execution replay, preserves the principal and ownership identity, and extends rather than replaces the chain. Scope containment requires identical ownership and authentication evidence plus service- and agent-lineage prefixes.

Worker protocol v1 carries impersonation, service, principal, and agent authority separately from model input and rejects missing, malformed, duplicate, or mismatched authority. Evaluation callbacks receive mandatory `EvaluationContext`; evaluation archives plus every current memory store require authenticated ownership for every operation. Memory tools, consolidation, Orleans session memory/deletion, metrics telemetry, live spans, durable execution traces, audit storage, execution journals, turn records, feedback, and conversations propagate that scope, and persistent partitions include the full ownership lineage without authentication evidence. Durable payloads contain neither authentication nor delegation evidence. Automated identity, harness, worker, evaluator, tool, storage, telemetry, and runtime tests cover cross-tenant substitution, prompt privacy, complete ownership isolation, and authority non-escalation.

`Nao.Identity.Oidc` completes the provider-neutral host boundary. It accepts only an already-authenticated `ClaimsIdentity` or a `ClaimsPrincipal` containing exactly one identity. Hosts configure tenant, subject, service, delegation, impersonation, group, role, allowlisted claim, and authentication-strength mappings. Missing, duplicate, malformed, or internally inconsistent authority claims fail closed. Token signature, issuer, audience, lifetime, and revocation validation remain host authentication responsibilities rather than core authorization behavior.

## GOV-01 — Permission enforcement

- [x] Define canonical resources and actions for files, web, tools, agents, memory, knowledge, models, artifacts, and administration.
- [x] Connect `PermissionGate` to a host-owned approval broker contract.
- [x] Implement approve-once, session, workspace, and durable grant scopes.
- [x] Implement expiry and revocation.
- [x] Make deny precedence and wildcard matching explicit and tested.
- [x] Enforce permissions through local tools, MCP tools, delegated agents, and retrieval adapters.
- [x] Ensure prompts cannot grant permissions.
- [x] Record the exact policy and grant versions used for each decision.

**Acceptance criteria**

- [x] Unmatched protected access fails closed.
- [x] Approval timeout or unavailable broker results in denial.
- [x] Revoked grants cannot be reused from caches or persisted sessions.

## GOV-02 — Policy execution

- [x] Apply modified inputs returned by policy evaluation to the actual operation.
- [x] Implement confirmation callbacks instead of treating confirmation as an unconditional block.
- [x] Define pre-input, pre-action, pre-side-effect, post-output, and pre-commit policy stages.
- [x] Add hierarchical policy scopes for platform, tenant, workspace, agent, tool, and session.
- [x] Define conflict resolution and deny precedence.
- [x] Version policies and retain evaluation evidence.
- [x] Add policies for rate, token, cost, data classification, destination, and model eligibility.
- [x] Prevent policy failures from silently allowing execution.

**Acceptance criteria**

- [x] Modified input is observable in the executed operation and audit evidence.
- [x] Every allow, warning, modification, confirmation, and block is queryable.
- [x] Policy-engine outage follows documented fail-closed behavior for protected operations.

## GOV-03 — Output safety and constitutions

- [x] Separate deterministic validation from probabilistic judge rules.
- [x] Define rule severity, scope, remediation, and evidence.
- [x] Add configurable redact, repair, reject, escalate, and quarantine outcomes.
- [x] Prevent rejected output from being committed to downstream systems.
- [x] Version constitutions and evaluators.
- [x] Add adversarial and multilingual safety datasets.
- [x] Measure false-positive and false-negative rates.

**Acceptance criteria**

- [x] Output policy decisions are reproducible with pinned deterministic dependencies.
- [x] Probabilistic decisions identify model, prompt, threshold, and evidence.
- [x] Quarantined output is inaccessible without explicit authorized review.

## SEC-02 — Secret management

- [ ] Define secret references rather than passing raw values through execution contracts.
- [ ] Add optional secret-provider interfaces and adapters.
- [ ] Resolve secrets only inside the smallest authorized execution boundary.
- [ ] Redact secrets from prompts, logs, traces, journals, errors, and artifacts.
- [ ] Support rotation and revocation without restarting unrelated sessions.
- [ ] Add canary-secret leakage tests.

### Exit criteria for R1

- [x] HAR-01 through HAR-04 are complete for supported environments.
- [ ] SEC-01, SEC-02, and GOV-01 through GOV-03 are complete.
- [ ] Security threat models and abuse cases are reviewed.
- [ ] Fail-closed and isolation tests run in CI.
- [ ] Production hosts do not rely on permissive development defaults.

[Back to roadmap](../roadmap.md)
