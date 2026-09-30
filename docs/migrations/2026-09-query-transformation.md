# 2026-09 query transformation strategies

## Scope

Retrieval requests now select a `KnowledgeQueryTransformationStrategy`:

- `None`;
- `Rewrite`;
- `Decompose`;
- `HypotheticalDocument`;
- `MultiQuery`.

`KnowledgeQueryTransformation.transformAsync` executes the selected strategy through an injected
`KnowledgeQueryTransformationModel`. Knowledge remains independent of provider APIs, prompt
formats, and model vendors.

## Transformation model boundary

The model receives:

- the complete authenticated `AuthorizationScope`;
- the normalized original query;
- the selected strategy;
- maximum generated variants;
- maximum generated characters;
- the request's retrieval budget.

The model returns text variants and usage evidence. Usage contains model calls, input/output
tokens, and estimated USD cost. The transformation rejects missing or invalid usage, budget
overruns, null output, incorrect strategy cardinality, and excessive variants.

Rewriting and hypothetical-document generation require exactly one generated result.
Decomposition and multi-query require at least one. All model strategies require room beyond the
original variant and a positive model-call budget.

## Variant evidence

Every `KnowledgeQueryTransformationPlan` contains the normalized original as ordinal zero plus all
unique generated variants. Each variant records:

- typed origin;
- stable ordinal;
- normalized text;
- content hash;
- optional model and configuration provenance.

Generated output passes through the same NFKC and whitespace normalizer as the original. Rewrite,
decomposition, and multi-query results use the query-character limit. Hypothetical documents use
the separate generated-text limit because they are intentionally longer than search queries.

The original user text remains available through the plan's `KnowledgeNormalizedQuery`.

## Authorization invariants

Model output contains text and usage only. It has no authorization, namespace, filter, strategy,
limit, or budget fields to replace. The plan retains the exact original request object, making
authorization-scope expansion structurally impossible at this boundary.

Retrieval execution must read authorization and filters from
`KnowledgeQueryTransformationPlan.request`, never from generated text or model metadata.

## Request construction

`KnowledgeRetrievalRequest.create` accepts task intent after the typed filter, followed by the
retrieval and query-transformation strategies. `KnowledgeRetrievalLimits.create` distinguishes the
original/query-variant character limit from the hypothetical-document generated-text limit.
