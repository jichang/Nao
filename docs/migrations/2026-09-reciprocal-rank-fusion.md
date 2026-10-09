# 2026-09 reciprocal-rank fusion

## Scope

`KnowledgeReciprocalRankFusionExecutor.fuse` combines completed lexical and vector retrieval
evidence for hybrid and hybrid-with-graph requests.

Fusion accepts only legs produced from the exact same immutable query-transformation plan and
admission decision. Combining evidence from merely equivalent reconstructed requests is rejected
because it would weaken the provenance chain.

## Score semantics

Lexical full-text scores and vector-distance scores are not directly comparable. Fusion therefore
uses weighted reciprocal rank and deliberately ignores raw score magnitude:

```text
contribution(strategy, list, candidate) =
    strategy_weight / (rank_constant + one_based_rank)

fused_score(candidate) =
    sum(all contributions for candidate)
```

The default configuration uses:

- rank constant: `60`;
- lexical weight: `1.0`;
- vector weight: `1.0`.

The constant and both strategy weights are explicit, finite, and positive. A candidate contributes
once for every lexical or vector query-variant list in which it appears. Repeated presence across
strategies or transformed queries is therefore consensus evidence, while duplicate occurrence
inside one ranking list is invalid.

The fused score is a rank-fusion score. It is neither a probability nor a normalized similarity,
and it must not be compared across executions with different configurations or numbers of input
lists.

## Evidence

Every fused candidate records:

- its final one-based rank;
- its fused score;
- every contributing retrieval leg;
- the exact query variant;
- source-list rank;
- raw adapter score;
- configured weight;
- computed reciprocal-rank contribution.

Raw scores are retained for auditability but do not affect fusion. The result also retains both
complete retrieval legs, `nao-reciprocal-rank-fusion@1`, configuration, input-list count, and raw
input-candidate count.

## Determinism and bounds

Candidates sort by:

1. fused score descending;
2. best contributing rank ascending;
3. canonical chunk identity ascending.

The request's total candidate limit caps fused output. The same chunk identity must resolve to an
identical indexed document across all legs; inconsistent records fail the operation.
