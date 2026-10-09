# Knowledge retrieval quality evidence

## Evaluator

`KnowledgeHybridRetrievalEvaluator` executes lexical, vector, and hybrid ranked retrieval exactly
once for every case in a `KnowledgeRetrievalQualityDataset`. Dataset name, version, ordered case
identity, query, and relevance judgments produce a deterministic SHA-256 content identity.

At one fixed cutoff, the evaluator reports per-case rankings and macro-averaged:

- recall;
- precision;
- mean reciprocal rank;
- normalized discounted cumulative gain.

Hybrid metrics are compared independently with the better lexical or vector baseline. A gate
passes when hybrid materially improves at least one metric without exceeding parity tolerance on
another metric. Exact parity requires a recorded non-blank justification.

## Reproducible hybrid reference fixture

The repository fixture `hybrid-retrieval` version `1` contains two deterministic cases evaluated
at cutoff 2:

| Strategy | Recall | Precision | MRR | nDCG |
|---|---:|---:|---:|---:|
| Lexical | 0.750000 | 0.500000 | 0.750000 | 0.622038 |
| Vector | 0.750000 | 0.500000 | 1.000000 | 0.806574 |
| Hybrid | 1.000000 | 1.000000 | 1.000000 | 1.000000 |

Against the better single-strategy value for each metric, hybrid records:

- recall delta: `+0.250000`;
- precision delta: `+0.500000`;
- MRR delta: `0.000000`;
- nDCG delta: approximately `+0.193426`.

The decision is `Improved`: hybrid improves recall, precision, and nDCG while preserving the best
baseline reciprocal rank. The fixture also verifies that metric parity fails without a
justification and succeeds only when the justification is retained in evidence.

## Replay

Run the focused evidence:

```bash
dotnet test tests/Nao.Eval.Tests/Nao.Eval.Tests.fsproj -c Release \
  --filter FullyQualifiedName~KnowledgeHybridRetrievalEvaluationTests
```

The focused tests additionally verify deterministic dataset hashing, exact strategy invocation
counts, cutoff propagation, duplicate-ranking rejection, and acceptance-decision semantics.
