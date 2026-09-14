# Security Threat Model

This document reviews the security model for Nao's R1 harness, governance, identity, worker,
provider, secret, persistence, and output-release boundaries. It describes implemented controls
and residual deployment responsibilities; it does not treat configuration contracts as proof that
a host deployment is secure.

**Review status:** Reviewed against the R1 implementation on 2026-09-14.

**Review scope:** SEC-01, SEC-02, HAR-01 through HAR-04, and GOV-01 through GOV-03.

## Security objectives

Nao must:

- Preserve the authenticated tenant, original user, effective user, service, agent, workspace,
  and session authority across every protected operation.
- Prevent one authorization identity from reading or mutating another identity's durable state.
- Deny undeclared resources, side effects, destinations, models, and secret references.
- Keep secret material out of prompts and every observable or durable output boundary.
- Prevent untrusted worker execution from silently escaping configured process or container
  restrictions.
- Fail closed when identity, policy, permission, secret, isolation, evidence, or persistence
  dependencies are unavailable or malformed.
- Prevent rejected, escalated, or quarantined model output from being published or persisted as
  successful output.
- Preserve immutable evidence for authorization, policy, execution, and review decisions.

Availability is important, but never takes precedence over authorization, isolation, secret
confidentiality, or output-release controls.

## Assets

| Asset | Required property |
|---|---|
| Authentication and delegation context | Integrity and unambiguous provenance |
| Tenant, workspace, session, memory, feedback, and conversation data | Confidentiality and authorization-bound isolation |
| Secret material and secret lifecycle state | Confidentiality, integrity, prompt revocation, and version correctness |
| Tool and MCP side effects | Explicit authorization, idempotency, and auditable outcomes |
| Model prompts, responses, and streaming output | Confidentiality, policy enforcement, and controlled release |
| Execution journals, traces, metrics, audit records, and artifacts | Integrity, tenant isolation, and secret-free persistence |
| Worker host, filesystem, environment, devices, and network | Least privilege and declared access only |
| Policy, constitution, evaluator, and provider versions | Integrity and reproducibility |
| Quarantined output | Confidentiality and explicit review authorization |

## Actors and assumptions

Potentially hostile actors include:

- An unauthenticated or incorrectly authenticated caller.
- An authenticated tenant user attempting cross-tenant or privilege-escalating access.
- A delegated service or agent exceeding its authority.
- A malicious prompt, retrieved document, model response, tool result, or MCP server.
- A compromised process or container worker.
- A failing or malicious provider, policy backend, confirmation broker, secret provider, or
  persistence adapter.
- An operator who accidentally enables permissive test configuration in a production path.

The following remain host responsibilities:

- Validate tokens, issuers, audiences, expiry, revocation, and authentication strength before
  constructing a `SecurityPrincipal`.
- Authorize secret-provider and persistence-adapter access to their backing systems.
- Protect encryption keys, vault credentials, audit infrastructure, container runtimes, worker
  binaries, images, trust roots, and deployment configuration.
- Select platform isolation that actually enforces the declared policy. Disabled process
  isolation is not a sandbox.
- Operate external egress brokers, scanners, attestation systems, distributed rate limits, and
  production quarantine-review interfaces.

## Trust boundaries and data flow

1. **Host to harness.** The host converts validated authentication into an
   `AuthorizationScope`. Protected operations reject absent authority rather than accepting an
   owner string supplied by the caller.
2. **Harness to policy and permission engines.** Typed lifecycle facts and canonical resources
   cross into host-owned decision backends. Backend failure, timeout, malformed decisions, or
   missing evidence fail closed.
3. **Harness to provider.** Provider activation retains only a `SecretReference`. Each operation
   resolves current material under the authenticated scope, creates an operation-local client,
   and disposes it after the call.
4. **Harness to worker.** A fresh process or container completes a version handshake before it
   receives identity, budgets, declared secrets, or input over private standard input. Worker
   output is untrusted until protocol validation and output governance complete.
5. **Harness to tools and MCP.** Every invocation is checked against a canonical typed resource
   and action. Permission and side-effect evidence is recorded around execution.
6. **Harness to persistence and observability.** Stores derive partitions from the complete
   claim-free `AuthorizationIdentity`. Redaction is repeated at storage boundaries rather than
   trusting upstream publication.
7. **Output checks to caller.** Constitution decisions gate release. Rejected, escalated, and
   quarantined content cannot become a successful response, conversation turn, trace, or
   checkpoint.

