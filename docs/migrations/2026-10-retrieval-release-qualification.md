# Retrieval release qualification

## Change

`KnowledgeRetrievalReleaseQualification` now combines the previously separate retrieval evidence
into one fail-closed R2 gate. The profile is bound to the exact version-1 adversarial corpus,
evaluation dataset content hash, and hybrid retrieval evaluator.

The initial profile requires all five adversarial cases and:

| Dimension | Requirement |
|---|---:|
| Recall | at least 0.90 |
| Precision | at least 0.90 |
| Mean reciprocal rank | at least 0.95 |
| nDCG | at least 0.95 |
| Mean latency | at most 250 ms |
| P95 latency | at most 500 ms |
| Total retrieval cost | at most USD 0.05 |
| Prohibited retrieved chunks | zero |
| Unauthorized retrieved chunks | zero |

The hybrid strategy must also improve over the lexical/vector baselines or retain an explicit
parity justification.

## Safety and isolation

Safety is evaluated from each case's typed prohibited-result judgments. Isolation is evaluated
independently against every corpus chunk classified as unauthorized. A retrieved unauthorized
chunk therefore fails isolation even if a malformed case omitted it from its prohibited targets.
Violations retain exact case and chunk identities.

## Evidence boundary

Qualification records the retrieval adapter component and configuration hash, complete hybrid
evaluation, threshold checks, and qualification time. The built-in deterministic evidence proves
the release profile and R2 gate behavior. It does not represent a live production-model
qualification and does not close the separate full production-adapter-set criterion.

Nao is unreleased, so this release contract is introduced at version 1 without compatibility
aliases or older profile readers.
