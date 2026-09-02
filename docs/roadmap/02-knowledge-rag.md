# Knowledge and RAG

This workstream creates a reusable knowledge subsystem for ingestion, indexing, retrieval, grounding, citations, provenance, and lifecycle management. Knowledge is external source-backed information; it is not the same as agent memory or conversation context.

**Milestone:** R2
**Dependencies:** Foundations; production deployment additionally requires governance and identity
**Proposed ownership:** `Nao.Knowledge` contracts plus optional parser, embedding, vector, lexical, and graph adapters

## Existing baseline

- [x] `ISemanticMemory` and embedding-provider abstractions exist.
- [x] In-memory, file, and ADO.NET semantic-memory implementations exist.
- [x] `IGraphMemory` and graph-query abstractions exist.
- [x] Conversation compaction and tiered agent memory exist.
- [ ] A source-to-index knowledge lifecycle exists.
- [ ] Retrieval has production indexing, authorization, citations, and quality gates.

## Architecture boundaries

- [ ] Define **knowledge** as versioned content derived from authoritative external sources.
- [ ] Define **memory** as information learned or retained through agent/user activity.
- [ ] Define **context** as transient material selected for one model call.
- [ ] Define **artifact** as an addressable produced or consumed object with lineage.
- [ ] Keep source ingestion independent of a particular agent or orchestration pattern.
- [ ] Keep vector, lexical, graph, parser, and object-storage vendors behind optional adapters.
- [ ] Permit retrieval without generation and ingestion without an LLM.

## KNO-01 — Knowledge contracts and records

- [ ] Define source, document, document version, content unit, chunk, index entry, citation, and retrieval-result contracts.
- [ ] Assign stable IDs and content hashes.
- [ ] Capture source URI, media type, language, timestamps, ownership, classification, and custom metadata.
- [ ] Capture derivation lineage from source bytes through parsing, chunking, enrichment, embedding, retrieval, and answer citation.
- [ ] Represent parser, chunker, embedding-model, and schema versions.
- [ ] Define confidence and trust metadata separately.
- [ ] Define tombstones, retention, legal hold, and deletion status.
- [ ] Version all durable contracts.

**Acceptance criteria**

- [ ] Every retrieved passage can be traced to an immutable source version and location.
- [ ] Reprocessing creates a new derived version without corrupting previous evidence.
- [ ] Deletion state propagates to every index and cache.

## KNO-02 — Source connectors

- [ ] Define pull, push, snapshot, and change-feed connector contracts.
- [ ] Support filesystem/directory ingestion as the reference connector.
- [ ] Add HTTP/web ingestion with robots, redirect, size, and media-type policies.
- [ ] Add Git repository ingestion with commit identity and path filters.
- [ ] Define database and object-storage connector extension points.
- [ ] Support include/exclude filters and maximum-content limits.
- [ ] Capture connector checkpoints for incremental synchronization.
- [ ] Implement retries, backpressure, quarantine, and dead-letter records.
- [ ] Apply identity, tenant, and network policy to connector execution.

**Acceptance criteria**

- [ ] Re-running an unchanged source does not duplicate documents or chunks.
- [ ] Changed, moved, and deleted sources converge correctly.
- [ ] One failed item does not corrupt a complete ingestion run.

## KNO-03 — Parsing and normalization

- [ ] Define parser discovery by media type and signature.
- [ ] Provide plain-text, Markdown, JSON, HTML, and PDF integration paths.
- [ ] Preserve headings, pages, tables, code blocks, links, and source offsets where available.
- [ ] Normalize encoding and Unicode without destroying source location mapping.
- [ ] Detect language and optionally OCR requirements.
- [ ] Treat parsed text as untrusted data and mitigate prompt-injection instructions.
- [ ] Bound parser time, memory, expansion ratio, and output size.
- [ ] Quarantine malformed, encrypted, unsupported, or suspicious files.

**Acceptance criteria**

- [ ] Parser fixtures verify text and location fidelity for supported formats.
- [ ] Archive bombs and oversized documents fail safely.
- [ ] Parsed instructions never become privileged system instructions by default.

## KNO-04 — Chunking and enrichment

- [ ] Define a versioned chunker contract.
- [ ] Implement token-window chunking with overlap.
- [ ] Implement structure-aware chunking using headings, paragraphs, tables, and code boundaries.
- [ ] Implement parent-child chunks for broad retrieval and focused context.
- [ ] Define semantic chunking as an optional strategy with pinned model/configuration.
- [ ] Preserve source offsets and parent hierarchy.
- [ ] Add deterministic metadata extraction.
- [ ] Add optional LLM entity/topic/summary extraction with provenance and confidence.
- [ ] Detect near-duplicate content.
- [ ] Measure chunk-size distributions and truncated-content rates.

**Acceptance criteria**

- [ ] Chunk generation is deterministic for deterministic strategies and pinned configuration.
- [ ] Every chunk cites a valid source range.
- [ ] Chunk strategy can be changed through a resumable re-indexing migration.

## KNO-05 — Embeddings

- [ ] Define embedding capability metadata: model ID, dimensions, normalization, distance metric, limits, and version.
- [ ] Support batching, cancellation, retries, throttling, and cost accounting.
- [ ] Validate dimensions before persistence.
- [ ] Add content-hash embedding caches scoped by model and preprocessing version.
- [ ] Define migration and dual-read behavior for model upgrades.
- [ ] Support local and hosted embedding providers.
- [ ] Record input provenance without storing prohibited raw content in telemetry.

**Acceptance criteria**

- [ ] Mixed dimensions or model identities cannot enter one incompatible index.
- [ ] Re-embedding can resume after interruption.
- [ ] Embedding calls appear in budgets, traces, and evaluations.

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
