# LLM claim-support evaluator

## Change

Nao now provides `KnowledgeLlmClaimSupportEvaluator`, a provider-neutral production adapter for
semantic support judgments. It connects any cancellation-aware `LlmProvider` to the existing
grounded claim-support contract.

The evaluator:

- sends answer claims and their verified citation excerpts as untrusted JSON data;
- requires one structured assessment for every claim;
- accepts only supplied claim and citation identities;
- requires supported claims to identify direct supporting citations;
- requires unsupported claims to identify no supporting citations;
- performs only the configured number of repair attempts;
- propagates caller cancellation into provider I/O;
- requires consistent provider-reported input, output, and total token usage;
- computes cost from explicit per-million-token pricing.

Malformed output, unknown identities, duplicate or missing assessments, inconsistent token
evidence, exhausted repair, timeout, and budget excess fail explicitly.

## Configuration identity

The evaluator descriptor is non-deterministic and records the exact evaluator component, model,
completion options, duration and response bounds, repair limit, and pricing through a canonical
configuration hash. Nao is unreleased, so this production adapter is introduced directly without
a compatibility wrapper.

## Qualification boundary

Controlled tests prove protocol, repair, cancellation, token, and pricing behavior. A real model
must still execute the complete five-case adversarial grounded-generation qualification before
the production-adapter-set R2 criterion can be completed.
