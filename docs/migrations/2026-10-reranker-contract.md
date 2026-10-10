# 2026-10 reranker contract and deterministic scoring

## Capability boundary

`KnowledgeReranker` is the vendor-independent reranking capability. Its descriptor records:

- component identity and version;
- score-based, cross-encoder, or language-model kind;
- optional model identity;
- configuration hash;
- deterministic or non-deterministic execution.

Score-based rerankers cannot declare a model. Cross-encoder and language-model rerankers must
declare one, and language-model rerankers cannot claim deterministic execution.

`KnowledgeRerankRequest` carries the exact `KnowledgeRetrievalCandidates`, maximum output count,
and an explicit duration, model-call, token, and cost budget. The capability receives the
retrieval request's complete `AuthorizationScope`; callers do not reconstruct identity from
candidate metadata.

## Output validation and evidence

A reranker returns exactly one finite score for every input chunk identity. Missing, duplicate,
unknown, null, or non-finite scores fail the operation. The executor owns final ordering:

1. reranker score descending;
2. original candidate rank ascending;
3. canonical chunk identity ascending.

The configured output limit is applied only after every input has been scored. Each retained
candidate records:

- original candidate and complete retrieval evidence;
- original rank and primary strategy score;
- reranked rank and score.

The overall result retains the complete reranker descriptor, request, metered usage, and duration.
Timeout actively cancels the capability.

## Deterministic score-based reranking

`KnowledgeScoreBasedReranker` is the reference deterministic implementation. Its score is:

```text
rankWeight / (rankConstant + originalRank)
  + evidenceBreadthWeight * strategyEvidenceCount
```

This avoids treating heterogeneous lexical, vector, parent-support, and graph scores as one
comparable scale. Configuration is validated and content-hashed into the reranker descriptor.
Default configuration preserves retrieval order through normalized original rank alone and has
zero model calls, tokens, and monetary cost.

## Model-assisted adapters

`KnowledgeCrossEncoderReranker` adapts `KnowledgeCrossEncoderScorer` and allows the adapter to
declare deterministic execution. `KnowledgeLanguageModelRerankerAdapter` adapts the optional
`KnowledgeLanguageModelReranker` capability and is always marked non-deterministic.

Both adapters receive a `KnowledgeModelRerankRequest` containing only:

- the unchanged original retrieval query;
- ordered candidate chunk identities and text;
- maximum model calls;
- maximum tokens;
- maximum cost.

`KnowledgeModelRerankerConfig` pins model identity, configuration hash, query characters, and
per-candidate characters. Character limits are enforced before model invocation. Model-assisted
outputs must record at least one model call; the shared executor then validates exact score
coverage, usage budgets, ordering, and timeout behavior. Provider transport, credentials,
batching, and tokenizer-specific accounting remain host-adapter responsibilities.
