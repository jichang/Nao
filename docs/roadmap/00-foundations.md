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

- [ ] Map every README feature to its source type, implementation status, and tests.
- [ ] Classify each capability as `implemented`, `partial`, `experimental`, `planned`, or `application-owned`.
- [ ] Reconcile documentation claims about group-directory support with current source.
- [ ] Document intentionally unsupported features and non-goals.
- [ ] Record known correctness gaps, including incomplete graph mutation behavior and host-owned enforcement points.
- [ ] Add a lightweight process for updating the inventory with every release.

**Acceptance criteria**

- [ ] No advertised capability lacks a source or plan reference.
- [ ] Partial behavior is not described as production-complete.
- [ ] CI detects broken documentation links to source-controlled roadmap pages.

## FND-02 — Solution and test-surface hygiene

- [ ] Inventory every source and test project under the repository.
- [ ] Decide whether omitted agent, assistant, loader, and end-to-end test projects belong in the supported solution.
- [ ] Add intended projects to `Nao.slnx` or document why they remain outside it.
- [ ] Remove stale or accidental cross-repository project references.
- [ ] Define unit, integration, end-to-end, security, performance, and evaluation test categories.
- [ ] Ensure the default build runs a deterministic supported subset.
- [ ] Add explicit CI jobs for tests requiring databases, containers, network access, or external models.
- [ ] Publish test results and coverage by project and category.

**Acceptance criteria**

- [ ] `dotnet build Nao.slnx` builds the supported product surface from a clean checkout.
- [ ] `dotnet test Nao.slnx` runs the documented default test surface.
- [ ] Every production project has an owning automated test project or documented exception.
- [ ] CI cannot silently skip a discovered test project.

## FND-03 — Platform vocabulary and ownership

- [ ] Define `agent`, `orchestrator`, `harness`, `tool`, `provider`, `workspace`, `session`, `turn`, and `execution`.
- [ ] Distinguish conversation context, working memory, episodic memory, semantic memory, knowledge, and artifacts.
- [ ] Define ownership and lifetime for tenant, workspace, group, user, session, and execution data.
- [ ] Define source-of-truth rules for immutable events versus materialized projections.
- [ ] Define trust levels for user input, retrieved content, model output, tools, and reasoner results.
- [ ] Define platform error categories and which errors are retryable.

**Acceptance criteria**

- [ ] Public APIs and documentation use the same terms consistently.
- [ ] Each durable record has an owner, retention policy, and deletion path.
- [ ] Error categories map consistently across agents, tools, providers, storage, and hosts.

## FND-04 — Package and dependency architecture

- [ ] Define dependency-direction rules between core contracts, implementations, runtimes, and adapters.
- [ ] Keep core contracts free from optional vendor SDKs.
- [ ] Define criteria for creating a separate package versus extending an existing package.
- [ ] Establish package naming for knowledge, telemetry, identity, vector, graph, and reasoning adapters.
- [ ] Define supported .NET and F# versions and upgrade cadence.
- [ ] Add architecture tests or build checks for forbidden dependencies.
- [ ] Document experimental API namespaces and stability guarantees.

**Acceptance criteria**

- [ ] Core packages can be consumed without Orleans, database, ontology, or vendor-specific dependencies.
- [ ] Optional integrations can evolve without forcing unrelated dependency upgrades.
- [ ] Forbidden dependency directions fail CI.

## FND-05 — Public contract compatibility

- [ ] Define semantic-versioning rules for F# records, discriminated unions, interfaces, and serialized contracts.
- [ ] Version durable events, Orleans state, knowledge records, traces, and evaluation reports.
- [ ] Define unknown-field and unknown-case behavior.
- [ ] Add golden compatibility fixtures for supported persisted versions.
- [ ] Add migration hooks and dry-run validation.
- [ ] Define deprecation periods and release-note requirements.
- [ ] Test rolling upgrade compatibility where multiple runtime versions can coexist.

**Acceptance criteria**

- [ ] Supported old state can be read or migrated by the current release.
- [ ] Incompatible state fails with an actionable diagnostic before mutation.
- [ ] Public breaking changes require an explicit major-version decision.

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
