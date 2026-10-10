# Memory and Knowledge

Nao distinguishes conversation context, agent memory, source-backed knowledge, and produced artifacts. Treating all four as “memory” creates incorrect trust, retention, and deletion behavior.

The memory layer exposes eight functional capability records rather than interfaces: `MemoryStore`, `EmbeddingProvider`, `SemanticMemory`, `EpisodicMemory`, `GraphMemory`, `TieredMemory`, `WorkingMemory`, and `MemoryConsolidation`. Concrete in-memory, file-system, and ADO.NET behavior lives in `Nao.Persistence.Memory` where applicable. Applications can use that package directly or opt into the aggregate `Nao.Persistence` composition package.

Source-backed records live separately in `Nao.Knowledge`. A `KnowledgeSource` establishes the
authoritative URI, authorization identity, classification, trust assessment, metadata, and
lifecycle. Immutable document versions derive stable identifiers from logical document identity,
version number, and canonical SHA-256 content hash. Parsed content units, chunks, index entries,
retrieved passages, and citations retain exact document-version and source-location lineage.
Parser, chunker, embedding, preprocessing, and other derivation components carry explicit
versions. Confidence is a bounded numeric assessment and is not conflated with source trust.
Lifecycle records distinguish retention, legal hold, tombstone, and purge state.

`KnowledgeCatalog.inMemory` is the deterministic reference lifecycle adapter. It partitions all
state by the complete authenticated identity and atomically commits a related source, document
version, units, chunks, and index entries only after validating their lineage. Retrieval-cache
entries must reference chunks from the same document version. Source tombstone and purge
operations update every related document, version, unit, chunk, index entry, and cache entry in
one locked transition; any legal hold blocks the entire mutation. Production durable and indexed
adapters remain KNO-03 through KNO-06 work.

Source acquisition uses vendor-neutral pull, push, snapshot, and change-feed contracts.
`FilesystemSourceConnector` is the local reference pull/snapshot implementation. It binds opaque,
content-hashed checkpoints to its connector identity, reads raw bytes without a parser or LLM,
and applies source-owner, knowledge-index, directory-list, and per-file read authorization before
the corresponding operation. Recursive enumeration excludes reparse points, supports canonical
include/exclude globs, and fails the whole synchronization rather than truncating when the item
limit is exceeded. Per-item access, I/O, and content-size failures are reported independently;
the prior checkpoint state for a failed known item is retained so a transient failure cannot
become a false deletion. Unique-content renames retain stable item identity, while ambiguous
renames converge as independent deletion and addition.

`HttpSourceConnector` provides HTTP(S) pull/snapshot acquisition through host-injected transport
and robots capabilities. Every initial and redirected URI is independently checked against the
authenticated web-read permission, path-aware network allowlist, and robots decision before the
transport is invoked. The transport receives the same network policy and byte limit so the host
can enforce DNS, resolved-address, socket, and streaming limits at the actual network boundary;
the connector validates declared and received sizes again. Redirect count, HTTP status,
media type, credentials, fragments, and conditional ETag/Last-Modified checkpoints fail closed.

`GitSourceConnector` acquires immutable commit snapshots from a host-injected repository
capability. It supports local file repositories and allowlisted HTTPS repositories, pins every
checkpoint and content item to a canonical SHA-1 or SHA-256 commit identity, filters canonical
repository-relative paths, excludes `.git` metadata, bounds item and blob sizes, and reuses the
same deterministic move/delete reconciliation as filesystem ingestion. Local repositories
require list authorization before repository access and read authorization for every selected
file; remote readers receive the network policy for enforcement at clone/fetch boundaries.
Duplicate paths, path traversal, mismatched unchanged commits, and changed content under an
immutable commit identity fail closed.

`SourceSynchronizationRunner` composes operational resilience around any pull connector without
embedding scheduling behavior in connector implementations. Fixed and bounded exponential retry
policies retry only failures classified as retryable by the platform taxonomy. Batch submission
rejects work above an explicit backlog bound and uses a semaphore to cap active synchronization.
Successful batches persist every isolated item failure as a v1 `QuarantinedSourceItem`; exhausted
or non-retryable whole-run failures persist a v1 `DeadLetteredSourceSync` and return an explicit
dead-letter outcome. Caller cancellation and failure-store errors propagate rather than being
retried or converted into success. Records contain redacted messages, opaque IDs, authenticated
ownership, connector/source identity, attempt counts, and timestamps. The owner-partitioned
in-memory failure store is the reference implementation; production durable storage remains
adapter-owned.

KNO-03 parsing begins at a signature-aware `ParserRegistry`; signatures take precedence over
declared media types so mislabeled binary content cannot be routed to a permissive text parser.
`KnowledgeParsingService` verifies the source content hash, enforces input bytes, timeout, unit
count, output characters, and expansion ratio, and returns an explicit quarantine outcome for
unsupported, malformed, suspicious, encrypted, or OCR-required content. Parser crashes remain
visible dependency failures rather than being mislabeled as document defects. Every successful
parse is marked `UntrustedSource`; parser output cannot elevate itself into system instructions.

