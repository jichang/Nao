# 2026-10 deterministic grounded citation verification

## Verification boundary

`KnowledgeGroundedCitationVerifier.verify` is the only path from a grounded-answer proposal to
`KnowledgeVerifiedGroundedAnswer`. It accepts the exact assembled context and either:

- returns a verified answer with resolved claim-to-citation provenance;
- preserves typed insufficient evidence;
- rejects the complete proposal with typed verification failures.

Proposals and model citation assertions are never implicitly upgraded to verified artifacts.

## Context validation

Before resolving model assertions, the verifier rechecks every assembled context item:

- chunk identities are unique;
- statement offsets and lengths fit the final rendered context;
- the statement text equals the context item text;
- the statement SHA-256 hash matches the citation evidence;
- the stable marker occurs immediately before the statement;
- every source location is non-empty and fits the quoted statement.

These checks bind verification to the final rendered context rather than trusting an earlier
formatting stage.

## Citation resolution

For every material claim, each asserted citation-reference ID must:

- be defined by the proposal;
- identify a chunk present in the assembled context;
- carry the exact marker retained by that context citation.

Claims declared as source quotations must additionally occur exactly in at least one resolved
quoted context statement. Generated interpretations do not use substring matching and proceed to
semantic support evaluation.

Verified citations retain the reference ID, complete `KnowledgeContextCitation`, and exact quoted
text. The context citation preserves source, document, immutable version, numeric version, chunk,
source locations, rendered statement range, and quoted-content hash.

Any failure rejects the entire proposal. Invalid citations cannot coexist with a
`KnowledgeVerifiedGroundedAnswer`.