## Threat analysis

| Threat | Attack path | Enforced control | Residual risk or owner |
|---|---|---|---|
| Identity spoofing | Caller supplies a tenant or owner identifier instead of authenticated authority | Protected APIs require `AuthorizationScope`; identities use typed constructors; malformed worker identity fails before handler invocation | Host token validation and principal construction |
| Confused deputy | Delegated service or agent acts outside the originating user's authority | Original and effective users remain distinct; delegation lineages are ordered; authorization is propagated to nested work | Host policy must decide which impersonation and delegation transitions are allowed |
| Cross-tenant data access | Crafted keys collide with another tenant's persisted data | Storage partitions derive from complete claim-free `AuthorizationIdentity`; stores enforce scope at the boundary | Backing-store credentials and physical isolation remain adapter/operator concerns |
| Authorization bypass | Direct tool, MCP, memory, retrieval, or quarantine access skips a UI check | Authorization is enforced inside each protected capability; deny takes precedence; canonical matching rejects malformed targets | New capability implementations require the same boundary review |
| Reusable-grant abuse | Expired, revoked, broader, or differently scoped approval is replayed | Grants bind canonical target, action, identity, expiry, and revocation state; immutable decision evidence is stored | Compromise of the host grant store remains a host threat |
| Policy bypass | Missing facts or a failed policy backend are treated as approval | Typed facts, hierarchical evaluation, explicit conflict rules, bounded callbacks, and fail-closed errors | Incorrect but well-formed application policy remains possible |
| Model-routing bypass | A caller selects a disallowed model after policy evaluation | The standard orchestrator checks the actual provider identity before budget use or invocation | Custom orchestrators must preserve this boundary |
| Prompt or retrieved-content injection | Untrusted content requests tools, secrets, policy changes, or unsafe output | Tool permissions remain independent of model instructions; secrets are reference-based; post-output constitutions gate release | Model-level manipulation that remains within granted authority requires application-specific policy and evaluation |
| Secret exfiltration | Secret appears in prompts, responses, chunks, errors, logs, traces, artifacts, or durable records | Operation-local resolution, execution-local exact redaction, split-stream redaction, and independent storage redaction | A compromised dependency can transmit material after legitimate resolution; hosts must restrict provider and worker network access |
| Secret substitution | Provider returns material for a different reference | `SecretValue` is reference-bound and mismatches fail closed | Provider correctness and vault ACLs remain host responsibilities |
| Stale secret use | Long-lived clients retain rotated or revoked credentials | Each provider operation resolves again; workers resolve per isolated execution; versioned lookup states represent revocation explicitly | An already in-flight operation cannot retroactively forget material |
| Secret lifecycle race | Concurrent rotation or revocation overwrites a newer value | Compare-and-swap mutations require the expected opaque `SecretVersion`; stale updates fail | Production vault adapter must provide equivalent atomic semantics |
| Worker protocol spoofing | Worker emits malformed, oversized, duplicate, or wrong-version frames | Strict versioned JSON-line protocol, frame validation, output byte limits, timeout, and process-tree termination | Worker binary and image provenance remain deployment concerns |
| Worker identity leakage | Principal data enters prompts, arguments, environment, output, or artifacts | Identity travels in a separate protocol field over private standard input and is not copied by the framework | Custom handlers can still deliberately disclose identity they receive |
| Filesystem escape | Process reads paths outside the declared allowlist | Linux Landlock mode validates and installs rules before handler execution; container mounts are explicit and root is read-only | Disabled/non-Linux process isolation is not confinement; Linux user namespaces retain host DAC access |
| Environment leakage | Worker inherits host credentials or configuration | Process workers inherit no host environment and receive only allowlisted values | Runtime or launcher defects remain platform risks |
| Network escape | Worker reaches undeclared internal or public destinations | Containers deny networking by default; allowlists require a host egress-broker lease; sandbox denial wins | Proxy routing, DNS, certificates, and broker enforcement are host responsibilities |
| Resource exhaustion | Worker consumes unlimited time, output, calls, tokens, cost, memory, CPU, or processes | Explicit budgets, cancellation, bounded output, process-tree termination, and container hard limits | Process memory and CPU sampling are reactive; direct-worker CPU excludes descendants |
| Container privilege escalation | Worker runs as root, gains capabilities, writes root, or reaches host devices | Digest-pinned image, numeric non-root identity, read-only root, dropped capabilities, no-new-privileges, explicit mounts/devices, and bounded temporary storage | Container daemon compromise is outside the library boundary |
| Side-effect duplication | Retry repeats an external mutation | Idempotency keys, intent/outcome journaling, replay-safety classification, checkpoints, and compensation records | External systems must honor idempotency or provide reconciliation |
| Ambiguous side effect | Network or process failure hides whether a mutation committed | Ambiguous outcomes are represented and quarantined rather than retried as success | Operator or adapter-specific reconciliation may be required |
| Unsafe output publication | Rejected or quarantined output is stored or shown as successful | Constitution disposition is the release gate; transformed output is revalidated once; quarantine review is authorized and audited | Rules and probabilistic evaluators can still have false positives or negatives |
| Audit tampering or omission | Security-sensitive action succeeds without evidence | Permission and policy evidence is immutable; required audit persistence failures fail closed | External store integrity, retention, and access monitoring remain operational responsibilities |
| Test-mode configuration in production | A host selects an unrestricted context, empty harness services, or implicit harness policy | Permissive fixtures are compiled only into test projects and are absent from the production package; production evaluation requires explicit harness and sandbox configuration; CI rejects production-source references | Hosts remain responsible for the security content of their explicit policy and adapter configuration |