`PlainTextParser` is the deterministic core text parser. It accepts strict UTF-8 with an
optional BOM, separates paragraphs, normalizes line endings and Unicode to NFC, and retains the
original byte, character, and line span for every normalized unit. The normalized unit carries
an explicit untrusted marker. `JsonParser` validates JSON while retaining its complete source
range. The optional `Nao.Knowledge.Documents` package adapts the separately packaged
`Nao.Documents` model and its Markdig and HtmlAgilityPack readers into located Markdown
and HTML knowledge units. `Nao.Documents` distinguishes exact UTF-8 ranges from structural addresses inside
document packages. Markdown and HTML produce exact byte, character, and line ranges; DOCX and ODT
produce stable paths within `word/document.xml` and `content.xml` without fabricated offsets.
Callers must provide entry-count, per-entry, total-expanded-byte, and compression-ratio limits
before a package reader can execute. The Nao adapter records provenance precision on every unit
and rejects missing provenance. RTF's internally generated HTML coordinates are deliberately
discarded because they do not identify the original RTF bytes, so RTF is not exposed as a
knowledge parser. PDF text extraction uses PdfPig and produces paginated content with page number,
visible-page bounds in points, and a bottom-left coordinate origin. Malformed and encrypted PDFs
have distinct quarantine outcomes, while image-only PDFs explicitly require OCR. Optional
language detection and OCR routing are implemented.

Public `Nao.Knowledge.Documents` parsers execute through `Nao.Knowledge.Documents.Worker`; their
in-process implementations are internal to the worker and tests. Parser construction rejects
local and process environments because their sampled memory limits are not hard guarantees. A
host must provide a container execution environment that advertises hard memory enforcement,
plus explicit memory, CPU, duration, output, and package-expansion limits. Each parse carries its
authenticated authorization scope into a newly created worker context rather than retaining a
cross-session `AgentContext`. Document bytes use the explicit opaque-untrusted worker payload
mode so text redaction cannot corrupt source or parsed output; stderr, failures, and captured
diagnostics remain redacted. Memory, CPU, duration, and output violations become typed suspicious
quarantine outcomes.

KNO-04 chunking starts with the v1 `KnowledgeChunker` contract and deterministic
`StructureAwareChunker`. The descriptor pins both the chunker component version and a canonical
configuration hash; both participate in chunk identity and derivation lineage. Heading-delimited
sections are grouped without splitting paragraphs, tables, code blocks, or other parsed units.
Bounded section parents support broad retrieval, while one leaf per source unit supports focused
retrieval. Every parent and leaf retains the contributing content-unit IDs and exact, structural,
or page-region source locations. Parent references must point to an earlier parent chunk from the
same committed document bundle, making the hierarchy acyclic and fail-closed. Token-window and
semantic strategies, enrichment, duplicate detection, metrics, and resumable re-indexing remain
future KNO-04 work.

`TokenWindowChunker` provides deterministic overlapping windows over canonical parsed-unit text.
Its tokenizer is an explicit versioned capability, and the built-in `UnicodeWordTokenizer`
offers a vendor-neutral Unicode word/punctuation strategy rather than pretending to reproduce a
model vendor's tokenization. Window size, overlap, and tokenizer identity are pinned into the
chunker configuration hash. Windows may cross unit boundaries, but every window retains all
contributing unit IDs and their truthful source locations. Token indices and canonical-text
character spans are metadata, not fabricated byte offsets into the original file.

`SemanticChunker` is an optional unit-boundary strategy driven by a separately supplied
`SemanticBoundaryModel`. The model contract requires a pinned component version, dimensions,
configuration hash, determinism declaration, batch operation, and cancellation. Adjacent source
units remain together while their cosine similarity meets the configured threshold, subject to a
hard maximum-unit bound. Model output must contain one finite, correctly dimensioned vector per
unit or chunking fails without producing partial output. The caller's authenticated authorization
scope is passed to every model invocation. Embeddings are used transiently to choose boundaries
and are not persisted as chunk metadata.

`DeterministicMetadataEnricher` derives stable retrieval facets from the authoritative document
units after any chunking strategy. It records content kinds, unit count, active heading hierarchy,
links, pages, and structural source paths under a reserved `derived.*` namespace. The enricher
validates document lineage and ordered unique units, refuses caller-supplied reserved metadata,
and records a versioned enrichment derivation. Reapplying the same extraction is idempotent;
metadata that disagrees with recorded lineage fails closed.

`ModelAssistedEnricher` optionally produces typed entity, topic, and summary records rather than
placing opaque model JSON in chunk metadata. Each result carries bounded confidence, pinned model
and configuration identity, an explicit model-generated-untrusted marker, and one or more exact
quotes tied to contributing content units and their source locations. The authenticated scope is
passed into the model capability. Response cardinality, chunk identity, value size, confidence,
evidence count, evidence ownership, exact quotes, and duplicate results are validated before any
enriched chunks are returned. Low-confidence results are deliberately excluded by the configured
threshold, and transient model output is never promoted to instructions or deterministic
metadata.

`ShingleNearDuplicateDetector` performs bounded deterministic duplicate analysis over a
caller-selected, authorization-scoped chunk set. It normalizes Unicode word tokens, hashes pinned
word shingles, uses an inverted shingle index to avoid comparing unrelated chunks, and reports
canonical exact or near-duplicate pairs with Jaccard similarity. Detector, tokenizer, threshold,
shingle size, and resource limits participate in stable pair identity. Empty or punctuation-only
content is not treated as duplicate, and chunk, candidate-pair, and result limits fail closed.
Detection records inform later indexing policy without mutating or discarding source chunks.

