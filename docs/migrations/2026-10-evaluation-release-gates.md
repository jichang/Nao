# Evaluation release gates

The unreleased evaluation contract now has one generic release-gate model. Callers project
functional, quality, safety, policy, retrieval, latency, token, and cost evidence into
`EvalReleaseSnapshot`; domain-specific reports remain responsible for producing their underlying
measurements.

`EvalResult.Outcome` now records success, correctness failure, or infrastructure failure.
`EvalReleaseProjection.fromReport` projects that evidence and the canonical quality, success,
latency, and scenario-success measurements into a release snapshot. Callers supply the remaining
domain metrics as additional measurements; duplicate metric keys are rejected.

Each configured metric requires an absolute threshold, a tolerated baseline delta, a minimum
sample count, and a supported confidence level. Proportions must provide exact success counts.
Sampled continuous measurements must provide standard deviation. Missing statistical evidence no
longer becomes a point-confidence fallback.

Baseline replacement is an explicit proposal, independent review, and promotion workflow.
Baseline versions and audit events are retained, rollback selects a retained version, and all
failed case identities observed by proposals remain in the registry and every subsequently
promoted baseline.

CI uses the `EvalFast` MSTest category for pull requests and pushes and the `EvalFull` category for
scheduled runs. The workflow publishes its evaluation TRX reports as the
`evaluation-gate-reports` artifact.
