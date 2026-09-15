# Knowledge and RAG

This workstream creates a reusable knowledge subsystem for ingestion, indexing, retrieval, grounding, citations, provenance, and lifecycle management. Knowledge is external source-backed information; it is not the same as agent memory or conversation context.

**Milestone:** R2
**Dependencies:** Foundations; production deployment additionally requires governance and identity
**Proposed ownership:** `Nao.Knowledge` contracts plus optional parser, embedding, vector, lexical, and graph adapters

## Existing baseline

- [x] Functional `SemanticMemory` and `EmbeddingProvider` capability records exist.
- [x] In-memory, file, and ADO.NET semantic-memory implementations exist.
- [x] Functional `GraphMemory` and graph-query contracts exist.
- [x] Conversation compaction and tiered agent memory exist.
- [x] A source-to-index knowledge lifecycle exists as an authorization-partitioned in-memory reference.
- [ ] Retrieval has production indexing, authorization, citations, and quality gates.

## Architecture boundaries

- [x] Define **knowledge** as versioned content derived from authoritative external sources.
- [x] Define **memory** as information learned or retained through agent/user activity.
- [x] Define **context** as transient material selected for one model call.
- [ ] Define **artifact** as an addressable produced or consumed object with lineage.
- [x] Keep source ingestion independent of a particular agent or orchestration pattern.
- [x] Keep vector, lexical, graph, parser, and object-storage vendors behind optional adapters.
- [x] Permit retrieval without generation and ingestion without an LLM.

### Document parsing boundary

- [x] Keep parser discovery, trust enforcement, resource limits, quarantine, and knowledge-unit conversion in `Nao.Knowledge`.
- [x] Maintain `Nao.Documents` as a separate reusable document-model and format-conversion package.
- [x] Integrate the packages through optional `Nao.Knowledge.Documents`; do not pull document-format dependencies into core knowledge contracts.
- [x] Distinguish exact text ranges from package structural paths instead of fabricating byte offsets for ZIP/XML formats.
- [x] Require callers to provide compressed-package limits explicitly.
- [x] Add new document readers to `Nao.Documents` first, then expose only provenance-safe readers through `Nao.Knowledge.Documents`.

## KNO-01 — Knowledge contracts and records

- [x] Define source, document, document version, content unit, chunk, index entry, citation, and retrieval-result contracts.
- [x] Assign stable IDs and content hashes.
- [x] Capture source URI, media type, language, timestamps, ownership, classification, and custom metadata.
- [x] Capture derivation lineage from source bytes through parsing, chunking, enrichment, embedding, retrieval, and answer citation.
- [x] Represent parser, chunker, embedding-model, and schema versions.
- [x] Define confidence and trust metadata separately.
- [x] Define tombstones, retention, legal hold, and deletion status.
- [x] Version all durable contracts.

**Acceptance criteria**

- [x] Every retrieved passage can be traced to an immutable source version and location.
- [x] Reprocessing creates a new derived version without corrupting previous evidence.
- [x] Deletion state propagates atomically to every index and cache in the reference catalog.

## KNO-02 — Source connectors

- [x] Define pull, push, snapshot, and change-feed connector contracts.
- [x] Support filesystem/directory ingestion as the reference connector.
- [x] Add HTTP/web ingestion with robots, redirect, size, and media-type policies.
- [x] Add Git repository ingestion with commit identity and path filters.
- [x] Define database and object-storage connector extension points.
- [x] Support include/exclude filters and maximum-content limits.
- [x] Capture connector checkpoints for incremental synchronization.
- [x] Implement retries, backpressure, quarantine, and dead-letter records.
- [x] Apply identity, tenant, and network policy to connector execution.

**Acceptance criteria**

- [x] Re-running an unchanged source does not duplicate documents or chunks.
- [x] Changed, moved, and deleted sources converge correctly.
- [x] One failed item does not corrupt a complete ingestion run.

## KNO-03 — Parsing and normalization