Every `ChunkingResult` includes a metrics snapshot rather than requiring callers to reconstruct
observability from chunk payloads. It reports input and output counts, parent/leaf counts,
character-size minimum, maximum, mean, p50, and p95, and optional token-size distributions when
the strategy has an authoritative tokenizer. Truncated unit and character counts use explicit
input denominators and expose rates without treating overlap as lost or duplicated source
coverage. Current structure-aware, token-window, and semantic strategies are lossless and report
zero truncation.

`ChunkingMigration` changes the pinned strategy across an explicit, canonically ordered set of
document versions. Its stable v1 migration identity covers the target chunker contract,
component, configuration, and document-version plan. For each document, the reference catalog
atomically replaces the prior chunks, invalidates index entries and retrieval-cache entries that
could cite those chunks, and advances an owner-partitioned revision checkpoint. A process that
fails between documents resumes from the next committed cursor; a completed migration is an
idempotent no-op. Supplied chunks must match the target strategy and document lineage, concurrent
checkpoint changes fail closed, and legal hold prevents replacement.

KNO-05 embedding metadata uses a dedicated v1 `EmbeddingCapabilityDescriptor`. A stable profile
identity covers the model component and version, output dimensions, normalization, distance
metric, preprocessing component and version, and all advertised limits. Limits declare maximum
batch size, characters and tokens per item, and tokens per batch. Descriptor creation and catalog
persistence recompute this identity and reject unsupported contract versions, invalid dimensions,
incoherent limits, or forged records. This knowledge-index contract is intentionally separate
from the simpler conversational-memory embedding capability.

`EmbeddingExecution` prepares source-backed embedding work through provider-owned token counting
and deterministic batches bounded by both item count and total input tokens. Every batch has a
stable ID derived from its embedding profile and ordered chunk/content identities, allowing
providers and accounting adapters to make retries idempotent. The executor propagates
authenticated scope and cancellation, reacquires the host throttle before every provider
attempt, retries only failures marked retryable by the platform taxonomy, and records one v1 cost
entry after each successful response. Cost records contain batch/profile/chunk identities,
counts, attempts, and estimated USD only; they do not copy source text. Provider responses must
return exactly one finite, correctly dimensioned vector for every requested chunk under the
pinned profile. Malformed output is a non-retryable failure and is never costed as a successful
batch.

Every successful provider batch also emits a private validated v1
`EmbeddingProvenanceRecord` before cost accounting completes. The stable batch identity binds
the ordered chunk IDs and canonical content hashes to the embedding profile. Each input records
only its chunk ID, content hash, character count, and authoritative preflight token count; batch
metadata adds provider-reported input tokens, estimated cost, attempt count, and start/completion
times. Raw text, vectors, provider responses, error messages, principals, and authentication
evidence are structurally absent. The recorder receives `AuthorizationScope` separately so
durable adapters can partition records by complete identity without persisting credentials or
claims. Recorder failure is not ignored and prevents the execution from reporting success.

Within a governed harness execution, every provider batch reserves a distinct embedding-call
budget before transport and records provider-reported tokens and cost afterward. Embedding calls
do not masquerade as chat LLM calls. The runtime emits a typed metric and a text-free
`embedding.generate` child span keyed only by profile and stable batch identities and numeric
usage. It also adds a text-free `EmbeddingCall` step to the execution trace consumed by
evaluations. Exceeding `MaxEmbeddingCalls`, total-token, or cost limits produces the same
structured execution-limit outcome as other governed resources; a zero-call budget blocks
before provider invocation.

Search storage is defined independently from agent semantic memory. Vector indexes accept only
validated records bound to one complete embedding profile; lexical indexes receive source-backed
text records; graph indexes are optional and store explicit chunk-to-chunk edges. Every operation
requires an `AuthorizationScope`. Tenant, workspace, session, and ACL constraints therefore form
the mandatory query boundary rather than optional post-retrieval filters. Callers may additionally
filter by namespace, source, immutable document version, prefixed source/version/chunk metadata,
source-modification time, and ingestion time.

`Nao.Knowledge.Postgres` implements the vector contract with PostgreSQL and pgvector. An adapter
instance is bound to one complete embedding profile and creates a profile-derived table with a
fixed `vector(dimensions)` column and the matching cosine, inner-product, or Euclidean HNSW
operator class. Profile metadata is recorded and verified before use. `ApplyAsync` accepts one
explicit mutation containing bulk upserts plus chunk-ID and/or typed-filter deletion. PostgreSQL
applies the deletion union and upserts in one owner-scoped transaction, rolls back every change
on failure, and reports upserted/deleted counts only after commit. Source retirement additionally
records a permanent owner/namespace/source tombstone under a transaction lock, deletes every
source row, and rejects later upserts. Queries require an active source-state row inside the SQL
boundary, so a retired source remains unqueryable even if physical cleanup is interrupted.
Queries apply tenant, workspace,
session, user/group/role ACL, namespace, source/version, metadata, and time predicates in SQL
before nearest-neighbor ordering. The host owns the configured `NpgsqlDataSource`, so credential
resolution, pooling, rotation, and disposal remain deployment concerns.

The same package implements lexical retrieval with PostgreSQL full-text search. Each configured
PostgreSQL text-search configuration receives a stable table and marker, persisted `tsvector`,
and GIN index. Configuration names are resolved through PostgreSQL's `regconfig` boundary rather
than interpolated into SQL. Indexing uses `to_tsvector`; queries use `websearch_to_tsquery` and
`ts_rank_cd`. Atomic mutation, source/version and metadata/time filters, and
tenant/workspace/session/user/group/role authorization share the vector adapter's implementation,
so lexical and vector results cannot diverge on access-control or transaction semantics.