## Reviewed abuse cases

The R1 review includes deterministic tests or explicit boundary analysis for these abuse cases:

1. Supply a forged owner string or omit authenticated authority when accessing protected storage.
2. Reuse another tenant's memory, conversation, feedback, trace, journal, audit, or quarantine key.
3. Escalate through nested service, agent, or effective-user delegation.
4. Invoke a tool, MCP server, delegated agent, memory operation, or retrieval target without the
   exact required permission.
5. Replay an expired, revoked, differently scoped, or conflicting approval.
6. Force a policy backend error, timeout, exception, malformed fact set, or failed confirmation
   and attempt to continue.
7. Select a provider different from the one approved by model policy.
8. Resolve an undeclared, missing, revoked, cross-owner, or mismatched secret.
9. Rotate or revoke a secret with a stale expected version.
10. Split secret material across streaming chunks or place it in maps, errors, artifacts, events,
    final responses, traces, journals, audit entries, metrics, or serialized files.
11. Fail the worker handshake and attempt to trigger secret resolution anyway.
12. Send malformed identity, budgets, protocol frames, excess output, or an unsupported protocol
    version from a worker.
13. Read undeclared files or environment variables from a worker.
14. Retain child processes after cancellation or exceed time, memory, CPU, output, call, token,
    cost, or process-count limits.
15. Start an unverified, mutable, privileged, writable-root, over-mounted, over-networked, or
    uncleared container.
16. Publish, persist, or commit output after reject, escalation, quarantine, or failed repair.
17. Read quarantined output without exact administrative authorization and audited review.
18. Repeat an ambiguous external side effect through retry or replay.

Canary tests deliberately use unique per-run values and scan returned worker results, captured
streams, provider results and failures, emitted events, structured errors, artifacts, and
file-backed persistence. The raw canary must be absent; durable tests also require explicit
redaction evidence so an empty-output implementation cannot pass.

## Residual risks and required deployment decisions

Before production use, a host must document:

- Authentication trust roots and principal-mapping policy.
- The selected worker isolation mode per workload and why it is sufficient.
- Container image verification, scanning, signing, and update policy.
- Network egress enforcement and destination-resolution behavior.
- Production vault and secret-rotation implementation.
- Persistence encryption, backup, retention, deletion, and operator-access policy.
- Audit export, immutability, alerting, and incident-response ownership.
- Application-specific permissions, lifecycle policies, constitutions, evaluator thresholds, and
  quarantine-review procedures.
- Recovery and reconciliation for non-idempotent external side effects.

Known platform limitations remain visible in the [capability inventory](capabilities.md), not
silently accepted as mitigations.

## Review procedure

Re-review this threat model when a change:

- Adds a trust boundary, protected capability, durable store, external provider, worker mode, or
  network path.
- Changes identity, delegation, authorization, policy, secret, protocol, or output-release
  contracts.
- Changes a durable schema or partition key.
- Weakens a fail-closed path, isolation default, resource bound, or audit requirement.
- Introduces a new production limitation or resolves an existing residual risk.

Each review must update the threat table and abuse cases, add focused negative tests where the
threat is executable in the repository, update the [capability inventory](capabilities.md), and
link the owning roadmap task.