- [x] Define parser discovery by media type and signature.
- [x] Provide plain-text, Markdown, JSON, HTML, DOCX, ODT, and PDF integration paths.
- [x] Preserve headings, tables, code blocks, links, exact source offsets, and package structural paths where available.
- [x] Preserve page number and visible-page bounds through paginated-format parsing adapters.
- [x] Normalize encoding and Unicode without destroying source location mapping.
- [x] Detect language through an optional detector and route OCR-required documents explicitly.
- [x] Treat parsed text as untrusted data and mitigate prompt-injection instructions.
- [x] Bound parser time, input size, expansion ratio, and output size.
- [x] Enforce hard parser memory limits through an isolated execution boundary.
- [x] Quarantine malformed, encrypted, unsupported, or suspicious files.

**Acceptance criteria**

- [x] Parser fixtures verify text and location fidelity for supported text, Markdown, JSON, HTML, DOCX, ODT, and PDF formats.
- [x] Archive bombs and oversized documents fail safely.
- [x] Parsed instructions never become privileged system instructions by default.

## KNO-04 — Chunking and enrichment

- [x] Define a versioned chunker contract.
- [x] Implement token-window chunking with overlap.
- [x] Implement structure-aware chunking using headings, paragraphs, tables, and code boundaries.
- [x] Implement parent-child chunks for broad retrieval and focused context.
- [x] Define semantic chunking as an optional strategy with pinned model/configuration.
- [x] Preserve source offsets and parent hierarchy.
- [x] Add deterministic metadata extraction.
- [x] Add optional LLM entity/topic/summary extraction with provenance and confidence.
- [x] Detect near-duplicate content.
- [x] Measure chunk-size distributions and truncated-content rates.

**Acceptance criteria**

- [x] Chunk generation is deterministic for deterministic strategies and pinned configuration.
- [x] Every chunk cites a valid source range.
- [x] Chunk strategy can be changed through a resumable re-indexing migration.

## KNO-05 — Embeddings

- [x] Define embedding capability metadata: model ID, dimensions, normalization, distance metric, limits, and version.
- [x] Support batching, cancellation, retries, throttling, and cost accounting.
- [x] Validate dimensions before persistence.
- [x] Add content-hash embedding caches scoped by model and preprocessing version.
- [x] Define migration and dual-read behavior for model upgrades.
- [x] Support local and hosted embedding providers.
- [x] Record input provenance without storing prohibited raw content in telemetry.

**Acceptance criteria**

- [x] Mixed dimensions or model identities cannot enter one incompatible index.
- [x] Re-embedding can resume after interruption.
- [x] Embedding calls appear in budgets, traces, and evaluations.

## KNO-06 — Indexes and storage adapters

- [ ] Define a vector index interface distinct from in-process semantic memory.
- [ ] Define lexical/full-text index and optional graph-index interfaces.
- [ ] Support namespace, tenant, workspace, ACL, metadata, time, and source-version filters.
- [ ] Implement one production vector adapter, with pgvector or Qdrant as an initial candidate.
- [ ] Implement one production lexical adapter.
- [ ] Define index creation, health, migration, rebuild, backup, and restore operations.
- [ ] Add bulk upsert/delete and transactional or compensating update semantics.
- [ ] Prevent stale chunks from remaining queryable after deletion.
- [ ] Benchmark corpus size, index time, recall, latency, memory, and cost.

**Acceptance criteria**

- [ ] Retrieval does not require loading all embeddings into process memory.
- [ ] ACL and tenant filters are applied inside the query boundary, not only after retrieval.
- [ ] Rebuilding from source records yields equivalent searchable content.

## RAG-01 — Query understanding

- [ ] Define a retrieval request with query, identity, filters, strategy, limits, and budget.
- [ ] Add deterministic normalization.
- [ ] Add optional query rewriting, decomposition, hypothetical-document, and multi-query strategies.
- [ ] Preserve the original query and all generated variants in evidence.
- [ ] Guard generated queries against authorization-scope expansion.
- [ ] Skip retrieval for clearly non-knowledge tasks through an explicit policy.

**Acceptance criteria**