Index benchmarks use `KnowledgeIndexBenchmark` to emit versioned JSON evidence for corpus size,
index duration and throughput, recall, query-latency percentiles, managed allocation, physical
adapter storage, and explicit USD cost. PostgreSQL storage uses `pg_total_relation_size`; benchmark
fixtures report zero cost because their embeddings and queries invoke no billed provider. See
[Knowledge index benchmarks](knowledge-index-benchmarks.md) for the reproducible workload and
reference evidence.

RAG retrieval begins with `KnowledgeRetrievalRequest`. It preserves the exact original query and
binds it to the complete `AuthorizationScope`, namespace, typed index filter, vendor-independent
retrieval strategy, bounded query/candidate fan-out, and explicit duration/model/embedding/token/
cost budget. The boundary accepts authorization scope rather than a bare identity so query
understanding cannot discard tenant, workspace, session, group, role, or principal evidence.
Zero provider-call and cost budgets are valid for deterministic lexical retrieval.

`KnowledgeQueryNormalizer` deterministically converts the original query to Unicode NFKC,
collapses Unicode whitespace to single ASCII spaces, trims through that canonicalization pass,
and rejects non-whitespace controls. It deliberately preserves case and punctuation. The
`KnowledgeNormalizedQuery` evidence retains the unchanged request, versioned normalizer
descriptor, and original/normalized content hashes, so normalization cannot discard authorization,
filters, limits, or budget and cannot overwrite the original user input.

`KnowledgeQueryTransformation` optionally applies rewrite, decomposition, hypothetical-document,
or multi-query generation through a provider-independent model capability. Every plan retains the
same request and normalized original as variant zero, then records normalized, deduplicated
generated variants with typed origin, ordinal, hash, and model/configuration provenance. Models
can return only text plus metered usage, not replacement authorization or filters. Duration,
model-call, token, cost, generated-character, and variant limits fail closed.

`KnowledgeRetrievalAdmissionPolicy` evaluates explicit task intent before transformation.
Knowledge-seeking and unspecified tasks retrieve; only explicit non-knowledge intent skips. The
conservative unspecified behavior prevents uncertain classification from silently suppressing
relevant sources. `KnowledgeQueryUnderstanding.planAsync` returns typed skip or retrieval-plan
evidence and never invokes the transformation model for a skipped task.

`Nao.Eval` compares original and transformed retrieval with
`KnowledgeRetrievalComparison.evaluateAsync`. It executes every evidenced variant exactly once
with the same immutable request, measures per-variant recall, precision, and latency, and compares
the ordinal-zero original against the union of generated-variant results. Comparison evidence
includes original/transformed measurements and recall, precision, and latency deltas.

`KnowledgeLexicalRetrievalExecutor` implements the production lexical retrieval leg over an
admitted query-understanding result. Skipped requests do not require an index. Retrieval plans run
every evidenced query variant exactly once against `KnowledgeLexicalIndex`, passing the immutable
authorization scope, namespace, typed filter, and per-query limit. Evidence retains the versioned
executor, original plan, raw adapter score and rank, per-variant timing, total candidate count,
duration, and zero lexical provider cost. The executor fails closed on null or over-limit output,
actively cancels on duration-budget expiry, and revalidates returned documents against namespace,
ACL, and every typed filter before exposing them. Raw candidates remain unfused and undeduplicated
for the later hybrid-fusion policy.

`KnowledgeVectorRetrievalExecutor` implements the corresponding vector leg. Query embeddings use a
dedicated `KnowledgeQueryEmbedder` instead of source-backed `EmbeddingExecution`; queries therefore
never acquire fake chunk identities or document-provenance records. One bounded embedding request
contains every normalized variant by stable content hash and returns metered provider-call, token,
and cost evidence under the index's complete embedding profile. Transformation usage is deducted
before calculating query-embedding token and cost limits. The executor then runs profile-pinned
vector queries concurrently with the immutable authorization, namespace, typed filter, and
per-query limit. Its duration budget covers embedding and index access, and returned hits are
revalidated against profile, namespace, ACL, and filter boundaries. Evidence retains raw
per-variant ranks and scores for later fusion.

Metadata-filtered retrieval is orthogonal to ranking strategy. Lexical and vector executors pass
the request's exact `KnowledgeIndexFilter` into every index query, where production adapters apply
source, immutable document-version, prefixed metadata, source-modification, and ingestion
predicates before ranking and limiting. Successful results include versioned
`KnowledgeRetrievalFilterEvidence` containing the exact filter and validated candidate count.
Every returned document is revalidated against all filter dimensions; this is defense in depth,
not a substitute for filtering inside the index boundary.

`KnowledgeReciprocalRankFusionExecutor` combines lexical and vector evidence only when both legs
retain the exact same immutable query plan and admission decision. Weighted reciprocal-rank fusion
uses `weight / (rankConstant + oneBasedRank)` for every strategy-and-variant list containing a
candidate; raw lexical and vector scores remain evidence but never become falsely comparable.
Each fused candidate retains every leg, variant, source rank, raw score, weight, and contribution.
Output ordering is deterministic by fused score, best contributing rank, then canonical chunk
identity, and the request's total candidate limit caps the result. Duplicate chunks inside one
ranking list or inconsistent documents for one chunk identity fail closed.

