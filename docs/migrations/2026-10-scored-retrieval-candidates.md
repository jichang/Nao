# 2026-10 scored retrieval candidates

## Unified candidate surface

`KnowledgeRetrievalCandidateAssembler.assemble` combines diversity-selected candidates with
optional parent-child and graph-neighborhood expansion. Every supplied expansion must reference
the exact same `KnowledgeRetrievalDiversitySelection`; evidence from independently constructed
pipelines cannot be mixed.

The result contains each chunk identity once. Candidate order and final one-based rank are:

1. diversity-selected chunks in selected rank order;
2. expanded parents in parent expansion order;
3. expanded graph neighbors in graph expansion order.

If multiple stages reach the same chunk, the existing candidate gains another typed evidence
entry instead of being duplicated. Inconsistent documents under one chunk identity fail the
operation.

## Strategy-specific score semantics

`KnowledgeCandidateStrategyEvidence` preserves the score domain that produced each candidate:

- `ReciprocalRankFusion` retains the selected fused candidate, its weighted RRF score, source
  ranks, raw lexical/vector scores, weights, variants, and contributions.
- `ParentChildExpansion` uses the maximum fused score among the evidenced selected children as
  its derived support score. It retains every supporting selected candidate and the exact
  parent-child expansion evidence.
- `GraphNeighborhoodExpansion` retains the graph adapter's raw score, depth, path, and expansion
  rank.

These values are not normalized into one cross-strategy scale. `primaryScore` returns the first
evidence score according to stage precedence, while callers can inspect every evidence entry when
a document was discovered through multiple strategies.

## Bounds and failure behavior

The assembler rejects:

- parent or graph evidence from another selection;
- parent evidence referencing a child outside the selected set;
- expanded parents without matching relationship evidence;
- inconsistent documents sharing one chunk identity;
- a unique aggregate candidate count above the request limit.

The assembler performs no provider or index calls and adds no token, model-call, embedding-call,
or monetary cost.
