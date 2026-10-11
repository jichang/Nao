# 2026-10 knowledge evaluation datasets

## Unified dataset contract

The retrieval-only `KnowledgeRetrievalQualityCase` and `KnowledgeRetrievalQualityDataset` contracts
were removed. `KnowledgeEvaluationCase` and `KnowledgeEvaluationDataset` now provide one
knowledge-evaluation source of truth for retrieval and grounded generation.

Each case records:

- a stable case ID and query;
- source, format, language, tenant-policy, and query-type dimensions;
- relevant document and chunk identities;
- expected facts with stable IDs and their supporting relevant chunks;
- prohibited document or chunk targets with review reasons.

Cases reject missing relevance, duplicate fact IDs, expected facts backed by non-relevant chunks,
duplicate prohibited targets, and targets that are simultaneously relevant and prohibited.

Datasets carry Nao schema version 1, a positive dataset revision, and a deterministic SHA-256
content hash. The hash covers schema version, name, revision, ordered cases, queries, dimensions,
sorted relevance judgments, expected facts and evidence, and prohibited targets and reasons.

## JSON fixtures

`KnowledgeEvaluationDatasetJson` uses public mutable DTOs and ordinary `System.Text.Json`
serialization. JSON fixtures retain their expected content hash. Deserialization reconstructs all
typed identities and domain contracts, then rejects a content-hash mismatch.

Unknown object properties follow normal serializer behavior. Unknown prohibited-result kinds,
unsupported schema versions, malformed identities, null required arrays, contradictory judgments,
and tampered governed content fail explicitly.

## Hybrid retrieval evaluation

`KnowledgeHybridRetrievalEvaluator` now consumes `KnowledgeEvaluationDataset`. Its current metrics
require at least one relevant chunk per evaluated case. Document relevance, expected facts, and
prohibited results remain available to subsequent document-level, groundedness, citation, and
prohibited-result evaluators.

This is a deliberate breaking change to the unreleased API. No aliases or adapters for the
retrieval-only dataset remain.
