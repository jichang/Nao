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
- [ ] Process and container isolation are enforced by real execution environments.
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
- [ ] Assign idempotency keys to turns, tool calls, provider calls, and committed artifacts.
- [ ] Distinguish retry-safe operations from externally side-effecting operations.
- [ ] Persist side-effect intent before execution and outcome after execution.
- [ ] Resume interrupted execution from the last safe checkpoint.
- [ ] Support deterministic replay with recorded provider/tool fixtures.
- [ ] Represent non-replayable dependencies explicitly.
- [ ] Add compensation orchestration for reversible tool operations.
- [ ] Detect and quarantine ambiguous outcomes after process or network failure.

**Acceptance criteria**

- [ ] Restarting after every checkpoint does not duplicate committed side effects.
- [ ] A recorded execution can be replayed without live provider access.
- [ ] Compensation results are durable, correlated, and auditable.

## HAR-03 — Process isolation

- [ ] Implement a process-backed `IExecutionEnvironment`.
- [ ] Define the worker protocol and version handshake.
- [ ] Run workers under a restricted operating-system identity where supported.
- [ ] Enforce working-directory and filesystem allowlists.
- [ ] Enforce environment-variable allowlists and secret handles.
- [ ] Enforce wall-clock timeout and propagate cancellation.
- [ ] Enforce output, tool-call, token, and cost budgets.
- [ ] Enforce memory and CPU limits on supported operating systems.
- [ ] Kill complete child-process trees after timeout, cancellation, or host shutdown.
- [ ] Capture bounded stdout/stderr as redacted artifacts.
- [ ] Document platform-specific enforcement differences.

**Acceptance criteria**

- [ ] A worker cannot read undeclared test files or environment variables.
- [ ] A worker cannot retain child processes after cancellation.
- [ ] Every configured limit has a deterministic test and structured result.

## HAR-04 — Container isolation

- [ ] Implement a container-backed `IExecutionEnvironment`.
- [ ] Pin images by digest and record supply-chain metadata.
- [ ] Use non-root users, read-only roots, dropped capabilities, and no privilege escalation by default.
- [ ] Define explicit mounts, working directories, devices, and temporary storage.
- [ ] Deny network access by default and support destination allowlists.
- [ ] Apply CPU, memory, process-count, time, and output limits.
- [ ] Scan or attest supported worker images.
- [ ] Clean up containers, volumes, and networks after all terminal states.
- [ ] Add host capability detection and a clear unsupported result.

**Acceptance criteria**

- [ ] Container escape-oriented regression tests run in the appropriate secured CI environment.
- [ ] No undeclared mount or network destination is reachable.
- [ ] Image and policy identity appear in execution evidence.

## SEC-01 — Identity and security principal

- [ ] Define a transport-neutral principal containing tenant, subject, service identity, roles, claims, and authentication strength.
- [ ] Propagate the principal through sessions, agents, tools, knowledge retrieval, storage, telemetry, and evaluation.
- [ ] Distinguish user authority from agent-delegated authority.
- [ ] Define impersonation and service-to-service delegation rules.
- [ ] Reject missing or ambiguous tenant context on protected operations.
- [ ] Provide host adapters for OIDC/OAuth claims without coupling core packages to one identity provider.

**Acceptance criteria**

- [ ] Authorization decisions can identify the original user and every delegation.
- [ ] Cross-tenant principal substitution fails closed.
- [ ] Authentication details are not leaked into prompts or model-visible context.

## GOV-01 — Permission enforcement

- [ ] Define canonical resources and actions for files, web, tools, memory, knowledge, models, artifacts, and administration.
- [ ] Connect `PermissionGate` to a host-owned approval broker contract.
- [ ] Implement approve-once, session, workspace, and durable grant scopes.
- [ ] Implement expiry and revocation.
- [ ] Make deny precedence and wildcard matching explicit and tested.
- [ ] Enforce permissions through local tools, MCP tools, delegated agents, and retrieval adapters.
- [ ] Ensure prompts cannot grant permissions.
- [ ] Record the exact policy and grant versions used for each decision.

**Acceptance criteria**

- [ ] Unmatched protected access fails closed.
- [ ] Approval timeout or unavailable broker results in denial.
- [ ] Revoked grants cannot be reused from caches or persisted sessions.

## GOV-02 — Policy execution

- [ ] Apply modified inputs returned by policy evaluation to the actual operation.
- [ ] Implement confirmation callbacks instead of treating confirmation as an unconditional block.
- [ ] Define pre-input, pre-action, pre-side-effect, post-output, and pre-commit policy stages.
- [ ] Add hierarchical policy scopes for platform, tenant, workspace, agent, tool, and session.
- [ ] Define conflict resolution and deny precedence.
- [ ] Version policies and retain evaluation evidence.
- [ ] Add policies for rate, token, cost, data classification, destination, and model eligibility.
- [ ] Prevent policy failures from silently allowing execution.

**Acceptance criteria**

- [ ] Modified input is observable in the executed operation and audit evidence.
- [ ] Every allow, warning, modification, confirmation, and block is queryable.
- [ ] Policy-engine outage follows documented fail-closed behavior for protected operations.

## GOV-03 — Output safety and constitutions

- [ ] Separate deterministic validation from probabilistic judge rules.
- [ ] Define rule severity, scope, remediation, and evidence.
- [ ] Add configurable redact, repair, reject, escalate, and quarantine outcomes.
- [ ] Prevent rejected output from being committed to downstream systems.
- [ ] Version constitutions and evaluators.
- [ ] Add adversarial and multilingual safety datasets.
- [ ] Measure false-positive and false-negative rates.

**Acceptance criteria**

- [ ] Output policy decisions are reproducible with pinned deterministic dependencies.
- [ ] Probabilistic decisions identify model, prompt, threshold, and evidence.
- [ ] Quarantined output is inaccessible without explicit authorized review.

## SEC-02 — Secret management

- [ ] Define secret references rather than passing raw values through execution contracts.
- [ ] Add optional secret-provider interfaces and adapters.
- [ ] Resolve secrets only inside the smallest authorized execution boundary.
- [ ] Redact secrets from prompts, logs, traces, journals, errors, and artifacts.
- [ ] Support rotation and revocation without restarting unrelated sessions.
- [ ] Add canary-secret leakage tests.

### Exit criteria for R1

- [ ] HAR-01 through HAR-04 are complete for supported environments.
- [ ] SEC-01, SEC-02, and GOV-01 through GOV-03 are complete.
- [ ] Security threat models and abuse cases are reviewed.
- [ ] Fail-closed and isolation tests run in CI.
- [ ] Production hosts do not rely on permissive development defaults.

[Back to roadmap](../roadmap.md)
