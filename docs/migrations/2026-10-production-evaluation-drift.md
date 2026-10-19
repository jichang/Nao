# Production evaluation and drift

Nao now has one governed path from production signals to permanent regression coverage.

`EvalProductionSamplingPolicy` requires deterministic sampling configuration, an explicit
consent-record SHA-256, positive retention, allowed signal kinds, and a versioned redactor.
Captured observations retain only redacted text and sanitization evidence. Explicit negative user
feedback is accepted only when its turn identity matches the correlated `TurnRecord`; every
observation binds the exact execution identity and `EvalExecutableConfiguration`.

Failures, denials, retries, abandonments, low-confidence outcomes, and negative feedback use the
same cluster contract. Candidate proposal requires the owning authenticated scope. Candidate
review requires a different user in the same tenant, group, workspace, and session boundary and
produces the existing version-1 production-derived `EvalCase`.

Drift rules now cover model, prompt, retrieval, cost, latency, and traffic distribution. Rules
define direction, warning and critical magnitudes, minimum samples, and escalation actions.
Alerts carry the changed dimension and exact affected segment as well as both executable
configurations.

Shadow, canary, and A/B experiments use deterministic opaque cohort hashes. They persist scoped
ownership and exact configuration identity without production content or authentication claims.
Resolved incidents require an approved production-derived case so the failure cannot disappear
from future regression suites.
