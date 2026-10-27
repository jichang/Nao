# Ontology and Symbolic Reasoning

This optional workstream adds formal knowledge representation and deterministic reasoning behind stable Nao contracts. LLMs may translate language, propose facts, select engines, and explain results; they must not be treated as substitutes for formal reasoners.

**Milestone:** R4
**Dependencies:** Knowledge/RAG, governance, provenance, and evaluation
**Proposed ownership:** core reasoning contracts plus optional RDF, OWL, rule-engine, and SMT adapters

## Existing baseline

- [x] Functional `GraphMemory` represents graph nodes, relations, and basic graph queries.
- [x] In-memory graph traversal and persistent graph support exist.
- [x] Constitution and policy primitives can evaluate operational rules.
- [x] Keep graph memory distinct from RDF/OWL semantics and SPARQL.
- [x] Nao exposes proof-producing symbolic engines through common contracts.

## Scope decisions

- [x] Treat ordinary property graphs, RDF graphs, ontologies, rules, and constraints as related but distinct models.
- [x] Keep ontology and solver dependencies out of the general agent core.
- [x] Do not require formal reasoning for ordinary RAG workloads.
- [x] Require explicit semantics for open-world versus closed-world assumptions.
- [x] Preserve the distinction between `proven false`, `not proven`, `unknown`, and `inconsistent`.
- [x] Never promote LLM-extracted assertions to trusted facts without provenance and policy.

## GRA-01 — Production graph foundation

- [x] Correct relation removal and node-deletion semantics in existing graph implementations.
- [x] Define relation identity, uniqueness, direction, properties, temporal validity, and confidence.
- [x] Define entity identity, aliases, merge, split, and conflict behavior.
- [x] Add provenance linking nodes and relations to source versions and extraction operations.
- [x] Add tenant, workspace, and ACL boundaries.
- [x] Add indexes for entity, predicate, property, neighborhood, and path queries.
- [x] Add one production graph-store adapter.
- [x] Define backup, restore, migration, deletion, and rebuild behavior.

**Acceptance criteria**

- [x] Node deletion cannot leave forbidden dangling relations.
- [x] Every extracted relation can be traced to its source evidence.
- [x] Graph queries cannot traverse into unauthorized subgraphs.

## ONT-01 — Semantic-web contracts

- [x] Define RDF term, triple/quad, named graph, prefix, and dataset contracts or adapter mappings.
- [x] Preserve IRIs, blank nodes, literals, language tags, and datatypes losslessly.
- [x] Define mappings between Nao graph records and RDF without claiming semantic equivalence where none exists.
- [x] Support import/export of standard RDF serializations through optional adapters.
- [x] Define ontology identity, version IRI, imports, and compatibility metadata.
- [x] Keep source graph and inferred graph distinguishable.

**Acceptance criteria**

- [x] Standards fixtures round-trip without semantic data loss.
- [x] Inferred statements can never be mistaken for asserted source statements.
- [x] Tenant graph boundaries survive import, query, and export.

## ONT-02 — SPARQL integration

- [x] Define a parameterized query interface and result model.
- [x] Support `SELECT`, `ASK`, `CONSTRUCT`, and controlled update operations as separate permissions.
- [x] Apply time, result-size, graph, and complexity budgets.
- [x] Prevent unsafe string interpolation through parameter binding or validated builders.
- [x] Add local and remote endpoint adapters.
- [x] Capture endpoint, dataset version, query identity, and result provenance.
- [x] Add agent tools for schema discovery and approved query execution.

**Acceptance criteria**

- [x] Read access cannot perform updates.
- [x] Queries cannot escape authorized named graphs.
- [x] Timeouts and result limits produce structured, auditable outcomes.

## ONT-03 — OWL and ontology reasoning

- [x] Define reasoner capability metadata for supported OWL profiles and operations.
- [x] Support consistency checking, classification, realization, and entailment queries.
- [x] Represent proof/explanation availability and unsupported operations.
- [x] Version ontology, reasoner, configuration, and imported dependencies.
- [x] Isolate reasoners with enforced resource limits.
- [x] Cache conclusions only against immutable ontology and reasoner identities.
- [x] Add adapters to at least one maintained reasoner through process, service, or native integration.
- [x] Add conformance fixtures for the supported semantic subset.

**Acceptance criteria**

- [x] Inconsistent ontology is reported distinctly from a false entailment.
- [x] Results identify reasoner, ontology version, assumptions, and evidence.
- [x] Unsupported OWL semantics fail explicitly rather than approximating silently.

## LOG-01 — Common reasoning contract

