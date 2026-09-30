# 2026-09 retrieval comparison evaluation

## Scope

`Nao.Eval` now references `Nao.Knowledge` and provides
`KnowledgeRetrievalComparison.evaluateAsync`. The evaluator compares retrieval quality for:

- the normalized original query;
- every generated query variant;
- the aggregate transformed strategy.

## Retrieval boundary

The evaluator receives a `KnowledgeRetrievalVariantRetriever`. Every invocation receives:

- the exact immutable request from the transformation plan;
- one evidenced query variant;
- cancellation.

This ensures original and transformed retrieval use identical authorization, namespace, typed
filters, retrieval strategy, limits, and budget. The retriever is responsible for mapping variant
text to the configured lexical, vector, hybrid, or graph-backed implementation.

Each variant executes exactly once. Variants run concurrently, and their measured results are
retained for both per-variant evidence and aggregate calculation.
Per-variant and transformed aggregate results must remain within the request's candidate limits.

## Metrics

Each quality measurement records:

- query count;
- distinct retrieved chunks;
- relevant chunks;
- relevant chunks retrieved;
- recall;
- precision;
- elapsed query milliseconds.

The original measurement uses ordinal-zero retrieval. For transformed strategies, the aggregate
uses the distinct union of generated-variant results and the sum of their measured query
latencies. It excludes the original result so the comparison measures the transformation itself.
When no generated variants exist, transformed and original measurements are identical.

The final comparison reports recall, precision, and latency deltas:

```text
delta = transformed - original
```

Positive recall or precision indicates improvement. Positive latency indicates additional
retrieval cost.

## Dataset requirements

Evaluation requires at least one relevant chunk identity. Empty relevance sets are invalid because
recall would be undefined. Null retrieval output fails explicitly. Duplicate retrieved identities
are collapsed before scoring.
