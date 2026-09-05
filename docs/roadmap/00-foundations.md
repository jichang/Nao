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

- [x] Define typed identifiers for tenant, group, user, workspace, session, turn, execution, artifact, source, and trace.
- [x] Define identifier generation, uniqueness, parsing, and serialization rules.
- [x] Propagate correlation identifiers through agents, tools, providers, memory, persistence, telemetry, and evaluation.
- [x] Define causation and correlation links for delegation and retries.
- [x] Prevent externally supplied identifiers from escaping authorization scope.

**Acceptance criteria**

- [x] One execution can be reconstructed across all participating components.
- [x] Retries retain causation while receiving distinct attempt identities.
- [x] Identifier-based cross-tenant access tests fail closed.

Core typed identifiers and canonical codecs are implemented, and the Orleans registry now consumes the core `WorkspaceId` rather than defining a runtime-local duplicate. `SecurityPrincipal` and `AuthorizationScope` bind tenant, group, user, workspace, and optional session identity; cross-tenant and unauthorized-group tests fail closed. Orleans session grains derive scope from a host-injected principal, persist tenant/user/group/workspace/session lineage, and revalidate it before every state operation. A session turn propagates one `CorrelationContext` through event scopes, harness execution, agents, tools, provider requests, audit and execution-journal persistence, execution traces, verification judges, and publishing observability services. Working memory uses typed execution ownership; summarization, compaction, consolidation, and task grounding require and forward caller correlation. Evaluation runners propagate the same correlation through evaluators and LLM judges, and each result retains that execution ID. Conversation, turn, metric, journal, execution-trace, low-level span, audit, working-memory, and evaluation records support typed execution reconstruction; an end-to-end file-reload test proves that one harness execution converges across its participating stores. FND-06 is complete.

## FND-07 — Architecture decision and documentation process

- [x] Add an architecture decision record template.
- [x] Require decisions for new platform boundaries, durable contracts, and security models.
- [x] Add contributor guidance for updating roadmap checkboxes.
- [x] Add ownership metadata for workstreams and public packages.
- [x] Define release notes and migration-guide templates.
- [x] Add a documentation build/link-validation job.

Architecture decisions use a required template and lifecycle under `docs/decisions/`; pull requests explicitly review ADR, migration, release-note, capability, roadmap, and validation obligations. `CODEOWNERS` assigns all public packages and roadmap workstreams, and documentation validation fails when required ownership entries, process templates, ADR metadata, review checks, or source/generated-site workflow commands are missing. FND-07 is complete.

### Exit criteria for R0

- [x] FND-01 through FND-07 are complete.
- [x] The capability inventory and solution membership are accurate.
- [x] Contract compatibility and dependency rules are enforced by CI.
- [x] Roadmap tasks can be linked reliably from issues, commits, and releases.

[Back to roadmap](../roadmap.md)