- [x] Define a `ReasoningRequest` containing assertions, assumptions, query, engine requirements, limits, and identity.
- [x] Define a `ReasoningResult` with `proven`, `disproven`, `unknown`, `inconsistent`, `timeout`, and `unsupported` outcomes.
- [x] Preserve conclusions, proof/explanation, unsat core where available, source assertions, engine identity, and usage.
- [x] Distinguish monotonic and non-monotonic reasoning.
- [x] Define closed-world, open-world, and negation semantics explicitly.
- [x] Support cancellation, resource budgets, sandboxing, and audit.
- [x] Integrate reasoner results with artifacts and citations.

**Acceptance criteria**

- [x] Consumers cannot collapse `unknown` into `false` accidentally through the primary API.
- [x] Engine assumptions and semantic mode are visible in every result.
- [x] Reasoning calls are traceable and reproducible with pinned inputs.

## LOG-02 — Rule-engine integration

- [x] Define fact, rule, query, binding, and explanation mappings.
- [x] Select an initial Datalog or Prolog integration based on required semantics and deployment constraints.
- [x] Isolate engine execution and limit time, memory, recursion, output, and external predicates.
- [x] Disable filesystem, process, and network predicates by default.
- [x] Define safe host-call extension points.
- [x] Preserve rule-set versions and source provenance.
- [x] Add stratified-negation and recursion conformance tests for the supported subset.

**Acceptance criteria**

- [x] Untrusted rules cannot invoke undeclared external effects.
- [x] Rule results include bindings and derivation evidence where supported.
- [x] Semantic limitations are documented and tested.

## LOG-03 — Constraint and SMT integration

- [x] Define typed variables, domains, constraints, objectives, assumptions, and model results.
- [x] Add an optional SMT adapter such as Z3.
- [x] Use bounded integer domains and Z3 optimization for finite-domain scheduling; add a separate adapter only when a concrete scheduling requirement exceeds this model.
- [x] Support satisfiable, unsatisfiable, unknown, timeout, unbounded, and optimality status.
- [x] Capture models, unsat cores, objective values, and solver statistics where available.
- [x] Apply resource limits and isolate native solver failures.
- [x] Validate LLM-generated constraints before solver invocation.

**Acceptance criteria**

- [x] Invalid generated constraints never execute as arbitrary code.
- [x] Unsatisfiable and unknown outcomes remain distinct.
- [x] Solver version, options, and normalized problem artifact permit reproduction.

## BRG-01 — Natural-language reasoning bridge

- [x] Discover ontology vocabulary and rule/constraint schemas before translation.
- [x] Translate user language into candidate entities, facts, queries, or constraints with confidence.
- [x] Require clarification for materially ambiguous mappings.
- [x] Validate candidates against schemas and authorization policy.
- [x] Show the interpreted formal query or a safe explanation before high-impact execution where required.
- [x] Pass only validated requests to deterministic engines.
- [x] Generate explanations grounded in returned proofs, bindings, or models.
- [x] Clearly label interpretation versus formally derived conclusion.

**Acceptance criteria**

- [x] The LLM cannot fabricate a “proven” status absent a corresponding reasoner result.
- [x] Ambiguous entity resolution is measurable and evaluated.
- [x] Explanations cite formal and source evidence.

## BRG-02 — Graph-enhanced RAG

- [x] Extract candidate entities and relations during knowledge ingestion.
- [x] Link extraction to source chunks and confidence.
- [x] Use graph neighborhoods and paths as optional retrieval candidates.
- [x] Fuse graph, vector, and lexical results without discarding authorization filters.
- [x] Detect and surface conflicting evidence.
- [x] Evaluate graph contribution independently from baseline hybrid RAG.
- [x] Avoid enabling graph expansion when it degrades quality, latency, or cost.

## RSN-01 — Reasoning evaluation and safety

- [x] Create truth-maintained conformance datasets for each supported engine and semantic mode.
- [x] Test inconsistent, incomplete, adversarial, cyclic, and resource-exhausting inputs.
- [x] Evaluate language-to-formal translation independently from solver correctness.
- [x] Evaluate proof-grounded explanations independently from answer style.
- [x] Add tenant and data-exfiltration tests for remote reasoners.
- [x] Add engine/version regression gates.
- [x] Define human-review requirements for high-impact conclusions.

### Exit criteria for R4 reasoning scope

- [x] GRA-01 and LOG-01 are complete before general reasoner exposure.
- [x] At least one selected ontology, rule, or constraint integration meets its acceptance criteria.
- [x] Formal outcomes preserve provenance, assumptions, uncertainty, and engine identity.
- [x] Optional reasoning packages do not expand core dependency requirements.

[Back to roadmap](../roadmap.md)
