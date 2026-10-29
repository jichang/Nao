# Platform Operations and Developer Experience

This workstream turns framework capabilities into a supportable platform with repeatable deployment, administration, extension, lifecycle, and developer workflows.

**Milestone:** R4
**Dependencies:** Production-critical portions of all earlier workstreams
**Primary owners:** runtime, integration packages, repository infrastructure, host applications

## OPS-01 — Supported deployment profiles

- [x] Define local development, single-node production, clustered production, and isolated-worker profiles.
- [x] Document which profile supports which reliability and security guarantees.
- [x] Provide validated configuration examples for each profile.
- [x] Separate development defaults from production-safe defaults.
- [x] Validate configuration at startup and fail before serving traffic when unsafe.
- [x] Record effective non-secret configuration in diagnostics.

**Acceptance criteria**

- [x] A clean environment can deploy each supported profile from versioned instructions.
- [x] Production profiles do not enable permissive identities, anonymous administration, unrestricted tools, or in-process untrusted execution.

## OPS-02 — Container and orchestration assets

- [x] Publish minimal, non-root, pinned container images with provenance.
- [x] Add health checks and graceful shutdown behavior.
- [x] Provide Kubernetes manifests or Helm charts for supported clustered deployment.
- [x] Define resource requests, limits, disruption budgets, topology spread, and autoscaling signals.
- [x] Separate runtime, worker, migration, and administrative responsibilities where appropriate.
- [x] Support network policies and workload identity.
- [x] Add deployment smoke tests.
- [x] Document unsupported multi-region assumptions before claiming multi-region support.

**Acceptance criteria**

- [x] Rolling deployment preserves supported in-flight and persisted work.
- [x] Workloads run without root or privileged containers by default.
- [x] Autoscaling does not violate provider quotas or storage capacity.

## OPS-03 — Data lifecycle and disaster recovery

- [x] Inventory all durable stores and derived indexes.
- [x] Define backup, restore, point-in-time recovery, and rebuild procedures.
- [x] Define recovery-point and recovery-time objectives by deployment profile.
- [x] Test restoration of sessions, grants, policies, knowledge sources, indexes, artifacts, traces, and evaluation baselines.
- [x] Define retention, archival, deletion, legal hold, and regional placement.
- [x] Add migration preflight, progress, rollback, and resumability.
- [x] Run scheduled restore drills.

**Acceptance criteria**

- [x] Restore tests verify referential consistency across stores.
- [x] Derived indexes can be rebuilt from authoritative records.
- [x] Deletion requests propagate to backups according to documented policy.

## OPS-04 — Configuration and administration

- [x] Define typed, validated, versioned platform configuration.
- [x] Separate immutable startup settings from safely reloadable settings.
- [x] Provide authenticated administrative APIs for providers, workspaces, policies, quotas, knowledge, evaluations, and operations.
- [x] Add dry-run validation and impact analysis.
- [x] Add optimistic concurrency and change audit.
- [x] Provide emergency disablement and rollback.
- [x] Keep application-specific UI outside reusable core packages.
- [x] Provide a reference administration client or integrate through an application such as Assistant.

**Acceptance criteria**

- [x] Invalid changes cannot replace last known-good configuration.
- [x] Every mutation identifies actor, reason, prior version, new version, and affected resources.
- [x] Administrative endpoints are not exposed anonymously in production profiles.

## OPS-05 — Service-level objectives and runbooks

- [x] Define availability, latency, durability, quality, safety, and cost indicators.
- [x] Define service-level objectives by operation and deployment profile.
- [x] Allocate error budgets.
- [x] Add alerts tied to user-visible impact.
- [x] Write runbooks for provider outage, storage degradation, worker exhaustion, policy failure, index corruption, and bad rollout.
- [x] Link alerts to diagnostics and rollback procedures.
- [x] Review incidents and add regression tests.

**Acceptance criteria**

- [x] Alerts are actionable and tested through exercises.
- [x] Operators can distinguish platform, provider, policy, storage, and customer-input failures.
- [x] SLO reporting excludes planned categories only through explicit documented policy.

## OPS-06 — Supply-chain security

- [x] Generate software bills of materials for packages and images.
- [x] Sign release artifacts and publish provenance attestations.
- [x] Scan dependencies, containers, and licenses.
- [x] Pin build tools and critical deployment dependencies.
- [x] Protect package publishing and release workflows.
- [x] Define vulnerability response and supported-version policy.
- [x] Review native dependencies in parser, reasoner, database, and provider adapters.

