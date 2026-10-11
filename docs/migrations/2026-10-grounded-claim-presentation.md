# 2026-10 grounded claim presentation

## Explicit claim semantics

Every `KnowledgeGroundedClaim` now declares one `KnowledgeGroundedClaimPresentation`:

- `SourceQuotation` means the claim text is copied exactly from cited source evidence;
- `GeneratedInterpretation` means the claim uses generated wording supported by cited evidence.

The model response DTO requires `kind` on every claim, using `source-quotation` or
`generated-interpretation`. Missing and unsupported values are rejected. This is a deliberate
breaking change to the unreleased response protocol; no default or compatibility parser exists.

## Verification

Deterministic citation verification now proves source-quotation claims against resolved evidence.
The exact claim substring must occur in at least one cited context statement using ordinal
comparison. Otherwise the complete proposal is rejected with `SourceQuotationNotFound`.

Quotation marks or surrounding prose should not be included in a source-quotation claim range
unless those characters also occur in the source. Callers can keep punctuation outside the claim
range or classify the complete generated wording as an interpretation.

Generated interpretations are not subjected to exact substring matching. They continue through
semantic claim-support evaluation. The evaluator receives the explicit presentation kind together
with claim text and resolved citations.
