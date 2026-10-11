# 2026-10 grounded-answer contracts

## Two-phase grounding

Grounded generation does not turn model output directly into a verified answer.
`KnowledgeGroundedGenerationResult` contains either:

- `ProposedAnswer`, whose citation assertions still require deterministic verification;
- `InsufficientEvidence`, which is a successful typed result rather than an execution failure.

This boundary prevents model-produced citation syntax from being presented as verified provenance.

## Answer proposals

`KnowledgeGroundedAnswerProposal` separates:

- trimmed answer text;
- ordered, non-overlapping material claim ranges;
- explicit source-quotation or generated-interpretation semantics for every claim;
- asserted citation references;
- per-claim confidence;
- answer-level confidence.

Claims retain the exact answer substring represented by their offset and length. Claim identifiers
and citation-reference identifiers must be unique. Citation markers must exactly encode their
declared chunk identity.

A proposal may assert a citation-reference identifier that is not present in its citation list.
This is intentional: proposals represent untrusted model output, and the deterministic citation
verifier must detect the missing reference. Supplied citation references that no claim asserts are
rejected as ambiguous.

## Insufficiency

Typed insufficiency reasons cover:

- insufficient assembled context;
- absence of material claims;
- explicit model abstention;
- policy rejection.

Every insufficiency includes a non-blank explanation. It does not use empty answer text, fabricated
claims, exceptions, or success-shaped fallback citations.
