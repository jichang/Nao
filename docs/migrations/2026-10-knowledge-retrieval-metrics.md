# 2026-10 complete knowledge retrieval metrics

## Ranked retrieval output

`KnowledgeRankedRetrievalEvaluator.RetrieveAsync` now returns
`KnowledgeRankedRetrievalOutput` instead of a bare chunk list. The output contains:

- the ordered retrieved chunk identities;
- explicit non-negative USD cost reported by the retrieval implementation.

This is a deliberate breaking change to the unreleased API. No bare-list adapter remains.

## Per-case measurements

`KnowledgeHybridRetrievalEvaluator` measures elapsed wall-clock time around every lexical, vector,
and hybrid invocation. Each `KnowledgeRetrievalCaseMeasurement` now retains:

- retrieved ranking;
- relevant retrieved count;
- recall at the configured cutoff;
- precision at the configured cutoff;
- reciprocal rank;
- normalized discounted cumulative gain;
- measured latency;
- reported USD cost.

Null output, duplicate chunks, results beyond the cutoff, negative cost, and cases without relevant
chunk judgments fail explicitly.

## Strategy aggregates

Each `KnowledgeRetrievalStrategyQuality` retains macro-averaged recall@k, precision@k, MRR, and
nDCG together with:

- mean latency;
- nearest-rank p95 latency;
- total reported USD cost.

The existing hybrid-improvement gate continues to compare ranking quality only. Latency and cost
are evidence for subsequent absolute and baseline release thresholds rather than hidden inputs to
the quality decision.