`KnowledgeRetrievalDiversitySelector` performs deterministic greedy selection over fused rank.
Configuration bounds the total selected candidates and optionally candidates per source and per
immutable document version, while exact-content suppression uses the indexed content hash. Every
fused candidate receives evidenced selected or suppressed status, including the selected rank or
the canonical duplicate chunk, source limit, document-version limit, or total limit. Selected
candidates retain their complete fusion evidence and score. The selector intentionally does not
infer near duplicates from text because durable near-duplicate relationships are not present in
indexed retrieval documents.

Searchable index documents now retain chunk role and optional parent identity. PostgreSQL vector
and lexical adapters persist and restore both fields, while `PostgresKnowledgeParentResolver`
resolves parent documents from the lexical table under SQL-bound authorization, active-source,
namespace, typed-filter, role, identity, and count predicates.
`KnowledgeParentChildExpansionExecutor` gathers unique parent identities from selected leaf chunks,
resolves each eligible parent once, and revalidates its request boundary, parent role, child
relationship, source, and immutable document version. Configuration and remaining request
candidate capacity bound expansion; every relationship is evidenced as expanded, unavailable, or
parent-limited. Resolution timeout actively cancels the adapter.

`KnowledgeGraphNeighborhoodExpansionExecutor` optionally follows graph edges only for
`HybridWithGraph` retrieval. Selected chunks become the authorized graph start set, while
configuration bounds edge kinds, depth, and neighbors and the request's remaining candidate
capacity provides the final result bound. Empty configured edge kinds permit all adapter-defined
edge kinds. Every returned path must begin at a selected chunk, end at its declared hit, remain
within depth, and contain no cycle. Non-selected hits are materialized through
`KnowledgeGraphDocumentResolver` with the exact authorization, namespace, typed filter, requested
chunk identities, and count limit, then revalidated at the retrieval boundary. Evidence records
expanded, already-selected, and unavailable hits with graph score, depth, path, and rank.
Ordinary lexical, vector, and hybrid strategies do not require graph infrastructure. Expansion
timeout actively cancels both graph traversal and document resolution.

`KnowledgeRetrievalCandidateAssembler` produces one ordered, deduplicated scored-candidate surface
from diversity selection and optional parent and graph expansion. It requires every input to
reference the exact same diversity selection. Selected chunks retain their weighted
reciprocal-rank fusion candidate and contribution evidence. Parent scores are explicitly derived
as the maximum fused score among their evidenced selected children; the supporting candidates and
relationship evidence remain attached. Graph neighbors retain the adapter's graph score, depth,
path, and hit evidence. These score kinds remain a typed discriminated union and are not normalized
or falsely compared across strategies. When parent and graph expansion reach the same document,
one candidate retains both evidence entries in stage order. Final ranks follow selected, parent,
then graph discovery order, and assembly fails if the unique aggregate exceeds the request's
candidate limit.

`KnowledgeHybridRetrievalPipeline` is the aggregate execution boundary for hybrid and
hybrid-with-graph retrieval. One linked deadline starts before normalization and query
understanding and remains active through lexical/vector execution, fusion, diversity, parent
expansion, graph expansion, and candidate assembly. Caller cancellation remains distinguishable
from budget expiry. Lexical and vector legs execute concurrently after one shared understanding
plan. Parent expansion receives precedence after selected candidates, and the pipeline reduces the
graph neighbor limit to the remaining candidate capacity; a zero limit is an explicit bounded
no-op. The resulting `KnowledgeHybridRetrievalUsage` reports model calls, embedding calls, input
and output tokens, estimated cost, raw lexical/vector candidate observations, final unique
candidates, and end-to-end duration. Calls, tokens, cost, duration, and final candidates are
validated again against the immutable request budget and limits after all stages complete.

`Nao.Eval` provides `KnowledgeHybridRetrievalEvaluator` for versioned lexical, vector, and hybrid
quality comparison at one ranked cutoff. Datasets carry deterministic content hashes and
non-empty relevance judgments. Evaluation invokes every strategy exactly once per case and
retains per-case rankings plus macro recall, precision, mean reciprocal rank, and nDCG. Hybrid is
accepted only when it materially improves at least one metric without exceeding the configured
regression tolerance, or when every metric is within parity tolerance and the evidence records an
explicit justification.

RAG reranking starts from `KnowledgeRerankRequest`, which binds the exact unified retrieval
candidates to an output limit and independent duration, model-call, token, and cost budget.
`KnowledgeReranker` is a vendor-independent capability for deterministic score, cross-encoder,
and language-model implementations. Its descriptor records kind, component, optional model,
configuration hash, and determinism. The executor passes the original authorization scope,
requires exactly one finite score for every candidate identity, applies deterministic score/rank/
chunk tie-breaking, actively cancels on timeout, and retains original versus reranked score and
rank evidence. `KnowledgeScoreBasedReranker` is the deterministic reference implementation; it
uses normalized original rank and optional evidence breadth rather than comparing heterogeneous
lexical, vector, parent, and graph raw scores.
`KnowledgeCrossEncoderReranker` and `KnowledgeLanguageModelRerankerAdapter` adapt host-provided
scoring capabilities to the same contract. They expose only the original query, ordered chunk
identities and text, and the request's model-call, token, and cost ceilings. Query and candidate
character limits are enforced before invocation. Cross-encoder determinism is declared by its
adapter; the optional language-model adapter is always non-deterministic. Both require metered,
nonzero model-call evidence and rely on the shared executor for exact score coverage, budgets,
ordering, and timeout cancellation.

