# Platform Operations and Developer Experience

This workstream turns framework capabilities into a supportable platform with repeatable deployment, administration, extension, lifecycle, and developer workflows.

**Milestone:** R4
**Dependencies:** Production-critical portions of all earlier workstreams
**Primary owners:** runtime, integration packages, repository infrastructure, host applications

## OPS-01 — Supported deployment profiles

- [ ] Define local development, single-node production, clustered production, and isolated-worker profiles.
- [ ] Document which profile supports which reliability and security guarantees.
- [ ] Provide validated configuration examples for each profile.
- [ ] Separate development defaults from production-safe defaults.
- [ ] Validate configuration at startup and fail before serving traffic when unsafe.
- [ ] Record effective non-secret configuration in diagnostics.

**Acceptance criteria**

- [ ] A clean environment can deploy each supported profile from versioned instructions.
- [ ] Production profiles do not enable permissive identities, anonymous administration, unrestricted tools, or in-process untrusted execution.

## OPS-02 — Container and orchestration assets

- [ ] Publish minimal, non-root, pinned container images with provenance.
- [ ] Add health checks and graceful shutdown behavior.
- [ ] Provide Kubernetes manifests or Helm charts for supported clustered deployment.
- [ ] Define resource requests, limits, disruption budgets, topology spread, and autoscaling signals.
- [ ] Separate runtime, worker, migration, and administrative responsibilities where appropriate.
- [ ] Support network policies and workload identity.
- [ ] Add deployment smoke tests.
- [ ] Document unsupported multi-region assumptions before claiming multi-region support.

**Acceptance criteria**

- [ ] Rolling deployment preserves supported in-flight and persisted work.
- [ ] Workloads run without root or privileged containers by default.
- [ ] Autoscaling does not violate provider quotas or storage capacity.

## OPS-03 — Data lifecycle and disaster recovery

- [ ] Inventory all durable stores and derived indexes.
- [ ] Define backup, restore, point-in-time recovery, and rebuild procedures.
- [ ] Define recovery-point and recovery-time objectives by deployment profile.
- [ ] Test restoration of sessions, grants, policies, knowledge sources, indexes, artifacts, traces, and evaluation baselines.
- [ ] Define retention, archival, deletion, legal hold, and regional placement.
- [ ] Add migration preflight, progress, rollback, and resumability.
- [ ] Run scheduled restore drills.

**Acceptance criteria**

- [ ] Restore tests verify referential consistency across stores.
- [ ] Derived indexes can be rebuilt from authoritative records.
- [ ] Deletion requests propagate to backups according to documented policy.

## OPS-04 — Configuration and administration

- [ ] Define typed, validated, versioned platform configuration.
- [ ] Separate immutable startup settings from safely reloadable settings.
- [ ] Provide authenticated administrative APIs for providers, workspaces, policies, quotas, knowledge, evaluations, and operations.
- [ ] Add dry-run validation and impact analysis.
- [ ] Add optimistic concurrency and change audit.
- [ ] Provide emergency disablement and rollback.
- [ ] Keep application-specific UI outside reusable core packages.
- [ ] Provide a reference administration client or integrate through an application such as Assistant.

**Acceptance criteria**

- [ ] Invalid changes cannot replace last known-good configuration.
- [ ] Every mutation identifies actor, reason, prior version, new version, and affected resources.
- [ ] Administrative endpoints are not exposed anonymously in production profiles.

## OPS-05 — Service-level objectives and runbooks

- [ ] Define availability, latency, durability, quality, safety, and cost indicators.
- [ ] Define service-level objectives by operation and deployment profile.
- [ ] Allocate error budgets.
- [ ] Add alerts tied to user-visible impact.
- [ ] Write runbooks for provider outage, storage degradation, worker exhaustion, policy failure, index corruption, and bad rollout.
- [ ] Link alerts to diagnostics and rollback procedures.
- [ ] Review incidents and add regression tests.

**Acceptance criteria**

- [ ] Alerts are actionable and tested through exercises.
- [ ] Operators can distinguish platform, provider, policy, storage, and customer-input failures.
- [ ] SLO reporting excludes planned categories only through explicit documented policy.

## OPS-06 — Supply-chain security

- [ ] Generate software bills of materials for packages and images.
- [ ] Sign release artifacts and publish provenance attestations.
- [ ] Scan dependencies, containers, and licenses.
- [ ] Pin build tools and critical deployment dependencies.
- [ ] Protect package publishing and release workflows.
- [ ] Define vulnerability response and supported-version policy.
- [ ] Review native dependencies in parser, reasoner, database, and provider adapters.

