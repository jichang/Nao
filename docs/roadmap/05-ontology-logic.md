# Ontology and Symbolic Reasoning

This optional workstream adds formal knowledge representation and deterministic reasoning behind stable Nao contracts. LLMs may translate language, propose facts, select engines, and explain results; they must not be treated as substitutes for formal reasoners.

**Milestone:** R4
**Dependencies:** Knowledge/RAG, governance, provenance, and evaluation
**Proposed ownership:** core reasoning contracts plus optional RDF, OWL, rule-engine, and SMT adapters

## Existing baseline

- [x] Functional `GraphMemory` represents graph nodes, relations, and basic graph queries.
- [x] In-memory graph traversal and persistent graph support exist.
- [x] Constitution and policy primitives can evaluate operational rules.
- [ ] Graph memory provides RDF/OWL semantics or SPARQL.
- [ ] Nao exposes proof-producing symbolic engines through common contracts.

## Scope decisions

- [ ] Treat ordinary property graphs, RDF graphs, ontologies, rules, and constraints as related but distinct models.
- [ ] Keep ontology and solver dependencies out of the general agent core.
- [ ] Do not require formal reasoning for ordinary RAG workloads.
- [ ] Require explicit semantics for open-world versus closed-world assumptions.
- [ ] Preserve the distinction between `proven false`, `not proven`, `unknown`, and `inconsistent`.
- [ ] Never promote LLM-extracted assertions to trusted facts without provenance and policy.

## GRA-01 — Production graph foundation

- [ ] Correct relation removal and node-deletion semantics in existing graph implementations.
- [ ] Define relation identity, uniqueness, direction, properties, temporal validity, and confidence.
- [ ] Define entity identity, aliases, merge, split, and conflict behavior.
- [ ] Add provenance linking nodes and relations to source versions and extraction operations.
- [ ] Add tenant, workspace, and ACL boundaries.
- [ ] Add indexes for entity, predicate, property, neighborhood, and path queries.
- [ ] Add one production graph-store adapter.
- [ ] Define backup, restore, migration, deletion, and rebuild behavior.

**Acceptance criteria**

- [ ] Node deletion cannot leave forbidden dangling relations.
- [ ] Every extracted relation can be traced to its source evidence.
- [ ] Graph queries cannot traverse into unauthorized subgraphs.

## ONT-01 — Semantic-web contracts

- [ ] Define RDF term, triple/quad, named graph, prefix, and dataset contracts or adapter mappings.
- [ ] Preserve IRIs, blank nodes, literals, language tags, and datatypes losslessly.
- [ ] Define mappings between Nao graph records and RDF without claiming semantic equivalence where none exists.
- [ ] Support import/export of standard RDF serializations through optional adapters.
- [ ] Define ontology identity, version IRI, imports, and compatibility metadata.
- [ ] Keep source graph and inferred graph distinguishable.

**Acceptance criteria**

- [ ] Standards fixtures round-trip without semantic data loss.
- [ ] Inferred statements can never be mistaken for asserted source statements.
- [ ] Tenant graph boundaries survive import, query, and export.

## ONT-02 — SPARQL integration

- [ ] Define a parameterized query interface and result model.
- [ ] Support `SELECT`, `ASK`, `CONSTRUCT`, and controlled update operations as separate permissions.
- [ ] Apply time, result-size, graph, and complexity budgets.
- [ ] Prevent unsafe string interpolation through parameter binding or validated builders.
- [ ] Add local and remote endpoint adapters.
- [ ] Capture endpoint, dataset version, query identity, and result provenance.
- [ ] Add agent tools for schema discovery and approved query execution.

**Acceptance criteria**

- [ ] Read access cannot perform updates.
- [ ] Queries cannot escape authorized named graphs.
- [ ] Timeouts and result limits produce structured, auditable outcomes.

## ONT-03 — OWL and ontology reasoning

- [ ] Define reasoner capability metadata for supported OWL profiles and operations.
- [ ] Support consistency checking, classification, realization, and entailment queries.
- [ ] Represent proof/explanation availability and unsupported operations.
- [ ] Version ontology, reasoner, configuration, and imported dependencies.
- [ ] Isolate reasoners with enforced resource limits.
- [ ] Cache conclusions only against immutable ontology and reasoner identities.
- [ ] Add adapters to at least one maintained reasoner through process, service, or native integration.
- [ ] Add conformance fixtures for the supported semantic subset.

**Acceptance criteria**

- [ ] Inconsistent ontology is reported distinctly from a false entailment.
- [ ] Results identify reasoner, ontology version, assumptions, and evidence.
- [ ] Unsupported OWL semantics fail explicitly rather than approximating silently.

## LOG-01 — Common reasoning contract