`KnowledgeContextAssembler` consumes one reranking under the exact original
`AuthorizationScope`. It revalidates every candidate against namespace, ACL, and typed filters
before considering it for context. Deterministic greedy selection follows reranked order while
enforcing total candidates, candidates per source, and exact tokens over the final rendered text,
including separators and stable chunk markers. Every candidate receives included, candidate-limit,
source-limit, or token-limit evidence. The result is explicitly insufficient when no candidate
fits or the configured minimum evidence count cannot be met. Chunk markers are not fabricated
source citations: searchable index documents retain validated byte, character, page, line, and
path locations from their chunks. Each included context item binds its marker and source,
immutable document-version, and chunk identities to those locations, the exact statement range in
the final rendered context, and the quoted statement's SHA-256 hash. Context assembly includes
whole statements rather than truncating them, so formatting and token suppression cannot detach a
marker from its citation evidence.

Context assembly also compares the explicit document identity and positive numeric version retained
by every searchable index document. Lower versions are suppressed when a newer version of the same
document is present. A document identity claimed by multiple sources, or multiple immutable
document-version identities claiming the same document and numeric version, is treated as a
conflict; every conflicting branch is suppressed with typed evidence before context budgets are
allocated. Separate chunks from one immutable version remain valid.

Context allocation requires an explicit source-preference policy. `AuthorityThenCurrent` sorts by
the conservative minimum of source and document-version trust before freshness;
`CurrentThenAuthority` reverses those priorities. Freshness uses source modification time when
available and ingestion time otherwise, and reranker rank remains the deterministic final
tie-breaker. Every candidate decision records both trust assessments, effective trust, freshness,
reranker rank, and final preference rank. PostgreSQL indexes persist the complete trust
assessments rather than deriving authority from mutable metadata.

Index control-plane operations are represented separately from query capabilities. A
`KnowledgeIndexManagement<'Record>` implementation exposes idempotent creation, health,
revision-based migration planning/application, atomic namespace rebuild, typed backup, and
owner-validated restore. Creation, health, and migration are authorized by deployment-controlled
adapter credentials rather than an end-user scope. Rebuild, backup, and restore require
`AuthorizationScope` and operate on exactly one owner and namespace. Backups retain validated
vector or lexical records instead of opaque provider dumps, carry a v1 marker, and fail closed
when restored under another authorization identity. Backup records may contain source text and
vectors and therefore require storage protection matching the indexed source classification.

`KnowledgeIndexRebuild.vectorAsync` and `KnowledgeIndexRebuild.lexicalAsync` execute rebuilds
through the normal atomic mutation boundary. They delete the complete owner namespace and insert
the authoritative records in one transaction, including the empty-corpus case. PostgreSQL
integration tests compare complete ranked hits before and after rebuild, covering restored
documents, ordering, and scores rather than row counts alone.

Concrete provider adapters live outside the vendor-independent knowledge package. The hosted
OpenAI-compatible adapter posts ordered batches to a complete embeddings endpoint, resolves an
optional bearer credential from `SecretProvider` for each operation, verifies the returned model
and response indexes, and calculates cost from provider-reported input tokens. The local Ollama
adapter uses `/api/embed`, explicitly disables server-side truncation, verifies the returned
model, and reports zero hosted cost. Both require a tokenizer callback matching the pinned
preprocessing profile so the executor can enforce limits before transport. Neither includes
provider error bodies in platform failures, and both expose deterministic disposal for their
owned HTTP clients.

`EmbeddingPersistence` is the validated boundary between generated vectors and storage adapters.
It creates private immutable v1 records only after recomputing the embedding profile, checking
the profile identity, requiring exact dimensions, and rejecting non-finite values. Stable
persisted IDs bind one chunk to one embedding profile. The reference store partitions by the
complete authorization identity, validates an entire batch before mutation, copies arrays on
write and read, accepts an equivalent replay without replacing the first record, and rejects any
attempt to rebind an ID to different vector data. Pgvector indexing consumes these validated
records without making the embedding store itself queryable.

`CachedEmbeddingExecution` adds an authorization-partitioned, content-addressed v1 cache above
provider execution. Keys combine the canonical content hash with the complete validated
embedding profile, so model and preprocessing versions—and every other compatibility-relevant
vector property—cannot collide. Equal text from different chunks is embedded once per profile
and expanded back into ordered chunk-specific results. Only unique misses reach the provider;
cache hits retain the executor's total-input bound and do not produce provider usage or cost.
Entries contain hashes, profile metadata, vectors, and timestamps but no source text. Cache
stores validate immutable batch writes, copy vector arrays, reject conflicting key reuse, and
expose a validating restoration factory for durable adapters.

`EmbeddingMigration` backfills a new profile over a canonical, unique content-hash plan. Its
stable v1 identity binds the complete source and target profiles to that plan without retaining
source text. Before work begins, the runner requires every source-profile cache entry to exist.
It then executes target work in the target executor's bounded batches and advances an
owner-partitioned compare-and-swap checkpoint only after the target cache commit succeeds. A
failure after cache persistence but before checkpoint persistence is safe: resume repeats the
batch as cache hits and does not repeat provider work.

Model cutover is explicit through source-only, target-first, and target-only read modes.
Target-first selects the target only when the complete requested result exists under that
profile; otherwise it falls back to a complete source-profile result. It never combines vectors
from different profiles or dimensions. Target-only therefore fails closed on any missing target
entry instead of inferring readiness from partial cache population. The selected profile is
returned with every result so callers can address only its compatible index.

## Data categories

