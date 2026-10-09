# 2026-09 metadata-filtered retrieval

## Scope

Metadata-filtered retrieval is now an explicit, evidenced boundary shared by lexical and vector
retrieval. It is not a separate ranking strategy: typed filters constrain the candidate universe
before lexical or vector ranking.

## Complete typed filter

Every retrieval leg passes the unchanged `KnowledgeIndexFilter` from the immutable request into
each index query. The filter supports:

- source identities;
- immutable document-version identities;
- exact prefixed source, version, and chunk metadata;
- source-modification lower and upper bounds;
- ingestion lower and upper bounds.

PostgreSQL adapters apply these predicates inside SQL before ranking and limiting. Other index
implementations are held to the same contract.

## Evidence and defense in depth

Successful lexical and vector retrieval now contain `KnowledgeRetrievalFilterEvidence`:

- `nao-retrieval-filter-boundary@1`;
- the exact filter instance from the request;
- the number of returned candidates revalidated against that filter.

The executor revalidates every returned candidate after the index call. Revalidation covers the
namespace and authorization boundary as well as every typed-filter dimension. A missing source
modification timestamp does not satisfy a requested source-modification range. Any widened adapter
result fails the whole retrieval operation rather than being silently discarded.

Filtering remains inside the index query for correctness, security, ranking quality, and bounded
work. Executor revalidation is defense in depth, not post-retrieval filtering.