**Acceptance criteria**

- [ ] Consumers can verify package and image integrity.
- [ ] Critical known vulnerabilities block release according to policy.
- [ ] Every shipped dependency has traceable provenance and license status.

## DX-01 — Reference applications and examples

- [ ] Add a minimal local agent example.
- [ ] Add a governed tool example with approval and audit.
- [ ] Add a knowledge ingestion and grounded RAG example.
- [ ] Add a distributed Orleans example.
- [ ] Add a replay and evaluation-gate example.
- [ ] Add an optional formal-reasoning example after that subsystem exists.
- [ ] Keep examples version-tested in CI.
- [ ] Explain production differences and unsafe shortcuts explicitly.

**Acceptance criteria**

- [ ] Every public workstream has at least one executable reference path.
- [ ] Examples compile and run against the repository version.
- [ ] Copying a development example cannot silently create a production-open administration surface.

## DX-02 — Extension and adapter SDK

- [ ] Define stable extension contracts for tools, providers, storage, parsers, connectors, retrievers, telemetry, identity, and reasoners.
- [ ] Publish conformance suites for each adapter category.
- [ ] Define registration, configuration, capability discovery, health, and shutdown conventions.
- [ ] Define package compatibility metadata.
- [ ] Support trusted compiled registration first.
- [ ] Design any dynamic plugin loading only with signature, trust, dependency, and isolation policies.
- [ ] Avoid loading arbitrary assemblies into the runtime process.

**Acceptance criteria**

- [ ] Third-party adapters can be validated without access to private test infrastructure.
- [ ] Adapter failure is isolated and reported through common error contracts.
- [ ] Compatibility mismatch fails before the adapter becomes routable.

## DX-03 — Developer tooling

- [ ] Provide templates for agents, tools, providers, evaluators, and adapters.
- [ ] Add configuration validation commands.
- [ ] Add dataset linting and local evaluation commands.
- [ ] Add trace/replay inspection tools.
- [ ] Add knowledge-ingestion status and index-diagnostics commands.
- [ ] Add migration planning and dry-run commands.
- [ ] Produce machine-readable output for automation.
- [ ] Ensure CLI operations use the same authenticated control-plane contracts as other clients.

**Acceptance criteria**

- [ ] A developer can scaffold, test, evaluate, and package an extension through documented commands.
- [ ] Local diagnostics explain configuration, dependency, authorization, and compatibility failures.

## DX-04 — API and documentation quality

- [ ] Add XML documentation to every public member.
- [ ] Publish conceptual architecture, security, operations, knowledge, evaluation, and extension guides.
- [ ] Generate API references in CI.
- [ ] Validate internal and external links.
- [ ] Version documentation with releases.
- [ ] Publish migration guides for breaking or durable-state changes.
- [ ] Add tested snippets rather than uncompiled examples.
- [ ] Clearly mark experimental and deprecated APIs.

**Acceptance criteria**

- [ ] Public API documentation builds without warnings under the agreed threshold.
- [ ] Documentation examples execute in CI.
- [ ] A release cannot omit required migration notes.

## DX-05 — Release and compatibility process

- [ ] Define branch, version, preview, stable, and long-term support policy.
- [ ] Automate package versioning and changelog generation from reviewed metadata.
- [ ] Run compatibility, migration, evaluation, security, and performance gates before release.
- [ ] Publish packages, symbols, source links, checksums, and attestations.
- [ ] Test installation into a clean consumer project.
- [ ] Define rollback and package-yank policy.
- [ ] Track roadmap tasks completed by each release.

## ECO-01 — Integration priorities

- [ ] Select initial adapters using user demand, maintenance health, licensing, deployment, and conformance criteria.
- [ ] Deliver at least one production vector backend.
- [ ] Deliver at least one lexical-search backend.
- [ ] Deliver at least one graph backend if graph use cases justify it.
- [ ] Deliver standard telemetry export.
- [ ] Deliver one enterprise identity and one secret-provider integration through optional packages or host guidance.
- [ ] Deliver formal-reasoning adapters only for validated domain use cases.
- [ ] Publish support tier and ownership for each adapter.

### Exit criteria for R4 platform scope

- [ ] OPS-01 through OPS-06 are complete for supported production profiles.
- [ ] DX-01 through DX-05 are complete for stable public extension points.
- [ ] Required ECO-01 integrations meet conformance and operational criteria.
- [ ] Installation, upgrade, backup, restore, rollback, and incident procedures are exercised.

[Back to roadmap](../roadmap.md)