| Category | Purpose | Typical lifetime | Authority |
|---|---|---|---|
| Conversation context | Material selected for the current model call | One turn or bounded conversation | Untrusted interaction history |
| Working memory | Temporary task state | One execution | Agent/runtime-derived |
| Episodic memory | Prior events and experiences | Cross-turn or cross-session by policy | Historical evidence |
| Semantic memory | Searchable learned facts or text | Policy-controlled | Derived and potentially uncertain |
| Knowledge | Versioned content from external sources | Source lifecycle | Source-backed |
| Artifact | Addressable input or output with lineage | Workflow/retention lifecycle | Depends on producer and verification |

## Conversation windows

Window strategies prevent unbounded model input:

```fsharp
type WindowStrategy =
    | LastN of int
    | TokenBudget of maxTokens: int
    | SummarizeAfter of threshold: int
```

Summaries are derived content. They should retain the source-message range and model/configuration identity if used as durable evidence.

## Key-value and working memory

`MemoryStore` is a functional capability record whose every operation requires a host-authenticated `AuthorizationScope`. Ownership includes tenant, user, selected group, workspace, and optional session; an agent name, routing key, or model-supplied owner string cannot select a storage partition. Session-owned memory is not implicitly shared with another session or a workspace-wide scope.

The capability directly owns counted deletion for that scope and strict retention purge before a caller-supplied cutoff. In-memory, file, and ADO.NET adapters preserve entries exactly at the cutoff and isolate every read and mutation by the complete ownership lineage. File and ADO partitions use `AuthorizationIdentity.storageKey`: a lowercase SHA-256 digest of fixed-order JSON identity fields, preserving optional values and delimiters without lossy filename sanitization. Roles, claims, service identity, and authentication strength do not enter memory storage keys or entry payloads. Hosts remain responsible for authentication, access policy, and retention decisions.

File documents and ADO component schemas remain v1. The current file layout and SQL ownership columns replace the former owner-string layout directly; no legacy reader or alias is provided.

`WorkingMemory` uses an opaque `WorkingMemoryAddress` combining the authenticated `AuthorizationScope` with a host-issued `ExecutionId`. Items carry neither value, so request or model payloads cannot redirect the scratchpad. Capacity, pure reads, explicit attention changes, decay, rendering, and deletion are isolated by both complete authorization lineage and execution identity. `GetAsync` does not mutate attention; callers use `FocusAsync`, whose change is persisted. Default expiry is derived deterministically from `AddedAt`; decay and unpin operations accept an explicit effective time so replay cannot extend an item's lifetime. Scope/execution deletion includes pinned items, while strict expiry deletion removes only unpinned items before the supplied cutoff.

Working-memory v1 events use `working/<identity-sha256>/<execution-id>.jsonl` files or matching ADO.NET stream names. Address fields and authentication evidence remain outside payloads. Events record the capacity, TTL, decay, and eviction policy; replay rejects policy mismatch instead of reinterpreting state. Each operation validates and replays only its address stream, and failed appends cannot publish speculative state. These reference adapters require one writer per stream, replay on each access, and retain historical payloads and deletion tombstones without physical erasure or compaction.

`EpisodicMemory` requires `AuthorizationScope` on every operation; episodes have no owner or principal fields. Records and embeddings are keyed by the complete authorization identity and episode ID. Queries, causal links, chains, synthesis, and importance-based forgetting stay inside that scope. Chain traversal returns each reachable episode once, including in cyclic or converging link graphs. The capability also owns counted scope deletion and strict retention purge by `Timestamp`.

Episodic v1 events live in `episodic/<identity-sha256>.jsonl` files or `episodic/<identity-sha256>` ADO.NET streams. Each record event includes its computed embedding: replay never invokes an embedding provider, and provider or append failures cannot expose a partial in-memory episode. Embedding callbacks receive only episode text or the query, never authentication evidence. Without a provider, a deterministic SHA-256 word-bucket embedding replaces process-dependent string hashes; it remains a simple reference algorithm, not a production embedding model. Persistent reads and mutations validate and replay only their selected stream. These reference adapters require one writer per stream, retain deletion tombstones, and do not implement distributed writer coordination, embedding-model version isolation, stream compaction, or physical erasure of historical episode/embedding payloads.

`TieredMemory` requires `AuthorizationScope` on every operation and stores owner-free entries under the complete authorization identity and entry key. Short- and mid-term capacity is independent per scope; promotion to long-term memory does not widen session or workspace authorization. Retrieval is a pure ranking operation; callers explicitly record access at an effective time to update counts and apply promotion policy. Optional embedding callbacks see only the query and candidate values from that scope, not authentication evidence or foreign entries. Eviction also receives an effective time, uses stable ordering, and preserves entries exactly at the TTL boundary. Scope and strict creation-time cutoff deletion are counted and persist as v1 tombstones.

Tiered v1 events use `tiered/<identity-sha256>.jsonl` files or `tiered/<identity-sha256>` ADO.NET streams. Every event records the capacity, promotion, and TTL configuration used to apply it. Replay fails closed if the configured policy differs, rather than silently reinterpreting earlier events under a different eviction policy. Each operation validates and replays only the selected stream; replay does not invoke embedding providers, and failed appends cannot publish speculative state. These reference adapters require one writer per stream, replay on each access, and do not implement distributed writer coordination, stream compaction, or physical erasure of historical payloads.

```fsharp
let store = InMemoryStore.create ()
let! _ = store.SaveAsync authorization entry
let! matches = store.RecallAsync authorization "user"
```

