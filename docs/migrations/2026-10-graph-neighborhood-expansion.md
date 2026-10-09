# 2026-10 graph-neighborhood retrieval expansion

## Optional strategy boundary

`KnowledgeGraphNeighborhoodExpansionExecutor.expandAsync` invokes graph infrastructure only for
`KnowledgeRetrievalStrategy.HybridWithGraph`. Lexical, vector, and ordinary hybrid retrieval
return `NotRequested` without requiring a graph index or document resolver.

Selected candidates are the graph start nodes. `KnowledgeGraphExpansionConfig` bounds traversal
depth and returned neighbors and optionally restricts edge kinds. An empty edge-kind set permits
all edge kinds supported by the adapter. The retrieval request's remaining total candidate
capacity further reduces the neighbor limit. A zero neighbor limit is an explicit no-op used when
earlier pipeline stages have consumed all remaining capacity.

## Authorized materialization

The graph index returns identities, scores, depths, and paths rather than searchable documents.
Non-selected hit identities are materialized through `KnowledgeGraphDocumentResolver`, which
receives:

- the exact request `AuthorizationScope`;
- namespace;
- complete typed index filter;
- the requested chunk identities;
- an explicit document count bound;
- cancellation.

Resolved documents are revalidated against authorization, namespace, every typed filter, the
requested identity set, uniqueness, and cardinality. Graph adapters and resolvers must enforce
the same boundary internally; executor validation is defense in depth.

## Path and evidence semantics

Every graph hit must:

- remain within the configured maximum depth;
- start at one of the selected candidates;
- end at the hit chunk identity;
- have a path length consistent with its declared depth;
- contain no repeated chunk identity.

The executor also rejects duplicate hit identities. Each accepted hit receives one typed outcome:

- `Expanded`, carrying the authorized document and expansion rank;
- `AlreadySelected`, when traversal reaches an existing selected candidate;
- `Unavailable`, when the authorized resolver cannot materialize the hit.

Expanded neighbors retain the graph hit's raw score, depth, and path as strategy-specific evidence.
They are ordered by graph-index result order after already-selected and unavailable hits are
excluded from expansion ranking.

## Bounds and failure behavior

Selected candidates plus expanded graph neighbors never exceed the retrieval request's total
candidate limit. The request duration budget bounds traversal and resolution as one operation.
Timeout actively cancels the linked token used by both dependencies. Null, over-limit, duplicate,
cyclic, unrequested, unauthorized, or filter-widening output fails the operation.
