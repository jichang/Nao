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
