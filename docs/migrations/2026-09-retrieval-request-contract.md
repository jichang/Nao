# 2026-09 retrieval request contract

## Scope

RAG retrieval now starts from one immutable `KnowledgeRetrievalRequest` containing:

- the original query;
- complete `AuthorizationScope`;
- index namespace;
- typed source/version/metadata/time filters;
- explicit task intent;
- retrieval strategy;
- query-transformation strategy;
- fan-out and candidate limits;
- duration, call, token, and cost budgets.

This is a new unreleased API. Callers should construct the request directly rather than passing
independent query and identity arguments through retrieval layers.

## Authorization

The request accepts `AuthorizationScope`, not a bare user ID. Tenant, workspace, optional group and
session ownership, authenticated principal groups, roles, service identity, claims, and
authentication strength therefore remain available at every transformation and retrieval
boundary.

`KnowledgeRetrievalRequest.identity` returns the complete owning `AuthorizationIdentity` when a
storage partition key is required. Retrieval implementations must still pass the original
authorization scope to indexes so ACL groups and roles are evaluated inside the query boundary.

Task intent is `KnowledgeSeeking`, `NonKnowledge`, or `Unspecified`. It is evaluated by the
[retrieval admission policy](2026-09-retrieval-admission-policy.md) before query transformation.

## Query preservation

Creation rejects blank queries and queries exceeding `MaximumQueryCharacters`, but otherwise
preserves the exact input. It does not trim, case-fold, normalize Unicode, or collapse whitespace.
Deterministic normalization is a later transformation and must retain both original and normalized
forms as evidence. See [deterministic query normalization](2026-09-query-normalization.md).

## Strategies

`KnowledgeRetrievalStrategy` declares the requested retrieval family:

- `Lexical`;
- `Vector`;
- `Hybrid`;
- `HybridWithGraph`.

The strategy does not embed an adapter or model identity. Deployment composition selects concrete
indexes and embedding profiles while the request remains vendor-independent.

## Limits and budgets

`KnowledgeRetrievalLimits` bounds:

- query characters;
- generated hypothetical-document characters;
- generated query variants;
- candidates per query;
- candidates across the request.

All limits are positive, and the per-query candidate limit cannot exceed the total.

`KnowledgeRetrievalBudget` independently bounds:

- wall-clock duration;
- model calls;
- embedding calls;
- tokens;
- estimated USD cost.

Duration must be positive. Other budget dimensions may be zero, allowing deterministic lexical
retrieval to state explicitly that it may not invoke a model or embedding provider.