**Acceptance criteria**

- [x] Consumers can verify package and image integrity.
- [x] Critical known vulnerabilities block release according to policy.
- [x] Every shipped dependency has traceable provenance and license status.

## DX-01 — Reference applications and examples

- [x] Add a minimal local agent example.
- [x] Add a governed tool example with approval and audit.
- [x] Add a knowledge ingestion and grounded RAG example.
- [x] Add a distributed Orleans example.
- [x] Add a replay and evaluation-gate example.
- [x] Add an optional formal-reasoning example after that subsystem exists.
- [x] Keep examples version-tested in CI.
- [x] Explain production differences and unsafe shortcuts explicitly.

**Acceptance criteria**

- [x] Every public workstream has at least one executable reference path.
- [x] Examples compile and run against the repository version.
- [x] Copying a development example cannot silently create a production-open administration surface.

## DX-02 — Extension and adapter SDK

- [x] Define stable extension contracts for tools, providers, storage, parsers, connectors, retrievers, telemetry, identity, and reasoners.
- [x] Publish conformance suites for each adapter category.
- [x] Define registration, configuration, capability discovery, health, and shutdown conventions.
- [x] Define package compatibility metadata.
- [x] Support trusted compiled registration first.
- [x] Design any dynamic plugin loading only with signature, trust, dependency, and isolation policies.
- [x] Avoid loading arbitrary assemblies into the runtime process.

**Acceptance criteria**

- [x] Third-party adapters can be validated without access to private test infrastructure.
- [x] Adapter failure is isolated and reported through common error contracts.
- [x] Compatibility mismatch fails before the adapter becomes routable.

## DX-03 — Developer tooling

- [x] Provide templates for agents, tools, providers, evaluators, and adapters.
- [x] Add configuration validation commands.
- [x] Add dataset linting and local evaluation commands.
- [x] Add trace/replay inspection tools.
- [x] Add knowledge-ingestion status and index-diagnostics commands.
- [x] Add migration planning and dry-run commands.
- [x] Produce machine-readable output for automation.
- [x] Ensure CLI operations use the same authenticated control-plane contracts as other clients.

**Acceptance criteria**

- [x] A developer can scaffold, test, evaluate, and package an extension through documented commands.
- [x] Local diagnostics explain configuration, dependency, authorization, and compatibility failures.

## DX-04 — API and documentation quality

- [x] Add XML documentation to every public member.
- [x] Publish conceptual architecture, security, operations, knowledge, evaluation, and extension guides.
- [x] Generate API references in CI.
- [x] Validate internal and external links.
- [x] Version documentation with releases.
- [x] Publish migration guides for breaking or durable-state changes.
- [x] Add tested snippets rather than uncompiled examples.
- [x] Clearly mark experimental and deprecated APIs.

**Acceptance criteria**

- [x] Public API documentation builds without warnings under the agreed threshold.
- [x] Documentation examples execute in CI.
- [x] A release cannot omit required migration notes.

## DX-05 — Release and compatibility process

- [x] Define branch, version, preview, stable, and long-term support policy.
- [x] Automate package versioning and changelog generation from reviewed metadata.
- [x] Run compatibility, migration, evaluation, security, and performance gates before release.
- [x] Publish packages, symbols, source links, checksums, and attestations.
- [x] Test installation into a clean consumer project.
- [x] Define rollback and package-yank policy.
- [x] Track roadmap tasks completed by each release.

## ECO-01 — Integration priorities

- [x] Select initial adapters using user demand, maintenance health, licensing, deployment, and conformance criteria.
- [x] Deliver at least one production vector backend.
- [x] Deliver at least one lexical-search backend.
- [x] Deliver at least one graph backend if graph use cases justify it.
- [x] Deliver standard telemetry export.
- [x] Deliver one enterprise identity and one secret-provider integration through optional packages or host guidance.
- [x] Deliver formal-reasoning adapters only for validated domain use cases.
- [x] Publish support tier and ownership for each adapter.

### Exit criteria for R4 platform scope

- [x] OPS-01 through OPS-06 are complete for supported production profiles.
- [x] DX-01 through DX-05 are complete for stable public extension points.
- [x] Required ECO-01 integrations meet conformance and operational criteria.
- [x] Installation, upgrade, backup, restore, rollback, and incident procedures are exercised.

[Back to roadmap](../roadmap.md)
