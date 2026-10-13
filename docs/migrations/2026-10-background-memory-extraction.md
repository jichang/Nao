# Background memory extraction

## Boundary

`BackgroundMemoryExtraction.extractAsync` converts an approved
`CrossSessionUserMemoryCandidate` into a typed extraction batch. It does not write a memory store
and cannot accept raw session events or merely eligible candidates.

The background worker must operate as the exact approved cross-session owner. Extraction receives
only the approved event identity, event kind, content, and correlation. The resulting batch retains
the complete approved candidate, extractor descriptor, assessments, usage, duration, and extraction
time.

## Proposals and confidence

Every extraction proposal contains:

- a canonical memory key;
- a normalized value;
- exact source text that must occur in the approved event;
- finite confidence between zero and one.

Policy classifies proposals at or above the configured threshold as accepted. Lower-confidence
proposals remain visible as `BelowConfidenceThreshold` evidence rather than being silently
discarded. An extraction batch is still a candidate batch; acceptance here does not make a memory
authoritative knowledge.

Duplicate case-insensitive keys, source text outside the event, proposal-limit violations, null
outputs, and usage-budget violations fail the entire extraction.

## Extractor provenance and budgets

Extractor descriptors identify:

- component and version;
- optional model and model version;
- configuration hash;
- whether execution is deterministic.

Policy bounds proposal count, wall-clock duration, model calls, tokens, and cost. Cancellation is
propagated through a linked timeout token.

This contract replaces direct, provenance-free synthesis into generic `MemoryEntry` values for the
cross-session workflow. Existing generic memory APIs remain storage capabilities, not extraction
authorization.