- [ ] Query transformations never remove mandatory tenant or ACL filters.
- [ ] Evaluation can compare original-query and transformed-query retrieval.

## RAG-02 — Hybrid retrieval and fusion

- [ ] Implement lexical retrieval.
- [ ] Implement vector retrieval.
- [ ] Implement metadata-filtered retrieval.
- [ ] Implement reciprocal-rank fusion or another documented fusion algorithm.
- [ ] Add configurable diversity and duplicate suppression.
- [ ] Add parent-child expansion.
- [ ] Add optional graph-neighborhood expansion.
- [ ] Return scored candidates with strategy-specific evidence.
- [ ] Bound candidate count, latency, and cost.

**Acceptance criteria**

- [ ] Hybrid retrieval outperforms or justifies parity with single-strategy baselines on versioned datasets.
- [ ] Score semantics and fusion behavior are documented and tested.

## RAG-03 — Reranking and context assembly

- [ ] Define a reranker contract.
- [ ] Support deterministic score-based, cross-encoder, and optional LLM rerankers.
- [ ] Record reranker model/configuration and candidate score changes.
- [ ] Enforce authorization again before context assembly as defense in depth.
- [ ] Assemble context within token and source-diversity budgets.
- [ ] Preserve citation markers through formatting and truncation.
- [ ] Detect conflicting or stale sources.
- [ ] Prefer authoritative and current sources through explicit policy.
- [ ] Represent insufficient evidence rather than forcing context.

**Acceptance criteria**

- [ ] Assembled context never contains an unauthorized candidate.
- [ ] Context stays within the declared token budget.
- [ ] Every included statement range maps to citation evidence.

## RAG-04 — Grounded generation and citations

- [ ] Define grounded-answer contracts separating answer text, claims, citations, confidence, and insufficiency.
- [ ] Require models to cite retrieved evidence through a response protocol.
- [ ] Validate citation existence and source ranges deterministically.
- [ ] Detect unsupported material claims.
- [ ] Add configurable abstain, repair, warn, and reject behavior.
- [ ] Distinguish source quotation from generated interpretation.
- [ ] Preserve citation provenance in output artifacts and audit records.
- [ ] Protect against retrieved prompt injection and source spoofing.

**Acceptance criteria**

- [ ] Invalid citations cannot be presented as verified citations.
- [ ] The system can return “insufficient evidence” without treating it as execution failure.
- [ ] Groundedness and citation correctness meet release thresholds.

## RAG-05 — Retrieval evaluation

- [ ] Create versioned datasets with queries, relevant documents/chunks, expected facts, and prohibited results.
- [ ] Measure recall@k, precision@k, MRR, nDCG, latency, and cost.
- [ ] Measure context relevance, answer faithfulness, citation correctness, and abstention quality.
- [ ] Add adversarial datasets for poisoning, prompt injection, stale sources, ambiguity, and ACL boundaries.
- [ ] Segment reports by source, format, language, tenant policy, and query type.
- [ ] Add regression thresholds and baseline promotion workflow.
- [ ] Capture production retrieval failures as reviewed dataset candidates.

## MEM-01 — Memory and knowledge integration

- [ ] Define when session events may become durable memory.
- [ ] Require consent/policy for cross-session user memory.
- [ ] Add background memory extraction with confidence and provenance.
- [ ] Resolve contradictions, corrections, expiry, and user-requested forgetting.
- [ ] Prevent unverified memory from becoming authoritative knowledge.
- [ ] Permit knowledge retrieval from memory tools without conflating storage semantics.
- [ ] Test tenant, user, group, workspace, and session boundaries.

### Exit criteria for R2

- [ ] KNO-01 through KNO-06 and RAG-01 through RAG-05 are complete for at least one production adapter set.
- [ ] MEM-01 boundaries are implemented and documented.
- [ ] Incremental ingestion, re-indexing, deletion, backup, and restore are tested.
- [ ] Retrieval quality, latency, cost, safety, and isolation gates pass.

[Back to roadmap](../roadmap.md)
