# 2026-09 vector retrieval

## Scope

`Nao.Knowledge` now provides:

- a dedicated query-embedding contract;
- `KnowledgeVectorRetrievalExecutor.executeAsync`;
- versioned, per-variant vector retrieval evidence.

## Query embeddings

Query embeddings do not use `EmbeddingExecution`. That executor is intentionally source-backed:
its inputs, persistence provenance, cache identity, and accounting records refer to durable
knowledge chunks. Inventing fake chunk identities for user queries would corrupt that model.

`KnowledgeQueryEmbedder` is instead a purpose-specific capability. It receives:

- the complete `AuthorizationScope`;
- normalized query text and stable content hashes;
- maximum provider calls;
- remaining token budget;
- remaining cost budget.

It returns exactly one vector for every requested query hash plus provider-call, input-token, and
estimated-cost evidence. The vector executor validates the result against the embedder's immutable
`EmbeddingCapabilityDescriptor`, including profile identity, dimensions, and finite values.

Query transformation usage is subtracted before constructing the embedding limits. Transformation
and query embedding therefore share the request's token and cost budgets rather than each
receiving the full allowance.

## Execution

Explicitly skipped requests require neither an embedder nor an index. Vector, hybrid, and
hybrid-with-graph requests may execute the vector leg; lexical-only requests fail before invoking
the embedder.

All query variants are embedded in one bounded capability invocation. The resulting vector
queries execute concurrently. Every index invocation receives the original request's:

- complete authorization scope;
- namespace;
- typed source, document-version, metadata, and time filter;
- per-query candidate limit;
- exact embedding profile.

The duration budget covers query embedding and every vector-index query. Expiry actively cancels
the complete operation.

## Evidence and validation

Successful retrieval records:

- `nao-vector-retrieval@1`;
- the admission and transformation plan;
- the complete embedding profile;
- query-embedding usage and duration;
- each query variant;
- ordered candidates with profile ID, raw adapter score, and one-based rank;
- total duration, query count, and raw candidate count.

Per-query and aggregate raw-candidate limits fail closed. Returned hits are revalidated against
the expected embedding profile, namespace, authorization boundary, and complete typed filter.
Candidates remain unfused and undeduplicated so the hybrid layer can apply an explicit fusion
policy without losing strategy evidence.
