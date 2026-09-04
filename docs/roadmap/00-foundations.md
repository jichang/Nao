# Foundations and Contracts

This workstream establishes an accurate baseline, stable platform vocabulary, package boundaries, compatibility rules, and a trustworthy build/test surface.

**Milestone:** R0
**Dependencies:** None
**Primary owners:** all Nao projects and repository infrastructure

## Existing baseline

- [x] `Nao.Agents` owns core agent, tool, memory, governance, observability, and harness contracts.
- [x] `Nao.Protocols` owns model response protocol abstractions.
- [x] `Nao.Persistence` provides persistent implementations.
- [x] `Nao.Providers` provides LLM provider adapters.
- [x] `Nao.Eval` provides evaluation primitives and runners.
- [x] `Nao.Runtime.Orleans` provides distributed session/workspace execution.
- [x] Public packages use centralized repository build properties.

## FND-01 — Capability inventory

- [x] Map every README feature to its source type, implementation status, and tests.
- [x] Classify each capability as `implemented`, `partial`, `experimental`, `planned`, or `application-owned`.
- [x] Reconcile documentation claims about group-directory support with current source.
- [x] Document intentionally unsupported features and non-goals.
- [x] Record known correctness gaps, including incomplete graph mutation behavior and host-owned enforcement points.
- [x] Add a lightweight process for updating the inventory with every release.

**Acceptance criteria**

- [x] No advertised capability lacks a source or plan reference.
- [x] Partial behavior is not described as production-complete.
- [x] CI detects broken documentation links to source-controlled roadmap pages.

## FND-02 — Solution and test-surface hygiene

- [x] Inventory every source and test project under the repository.
- [x] Decide whether omitted agent, assistant, loader, and end-to-end test projects belong in the supported solution.
- [x] Add intended projects to `Nao.slnx` or document why they remain outside it.
- [x] Remove stale or accidental cross-repository project references.
- [x] Define unit, integration, end-to-end, security, performance, and evaluation test categories.
- [x] Ensure the default build runs a deterministic supported subset.
- [x] Require explicit CI jobs for tests needing databases, containers, network access, or external models; no such tests currently exist.
- [x] Publish test results and coverage by project and category.

**Acceptance criteria**

- [x] `dotnet build Nao.slnx` builds the supported product surface from a clean checkout.
- [x] `dotnet test Nao.slnx` runs the documented default test surface.
- [x] Every production project has an owning automated test project or documented exception.
- [x] CI cannot silently skip a discovered test project.

## FND-03 — Platform vocabulary and ownership

- [x] Define `agent`, `orchestrator`, `harness`, `tool`, `provider`, `workspace`, `session`, `turn`, and `execution`.
- [x] Distinguish conversation context, working memory, episodic memory, semantic memory, knowledge, and artifacts.
- [x] Define ownership and lifetime for tenant, workspace, group, user, session, and execution data.
- [x] Define source-of-truth rules for immutable events versus materialized projections.
- [x] Define trust levels for user input, retrieved content, model output, tools, and reasoner results.
- [x] Define platform error categories and which errors are retryable.

**Acceptance criteria**

- [x] Public APIs and documentation use the same terms consistently.
- [x] Each durable record has an owner, retention policy, and deletion path.
- [x] Error categories map consistently across agents, tools, providers, storage, and hosts.

Lifecycle coverage includes session turns, audit, key/value memory, semantic memory, execution-scoped working memory, owner-scoped episodic, graph, and tiered memory, traces, metrics, feedback, execution journals, and derived evaluation datasets/reports. Session destruction coordinates its conversation, turn, memory, metric, and journal owners before clearing runtime identity. Canonical exception and HTTP mappings preserve category and retryability across agent, tool, provider, storage, and host boundaries. FND-03 is complete.

## FND-04 — Package and dependency architecture

