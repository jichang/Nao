# 2026-10 retrieval diversity and duplicate suppression

## Scope

`KnowledgeRetrievalDiversitySelector.select` applies deterministic post-fusion selection to a
`KnowledgeReciprocalRankFusion` result.

The v1 policy can configure:

- maximum selected candidates;
- maximum candidates from one source;
- maximum candidates from one immutable document version;
- exact-content duplicate suppression.

Each optional group limit must be positive and cannot exceed the selected-candidate limit. The
selected-candidate limit cannot exceed the immutable retrieval request's total candidate limit.

## Selection semantics

Candidates are considered greedily in deterministic fused-rank order. The first eligible
candidate is retained, preserving its complete fusion evidence and score. Selected candidates
receive contiguous one-based selection ranks without changing their fused scores.

Suppression precedence is:

1. selected-candidate limit;
2. exact content hash already selected;
3. per-source limit;
4. per-document-version limit.

Every fused candidate receives one typed decision:

- selected with its selection rank;
- exact-content duplicate of a previously selected chunk;
- source limit;
- document-version limit;
- selected-candidate limit.

The result retains `nao-retrieval-diversity@1`, configuration, the complete fusion input, selected
candidates, and all selection decisions.

## Duplicate boundary

Exact suppression uses the indexed chunk content hash, not text comparison. It can therefore
suppress byte-identical normalized chunk content across different sources or document versions
without recomputing content.

Near-duplicate detector findings are not currently stored in the retrieval index contract.
The diversity selector does not infer near duplicates from text or fused scores. Near-duplicate
suppression should be added only when retrieval receives durable, provenance-bearing duplicate
relationships. Parent-child expansion remains a separate roadmap stage because indexed retrieval
documents likewise do not currently carry parent identity.

## Determinism and safety

The selector performs no provider calls and adds no model, embedding, token, or monetary cost.
It never expands authorization scope or adds candidates. Disabling exact suppression and group
limits preserves fused order and candidates up to the configured selection limit.
