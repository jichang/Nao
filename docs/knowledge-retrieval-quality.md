# Knowledge retrieval quality evidence

## Evaluator

`KnowledgeHybridRetrievalEvaluator` executes lexical, vector, and hybrid ranked retrieval exactly
once for every case in a `KnowledgeEvaluationDataset`. Dataset schema version, name, positive
revision, ordered case identity, query, source/format/language/tenant-policy/query-type dimensions,
sorted document/chunk relevance, expected facts and supporting evidence, and prohibited results
produce a deterministic SHA-256 content identity.
Hybrid retrieval metrics require relevant chunk judgments; the additional fields are retained for
later grounded-generation and prohibited-result evaluators.

At one fixed cutoff, the evaluator reports per-case rankings and macro-averaged:

- recall;
- precision;
- mean reciprocal rank;
- normalized discounted cumulative gain.

Every case also records evaluator-measured wall-clock latency and retrieval-reported USD cost.
Strategy aggregates retain mean latency, nearest-rank p95 latency, and total cost. Retrieval output
with negative cost fails before metrics are published.

Each strategy additionally groups its cases by source, normalized format, language, tenant policy,
and typed query classification. Every segment reports case count, the four ranking metrics, mean
and p95 latency, and total cost. Multi-source and multi-format cases belong to every applicable
slice, so segment totals intentionally overlap.

## Regression gates and baseline promotion

`KnowledgeQualitySnapshot.ofHybridRetrieval` and
`KnowledgeQualitySnapshot.ofGroundedGeneration` project reports into governed release evidence.
Snapshots include global metrics and every available segment metric.

`KnowledgeQualityGateConfig` assigns an absolute release threshold and maximum baseline regression
to each required metric key, plus a minimum evaluated case count. Higher quality must stay above
its floor and within the permitted decrease. Latency and cost must stay below their ceiling and
within the permitted increase. Missing required metrics, insufficient samples, or either type of
threshold failure blocks promotion.

Baseline comparisons require the exact dataset content hash and evaluator family. A passing result
may be promoted only with authenticated ownership and a non-blank review justification. The
version-1 baseline records the threshold configuration hash and links to the previous baseline,
making promotion history explicit.

## Production failure candidates

`KnowledgeDatasetCandidate.capture` records production retrieval failures without directly
altering evaluation datasets. Capture requires consent and retention evidence plus a mandatory
redactor. Candidate records retain the redacted query, redaction count, failure kind, correlation,
observed chunk identities, and evaluation dimensions, but not raw queries or removed values.

Review is permission-gated and either rejects the candidate or supplies complete dataset
judgments. Approval constructs an ordinary `KnowledgeEvaluationCase`; therefore contradictory,
missing, or malformed judgments fail before promotion. Dataset promotion has its own permission
decision and accepts only approved, unique, unexpired candidates. Promotion either creates a new
dataset or advances an existing dataset revision while retaining candidate and promoter
provenance.

Hybrid metrics are compared independently with the better lexical or vector baseline. A gate
passes when hybrid materially improves at least one metric without exceeding parity tolerance on
another metric. Exact parity requires a recorded non-blank justification.

## Reproducible hybrid reference fixture

The repository fixture `hybrid-retrieval` revision `1` contains two deterministic cases evaluated
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

The focused tests additionally verify deterministic dataset hashing, JSON fixture round trips and
tamper rejection, exact strategy invocation counts, cutoff propagation, duplicate-ranking
rejection, latency and cost aggregation, negative-cost rejection, and acceptance-decision
semantics.

## Grounded generation quality

`KnowledgeGroundedGenerationEvaluator` uses the dataset's expected facts and relevant chunks
together with grounded-output audit evidence. It reports macro context relevance, answer
faithfulness, citation correctness, and abstention accuracy.

The grounded-generation report applies the same five dataset dimensions. Each segment retains case
count, all available generation-quality metrics, abstention accuracy, model calls, tokens, cost,
and support-evaluation duration.

The measurements remain deterministic over the recorded evidence:

- context chunks are relevant only when present in the case's relevant-chunk judgments;
- a published claim is faithful only when its recorded semantic support verdict is `Supported`;
- a citation is correct only when the support evaluator selected its reference for that claim and
  the referenced chunk is relevant;
- cases with expected facts require an answer, while cases without expected facts require
  abstention.

Accepted and warned outputs are published answers. An explicit abstention is an abstention.
Repair-required and rejected outputs are unresolved and fail response-decision accuracy. Empty
denominators produce an unavailable metric, and case identities must exactly match the versioned
dataset before any report is returned.

## Adversarial RAG corpus

`KnowledgeAdversarialDatasets.adversarialV1` provides ten synthetic source chunks and five
evaluation cases:

| Case | Relevant behavior | Prohibited or guarded behavior |
|---|---|---|
| Poisoning | Prefer the authoritative two-reviewer release policy | Exclude contradictory unverified guidance |
| Prompt injection | Return the verified administration-portal procedure | Suppress retrieved model-directed instructions |
| Stale source | Use the current `/v2/events` endpoint | Exclude retired `/v1/events` guidance |
| Ambiguity | Preserve both conflicting authoritative schedules | Abstain because no expected fact resolves the conflict |
| ACL boundary | Return only the public restriction policy | Never retrieve the unauthorized synthetic budget document |

Every fixture chunk records trust, freshness, access, and typed adversarial risks. The corpus and
all scenario judgments participate in a deterministic content hash. Each adversarial case derives
an ordinary `KnowledgeEvaluationCase`, so existing retrieval and grounded-generation evaluators
can execute it directly.

Run the focused grounded-generation evidence:

```bash
dotnet test tests/Nao.Eval.Tests/Nao.Eval.Tests.fsproj -c Release \
  --filter FullyQualifiedName~KnowledgeGroundedGenerationEvaluationTests
```

Run the adversarial dataset contract evidence:

```bash
dotnet test tests/Nao.Eval.Tests/Nao.Eval.Tests.fsproj -c Release \
  --filter FullyQualifiedName~KnowledgeAdversarialDatasetTests
```
