# 2026-10 aggregate hybrid retrieval bounds

## End-to-end execution boundary

`KnowledgeHybridRetrievalPipeline.executeAsync` is the production orchestration boundary for
`Hybrid` and `HybridWithGraph` requests. It executes:

1. normalization, admission, and optional query transformation;
2. concurrent lexical and vector retrieval over the exact same understanding plan;
3. reciprocal-rank fusion and diversity selection;
4. optional parent expansion;
5. optional graph expansion;
6. unified scored-candidate assembly.

Lexical-only and vector-only requests are rejected because they do not satisfy this pipeline's
hybrid contract. `HybridWithGraph` requires an explicit graph stage. An explicit non-knowledge
admission result returns `Skipped` before index or embedding infrastructure is invoked.

## Shared duration

One linked cancellation source starts before normalization and is scheduled against the request's
complete maximum duration. The same token flows through transformation, embedding, index queries,
parent resolution, and graph traversal. Budget expiry becomes an aggregate `TimeoutException`;
caller cancellation remains caller cancellation.

Per-stage timeout checks remain defense in depth, but they no longer permit each sequential stage
to consume the request's full duration independently. Synchronous fusion and assembly are measured
and validated after completion even though they cannot be interrupted.

## Candidate allocation

The request's unique final-candidate limit is allocated in deterministic stage order:

1. diversity-selected fused candidates;
2. expanded parents;
3. graph neighbors.

After parent expansion, the pipeline reduces the configured graph-neighbor limit to the remaining
capacity. A zero-neighbor graph configuration is a valid bounded no-op. Unified assembly
deduplicates convergent evidence and rejects any aggregate overflow.

Raw lexical and vector candidate counts remain evidence of ranking work; they are not added
together and treated as final candidates because the two retrieval legs intentionally observe
overlapping ranking lists.

## Aggregate usage evidence

`KnowledgeHybridRetrievalUsage` records:

- query-transformation model calls;
- query-embedding provider calls;
- transformation and embedding input tokens;
- transformation output tokens;
- combined estimated USD cost;
- raw lexical and vector candidate observations;
- final unique candidate count;
- end-to-end duration.

The pipeline validates calls, total tokens, cost, final candidates, and duration against the
immutable request after every stage completes. Transformation and query embedding retain their
earlier pre-invocation and result-validation limits, so aggregate accounting supplements rather
than replaces provider-bound enforcement.
