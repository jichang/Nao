# 2026-10 parent-child retrieval expansion

## Searchable hierarchy

`KnowledgeIndexDocument` now preserves the indexed chunk's `KnowledgeChunkRole` and optional
`ParentChunkId`. `restore` requires both values and rejects parent chunks that themselves reference
a parent. The old restore shape was removed because Nao is unreleased.

PostgreSQL vector and lexical tables now persist `chunk_role` and `parent_chunk_id`. Their result
readers restore both fields, and table constraints reject unsupported roles or nested parent
references. A partial parent-identity index supports hierarchy operations.

Existing development index tables must be rebuilt from source records to receive the new schema.
No compatibility columns or legacy restore overloads are retained.

## Resolution boundary

`KnowledgeParentResolver` resolves requested parent identities under:

- the exact request `AuthorizationScope`;
- namespace;
- complete typed index filter;
- an explicit parent count bound;
- cancellation.

`PostgresKnowledgeParentResolver` implements this contract against the lexical index table.
Authorization, active-source state, namespace, typed filters, parent role, requested identities,
ordering, and limits are applied inside SQL.

## Expansion

`KnowledgeParentChildExpansionExecutor.expandAsync` operates after diversity selection. It:

1. gathers unique parent identities in selected-candidate order;
2. limits resolution by configuration and remaining request candidate capacity;
3. resolves every eligible parent once;
4. revalidates authorization and filters;
5. requires a parent role with no parent of its own;
6. requires every child and parent to share source and immutable document version;
7. retains parent-to-child evidence.

Each requested relationship produces one outcome:

- expanded with the resolved parent document;
- unavailable because the authorized resolver returned no parent;
- omitted by the parent limit.

Multiple selected children referencing one parent produce one resolved parent and one evidence
entry containing all child identities. Selected candidates remain unchanged.

## Bounds and failure behavior

Selected candidates plus expanded parents never exceed the retrieval request's total candidate
limit. The request duration budget bounds the resolver invocation and timeout actively cancels it.
Null results, duplicate or unrequested parents, incorrect roles, mismatched relationships, and
cross-source or cross-version parents fail the operation.
