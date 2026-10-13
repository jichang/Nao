# Session-event durable-memory eligibility

## Boundary

`SessionMemoryEligibility` defines the first boundary between session events and durable memory.
Evaluation produces a `DurableSessionMemoryCandidate`; it does not write a memory store.

A session event may become a candidate only when:

- it belongs to the complete authenticated scope performing evaluation;
- the scope contains a session identity;
- it is a recent, non-future event;
- it is directly user-asserted as a fact, preference, correction, or explicit remember request;
- the active versioned policy allows that user-event kind;
- it is explicitly marked with durable persistence intent;
- its content is classified as ordinary.

Assistant output, tool output, sub-agent output, and system events can never be configured for
direct durable promotion. Sensitive content requires a later review path. Secret content is
forbidden.

## Evidence

`SessionMemoryEvent` preserves:

- stable event identity;
- complete authorization identity, including session;
- execution correlation;
- event kind;
- exact candidate content;
- content classification;
- persistence intent;
- occurrence time.

Eligible candidates additionally retain policy identity, policy version, and evaluation time.
Ineligible results retain the event and all applicable typed rejection reasons.

## Scope

This contract governs conversion of session events into memory candidates. It does not reinterpret
generic tiered-memory movement as consent or eligibility. Cross-session consent, extraction,
contradiction handling, expiry, forgetting, and knowledge integration remain separate MEM-01
boundaries.

This is a new unreleased contract with no permissive fallback for unclassified events.