- [x] Define dependency-direction rules between core contracts, implementations, runtimes, and adapters.
- [x] Keep core contracts free from optional vendor SDKs.
- [x] Define criteria for creating a separate package versus extending an existing package.
- [x] Establish package naming for knowledge, telemetry, identity, vector, graph, and reasoning adapters.
- [x] Define supported .NET and F# versions and upgrade cadence.
- [x] Add architecture tests or build checks for forbidden dependencies.
- [x] Document experimental API namespaces and stability guarantees.

**Acceptance criteria**

- [x] Core packages can be consumed without Orleans, database, ontology, or vendor-specific dependencies.
- [x] Optional integrations can evolve without forcing unrelated dependency upgrades.
- [x] Forbidden dependency directions fail CI.

The project graph is acyclic and enforced by `scripts/validate-project-dependencies.py`, including core-package checks for optional runtime, database, vector-store, and model-vendor dependencies. Provider adapters are independently consumable through `Nao.Providers.OpenAICompatible`, `Nao.Providers.Anthropic`, and `Nao.Providers.Ollama`; persistence capabilities are independently consumable through infrastructure, memory, observability, and feedback packages. The aggregate `Nao.Providers` and `Nao.Persistence` packages remain explicit opt-in composition conveniences. FND-04 is complete.

## FND-05 — Public contract compatibility

- [x] Define pre-release breaking-change policy and post-release semantic-versioning decisions for F# records, discriminated unions, interfaces, and serialized contracts.
- [x] Version durable events, Orleans state, knowledge records, traces, and evaluation reports.
- [x] Define unknown-field and unknown-case behavior.
- [x] Require migration guides for breaking API and durable-format changes.
- [x] Define when migration code, deprecation periods, and major versions are required.
- [x] Inventory every current durable format and identify fail-fast/versioning gaps.
- [x] Verify every incompatible or corrupt format fails before mutation.
- [x] Document mixed-version runtime support as unsupported until a stable multi-version deployment contract exists.

**Acceptance criteria**

- [x] Pre-release breaking changes use explicit external migration or reset instructions instead of embedded legacy readers.
- [x] Incompatible state fails with an actionable diagnostic before mutation.
- [x] Every public breaking change requires a migration guide; stable releases additionally require an explicit semantic-version decision.

Nao currently makes no backward-compatibility or rolling-upgrade promise. The migration policy favors clean current contracts and documented external transformation over runtime compatibility branches. Implemented event streams, traces, evaluation archives, Orleans session state, file documents, and ADO.NET tables carry explicit current-schema versions and reject incompatible or corrupt state before mutation. Knowledge-record persistence is not yet implemented and must define these guarantees with its first durable contract. FND-05 is complete.

## FND-06 — Stable identity and correlation model

- [ ] Define typed identifiers for tenant, group, user, workspace, session, turn, execution, artifact, source, and trace.
- [ ] Define identifier generation, uniqueness, parsing, and serialization rules.
- [ ] Propagate correlation identifiers through agents, tools, providers, memory, persistence, telemetry, and evaluation.
- [ ] Define causation and correlation links for delegation and retries.
- [ ] Prevent externally supplied identifiers from escaping authorization scope.

**Acceptance criteria**

- [ ] One execution can be reconstructed across all participating components.
- [ ] Retries retain causation while receiving distinct attempt identities.
- [ ] Identifier-based cross-tenant access tests fail closed.

## FND-07 — Architecture decision and documentation process

- [ ] Add an architecture decision record template.
- [ ] Require decisions for new platform boundaries, durable contracts, and security models.
- [ ] Add contributor guidance for updating roadmap checkboxes.
- [ ] Add ownership metadata for workstreams and public packages.
- [ ] Define release notes and migration-guide templates.
- [ ] Add a documentation build/link-validation job.

### Exit criteria for R0

- [ ] FND-01 through FND-07 are complete.
- [ ] The capability inventory and solution membership are accurate.
- [ ] Contract compatibility and dependency rules are enforced by CI.
- [ ] Roadmap tasks can be linked reliably from issues, commits, and releases.

[Back to roadmap](../roadmap.md)
