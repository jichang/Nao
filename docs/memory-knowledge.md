# Memory and Knowledge

Nao distinguishes conversation context, agent memory, source-backed knowledge, and produced artifacts. Treating all four as “memory” creates incorrect trust, retention, and deletion behavior.

The memory layer exposes eight functional capability records rather than interfaces: `MemoryStore`, `EmbeddingProvider`, `SemanticMemory`, `EpisodicMemory`, `GraphMemory`, `TieredMemory`, `WorkingMemory`, and `MemoryConsolidation`. Concrete in-memory, file-system, and ADO.NET behavior lives in `Nao.Persistence` where applicable.

## Data categories

| Category | Purpose | Typical lifetime | Authority |
|---|---|---|---|
| Conversation context | Material selected for the current model call | One turn or bounded conversation | Untrusted interaction history |
| Working memory | Temporary task state | One execution or session | Agent/runtime-derived |
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

`MemoryStore` is a functional capability record for structured entries scoped to an owner. Hosts provide the owner scope; agents and tools should not be able to select arbitrary users or tenants.

```fsharp
let store = InMemoryStore.create ()
let! _ = store.SaveAsync agentId entry
let! matches = store.RecallAsync agentId "user"
```

## Memory agent and tools

A memory specialist can expose search, remember, and optional forget operations to an orchestrator. This favors deliberate recall over injecting every retained item into every prompt.

```fsharp
let policy = MemoryToolConfig.Default
let operations = MemoryTools.create policy store owner
let specialist =
    MemoryAgent.create orchestratorFactory provider operations
let memoryTool = MemoryAgent.asTool specialist
```

Storage operations remain deterministic even when an LLM interprets vague memory requests.

## Semantic memory

`SemanticMemory` is a functional capability record for embedding-based storage and retrieval:

```fsharp
let memory = InMemorySemanticMemory.create embeddingProvider

let! _ =
    memory.StoreAsync agentId "fact-1" "The capital of France is Paris"

let! results =
    memory.RetrieveAsync agentId "French capital" 3
```

Current implementations are useful foundations but do not constitute a production vector platform. In-process cosine scanning does not provide scalable indexing, backend ACL filtering, embedding migrations, or production vector operations.

## Graph memory

`GraphMemory` represents functional operations over nodes, relations, and basic entity, predicate, neighborhood, property, and path queries. It is a property-graph-style memory abstraction; it is not an RDF/OWL ontology model and does not imply description-logic reasoning.

Graph records require stronger production semantics around identity, relation removal, cascading deletion, provenance, confidence, authorization, indexing, and durable rebuilds. These tasks are tracked in the [ontology and reasoning roadmap](roadmap/05-ontology-logic.md).

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