- [ ] Define a `ReasoningRequest` containing assertions, assumptions, query, engine requirements, limits, and identity.
- [ ] Define a `ReasoningResult` with `proven`, `disproven`, `unknown`, `inconsistent`, `timeout`, and `unsupported` outcomes.
- [ ] Preserve conclusions, proof/explanation, unsat core where available, source assertions, engine identity, and usage.
- [ ] Distinguish monotonic and non-monotonic reasoning.
- [ ] Define closed-world, open-world, and negation semantics explicitly.
- [ ] Support cancellation, resource budgets, sandboxing, and audit.
- [ ] Integrate reasoner results with artifacts and citations.

**Acceptance criteria**

- [ ] Consumers cannot collapse `unknown` into `false` accidentally through the primary API.
- [ ] Engine assumptions and semantic mode are visible in every result.
- [ ] Reasoning calls are traceable and reproducible with pinned inputs.

## LOG-02 — Rule-engine integration

- [ ] Define fact, rule, query, binding, and explanation mappings.
- [ ] Select an initial Datalog or Prolog integration based on required semantics and deployment constraints.
- [ ] Isolate engine execution and limit time, memory, recursion, output, and external predicates.
- [ ] Disable filesystem, process, and network predicates by default.
- [ ] Define safe host-call extension points.
- [ ] Preserve rule-set versions and source provenance.
- [ ] Add stratified-negation and recursion conformance tests for the supported subset.

**Acceptance criteria**

- [ ] Untrusted rules cannot invoke undeclared external effects.
- [ ] Rule results include bindings and derivation evidence where supported.
- [ ] Semantic limitations are documented and tested.

## LOG-03 — Constraint and SMT integration

- [ ] Define typed variables, domains, constraints, objectives, assumptions, and model results.
- [ ] Add an optional SMT adapter such as Z3.
- [ ] Add an optional finite-domain scheduling/optimization adapter if needed.
- [ ] Support satisfiable, unsatisfiable, unknown, timeout, and optimality status.
- [ ] Capture models, unsat cores, objective values, and solver statistics where available.
- [ ] Apply resource limits and isolate native solver failures.
- [ ] Validate LLM-generated constraints before solver invocation.

**Acceptance criteria**

- [ ] Invalid generated constraints never execute as arbitrary code.
- [ ] Unsatisfiable and unknown outcomes remain distinct.
- [ ] Solver version, options, and normalized problem artifact permit reproduction.

## BRG-01 — Natural-language reasoning bridge

- [ ] Discover ontology vocabulary and rule/constraint schemas before translation.
- [ ] Translate user language into candidate entities, facts, queries, or constraints with confidence.
- [ ] Require clarification for materially ambiguous mappings.
- [ ] Validate candidates against schemas and authorization policy.
- [ ] Show the interpreted formal query or a safe explanation before high-impact execution where required.
- [ ] Pass only validated requests to deterministic engines.
- [ ] Generate explanations grounded in returned proofs, bindings, or models.
- [ ] Clearly label interpretation versus formally derived conclusion.

**Acceptance criteria**

- [ ] The LLM cannot fabricate a “proven” status absent a corresponding reasoner result.
- [ ] Ambiguous entity resolution is measurable and evaluated.
- [ ] Explanations cite formal and source evidence.

## BRG-02 — Graph-enhanced RAG

- [ ] Extract candidate entities and relations during knowledge ingestion.
- [ ] Link extraction to source chunks and confidence.
- [ ] Use graph neighborhoods and paths as optional retrieval candidates.
- [ ] Fuse graph, vector, and lexical results without discarding authorization filters.
- [ ] Detect and surface conflicting evidence.
- [ ] Evaluate graph contribution independently from baseline hybrid RAG.
- [ ] Avoid enabling graph expansion when it degrades quality, latency, or cost.

## RSN-01 — Reasoning evaluation and safety

- [ ] Create truth-maintained conformance datasets for each supported engine and semantic mode.
- [ ] Test inconsistent, incomplete, adversarial, cyclic, and resource-exhausting inputs.
- [ ] Evaluate language-to-formal translation independently from solver correctness.
- [ ] Evaluate proof-grounded explanations independently from answer style.
- [ ] Add tenant and data-exfiltration tests for remote reasoners.
- [ ] Add engine/version regression gates.
- [ ] Define human-review requirements for high-impact conclusions.

### Exit criteria for R4 reasoning scope

- [ ] GRA-01 and LOG-01 are complete before general reasoner exposure.
- [ ] At least one selected ontology, rule, or constraint integration meets its acceptance criteria.
- [ ] Formal outcomes preserve provenance, assumptions, uncertainty, and engine identity.
- [ ] Optional reasoning packages do not expand core dependency requirements.

[Back to roadmap](../roadmap.md)