## Memory agent and tools

A memory specialist can expose search, remember, and optional forget operations to an orchestrator. This favors deliberate recall over injecting every retained item into every prompt.

```fsharp
let policy = MemoryToolConfig.Default
let operations = MemoryTools.create policy store
let specialist =
    MemoryAgent.create orchestratorFactory provider operations
let memoryTool = MemoryAgent.asTool specialist
```

The same tool instances may serve different invocations: each operation derives ownership from `AgentContext.Authorization`, never a captured owner callback or input field. Missing authorization fails with a permission-denied result before storage access, including in unrestricted test contexts. The harness binds both delegated agents and each tool dispatch to the execution request's authorization, so substituting nested context identity cannot redirect memory access. Authentication metadata stays separate from tool inputs and search results.

`MemoryConsolidation` also requires the authorization scope for all strategies and forwards it to every read, rewrite, and deletion. Summarization prompts contain selected entry content, not the principal. Key/value, semantic, episodic, graph, and tiered memory require `AuthorizationScope` for every operation; working memory requires its authenticated execution address. Their persistent partitions use the same claim-free SHA-256 identity key and exclude authentication evidence.

Storage operations remain deterministic even when an LLM interprets vague memory requests. The forget tool's input confirmation flag is not authenticated user approval; host permission policy must authorize deletion.

## Semantic memory

`SemanticMemory` is a functional capability record for embedding-based storage and retrieval:

```fsharp
let memory = InMemorySemanticMemory.create embeddingProvider

let! _ =
    memory.StoreAsync authorization "fact-1" "The capital of France is Paris"

let! results =
    memory.RetrieveAsync authorization "French capital" 3
```

Current implementations are useful foundations but do not constitute a production vector platform. In-process cosine scanning does not provide scalable indexing, backend ACL filtering, embedding migrations, or production vector operations.

## Graph memory

`GraphMemory` represents functional operations over nodes, relations, and basic entity, predicate, neighborhood, property, and path queries. It is a property-graph-style memory abstraction; it is not an RDF/OWL ontology model and does not imply description-logic reasoning.

Every operation requires a host-authenticated `AuthorizationScope`. Nodes and relations carry no owner or principal fields: the supplied scope selects the complete authorization-identity partition, and extraction callbacks receive only text and return owner-free relations. Queries, traversal, extraction, and removal cannot cross partitions. Removing or expiring a node cascades its incident relations; scope deletion and strict cutoff purge count all removed nodes and relations.

Graph event envelopes remain v1. Files live at `graph/<identity-sha256>.jsonl`; ADO.NET events use stream `graph/<identity-sha256>`. Each operation validates and replays only the selected stream using the caller's scope, without reconstructing a principal from persisted data. Extraction appends its complete relation batch as one event and replay never calls the extractor. Failed appends cannot expose speculative projection state, and reads fail closed on corrupt current streams. These reference adapters replay the selected stream on each operation, serialize operations within an instance, and require one writer per stream; distributed writer coordination, compaction, and physical erasure of historical events are not implemented.

Parallel assertions for the same subject/predicate/object tuple, richer provenance and confidence policy, resource-level authorization, indexing, and production durable rebuilds remain future graph work. These tasks are tracked in the [ontology and reasoning roadmap](roadmap/05-ontology-logic.md).

## Knowledge architecture

The planned knowledge subsystem separates ingestion from retrieval and generation:

```text
Sources
  → connectors
  → parsing and normalization
  → chunking and enrichment
  → embeddings and indexes
  → hybrid retrieval
  → reranking
  → context assembly
  → grounded generation
  → citation verification
```

A production knowledge record should preserve:

- Stable source and version identity
- Content hashes
- Media type, language, ownership, and classification
- Parser, chunker, embedding, and schema versions
- Source locations for every derived chunk
- Lineage through retrieval and answer citations
- Retention, deletion, and legal-hold state

## RAG is more than vector search

The target retrieval pipeline includes:

- Query normalization, rewriting, and decomposition
- Lexical, vector, metadata, and optional graph retrieval
- Fusion, diversity, and duplicate suppression
- Parent-child expansion
- Reranking
- Token-budget-aware context assembly
- Citation preservation and validation
- Explicit insufficient-evidence behavior
- ACL filtering inside the query boundary

Retrieval and generation are evaluated separately. Retrieval metrics include recall, precision, reciprocal rank, nDCG, latency, and cost. Grounded generation metrics include context relevance, faithfulness, citation correctness, and abstention quality.

## Trust and prompt injection

Retrieved content is untrusted data, even when it comes from an approved source. Documents can contain instructions intended to override platform behavior.

- Keep source text separate from system and policy instructions.
- Do not grant permissions based on retrieved content.
- Preserve source identity and trust classification.
- Validate generated queries and filters.
- Require evidence for material claims.
- Detect unsupported or spoofed citations.

## Cross-session memory

Automatic long-term memory synthesis requires policy and consent:

- Define which session events may become memory.
- Preserve extraction provenance and confidence.
- Resolve corrections, contradictions, expiry, and forgetting.
- Prevent uncertain learned memory from becoming authoritative knowledge.
- Enforce user, tenant, group, workspace, and session boundaries.

## Roadmap

The complete implementation plan is in [Knowledge and RAG](roadmap/02-knowledge-rag.md). It covers contracts, connectors, parsers, chunking, embeddings, vector and lexical adapters, hybrid retrieval, reranking, citations, evaluation, deletion, and memory integration.
