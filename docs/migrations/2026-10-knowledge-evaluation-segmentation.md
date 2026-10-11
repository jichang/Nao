# Knowledge evaluation report segmentation

## Dataset dimensions

Every `KnowledgeEvaluationCase` now requires `KnowledgeEvaluationDimensions` containing:

- one or more source identities;
- one or more normalized source formats;
- a valid culture language tag;
- a tenant-policy label;
- a typed query classification: fact lookup, procedural, comparison, exploratory, or
  troubleshooting.

These dimensions participate in the version-1 dataset content hash and DTO-based JSON fixtures.
Missing sources, formats, language, or tenant policy fail during reconstruction.

This deliberately changes `KnowledgeEvaluationCase.create`; no dimensionless constructor remains.

## Segmented reports

Retrieval strategy reports now include a `KnowledgeRetrievalSegmentQuality` for every observed
source, format, language, tenant policy, and query type. Each slice retains case count, ranking
metrics, latency, and cost.

Grounded-generation reports similarly include `KnowledgeGenerationSegmentQuality` slices with
context relevance, answer faithfulness, citation correctness, abstention accuracy, evaluator
usage, duration, and cost.

A case with multiple sources or formats participates in each applicable slice. Segment costs and
case counts therefore overlap and must not be summed to reconstruct global totals. Global report
metrics remain calculated directly from the complete case set.
