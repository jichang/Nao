# 2026-10 grounded claim support evaluation

## Semantic support boundary

Citation verification proves that asserted evidence exists in the exact assembled context. It does
not prove that the evidence entails a generated claim.

`KnowledgeGroundedClaimSupportEvaluator.evaluateAsync` adds a separate provider-neutral semantic
support boundary after deterministic citation verification. An evaluator receives every exact
material claim, its explicit source-quotation or generated-interpretation presentation, and its
resolved citation text and complete context provenance. It must return exactly one assessment for
every claim.

Each assessment records:

- a supported or unsupported verdict;
- confidence;
- the claim's citation references that actually support it;
- a non-blank explanation.

A supported assessment must identify at least one citation belonging to that claim. Assessments
cannot attribute support to another claim's evidence.

## Results and evidence

The executor returns `KnowledgeGroundedClaimSupportResult.Supported` only when every material claim
is supported. If any claim is unsupported, it returns `Unsupported` with the complete ordered
assessment set and an explicit unsupported-claim projection. Unsupported claims are a typed
quality result, not an evaluator execution failure.

Every evaluation retains the structurally verified answer, evaluator component/model/configuration
descriptor, metered model calls, tokens, cost, and measured duration.

Malformed evaluator output fails closed. Missing, duplicate, or extra assessments, unrelated
supporting citation IDs, null output, and usage beyond the configured duration, model-call, token,
or cost budget cannot produce a supported answer.

## Integration

Callers must run deterministic citation verification first and pass only a
`KnowledgeVerifiedGroundedAnswer` to the support evaluator. Citation presence alone must not be
interpreted as semantic support. Policy handling for unsupported claims—abstain, repair, warn, or
reject—is a subsequent boundary and is not implicit in evaluation.
