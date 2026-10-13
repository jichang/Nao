# Grounded release qualification

## Change

`Nao.Eval` now defines an executable initial release profile for grounded-generation quality.
`KnowledgeGroundedReleaseQualification` binds qualification evidence to:

- the exact `rag-adversarial` revision 1 dataset content hash;
- the exact grounded-generation evaluator descriptor;
- at least five evaluated cases;
- context relevance of at least 0.90;
- answer faithfulness of at least 0.95;
- citation correctness of at least 0.95;
- abstention accuracy of 1.00;
- zero unresolved responses;
- one consistent support-evaluator descriptor;
- a versioned generation-adapter component and configuration hash.

Qualification returns typed failure reasons and the complete quality-gate result rather than a
success-shaped fallback. The deterministic repository reference evidence reaches 1.00 for all four
metrics across the five adversarial cases.

## Scope

This evidence satisfies the RAG-04 groundedness and citation threshold criterion. A reference
adapter is not a production adapter, so the separate R2 production-adapter qualification criterion
remains open.

## Compatibility

This is a new unreleased evaluation contract. No legacy profile, compatibility adapter, or fallback
qualification path is provided.
