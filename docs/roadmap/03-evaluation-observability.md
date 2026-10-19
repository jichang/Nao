# Evaluation and Observability

This workstream connects production execution, telemetry, replay, evaluation, regression detection, and release gates into a continuous improvement loop.

**Milestone:** R3
**Dependencies:** Foundations and harness; knowledge workstream for retrieval-specific evaluation
**Primary owners:** `Nao.Eval`, `Nao.Agents`, persistence and telemetry adapters

## Existing baseline

- [x] Exact, contains, regular-expression, verification, composite, and LLM-judge evaluation primitives exist.
- [x] Dataset execution and report generation exist.
- [x] Traces, spans, metrics, token usage, execution journals, and regression primitives exist.
- [x] In-memory and persistent observability implementations exist.
- [x] Evaluation runs use the same complete harness configuration as production.
- [ ] Standard external telemetry export and continuous drift workflows exist.

## EVAL-01 — Versioned evaluation specification

- [x] Define versioned dataset, case, turn, expected behavior, evaluator, and report contracts.
- [x] Record dataset revision and content hash.
- [x] Record agent, prompt, response protocol, tool, provider, model, harness, policy, knowledge-index, and evaluator versions.
- [x] Support expected answer, properties, tool calls, forbidden actions, artifacts, citations, budgets, and terminal states.
- [x] Support deterministic setup and teardown fixtures.
- [x] Define redaction rules for production-derived examples.
- [x] Define case ownership, review status, labels, and expiry.

**Acceptance criteria**

- [x] A report fully identifies the executable configuration that produced it.
- [x] Dataset mutation creates a new identifiable revision.
- [x] Sensitive production data cannot enter a dataset without explicit sanitization and review.

## EVAL-02 — Harness parity and reproducibility

- [x] Execute cases through the same harness entry point as production.
- [x] Support pinned live-provider, mock-provider, and recorded-replay modes.
- [x] Record random seeds and scheduling settings.
- [x] Provide deterministic clocks and IDs for tests where needed.
- [x] Capture tool and provider fixtures with schema/version metadata.
- [x] Verify stop-on-first-failure and cancellation semantics.
- [x] Distinguish nondeterministic variance from regression.
- [x] Generate a portable reproduction bundle.

**Acceptance criteria**

- [x] Recorded mode runs without external network access.
- [x] Repeated deterministic runs produce equivalent results.
- [x] A failed CI case can be reproduced locally from its bundle.

## EVAL-03 — Evaluator quality

- [x] Define deterministic evaluators before using LLM judges where possible.
- [x] Add structured-output validation, schema validation, tool-sequence, artifact, citation, policy, and resource evaluators.
- [x] Add retrieval metrics and groundedness evaluators.
- [x] Calibrate LLM judges against human-labeled sets.
- [x] Measure inter-rater agreement and judge stability.
- [x] Detect position, verbosity, model-family, and self-preference bias.
- [x] Support multiple judges and aggregation policies for high-risk gates.
- [x] Record judge rationale as untrusted evidence, not ground truth.

**Acceptance criteria**

- [x] Every release-blocking evaluator has documented reliability and failure semantics.
- [x] Judge upgrades require calibration against the prior version.
- [x] Deterministic validation cannot be overridden by a favorable probabilistic score.

## EVAL-04 — Regression and release gates

- [x] Define gates for quality, safety, success rate, policy compliance, retrieval, latency, token use, and cost.
- [x] Support absolute thresholds and change-from-baseline thresholds.
- [x] Add statistical confidence intervals and minimum sample-size rules.
- [x] Segment regressions by scenario, provider, model, language, source, and tenant policy.
- [x] Define baseline creation, review, promotion, retention, and rollback.
- [x] Prevent failed cases from disappearing through baseline replacement.
- [x] Publish reviewable report artifacts in CI.
- [x] Add scheduled full suites and fast pull-request suites.

**Acceptance criteria**

- [x] CI fails predictably when a configured threshold is crossed.
- [x] Baseline promotion is explicit and leaves an audit trail.
- [x] Reports separate correctness regressions from infrastructure failures.

## EVAL-05 — Production feedback and drift

- [x] Sample production traces using configurable privacy-aware policies.
- [x] Capture explicit user feedback and correlate it with execution evidence.
- [x] Cluster failures, denials, retries, abandonments, and low-confidence outcomes.
- [x] Convert selected failures into reviewed regression cases.
- [x] Detect model, prompt, retrieval, cost, latency, and traffic-distribution drift.
- [x] Define alerts and escalation thresholds.
- [x] Support shadow, canary, and A/B evaluation without leaking tenant data.
- [x] Close resolved incidents with permanent regression cases where appropriate.

**Acceptance criteria**

- [x] Production-derived cases preserve consent, redaction, and retention constraints.
- [x] Drift alerts identify the changed dimension and affected segment.
- [x] User feedback can be traced to the exact tested configuration.

## OBS-01 — Unified telemetry model

- [x] Define standard attributes for tenant, workspace, session, turn, execution, agent, tool, provider, model, and storage operation.
- [x] Propagate trace and correlation context across Orleans and external process boundaries.
- [x] Define span structure for harness stages, orchestration rounds, provider calls, tool calls, retrieval, reasoners, and persistence.
- [x] Define canonical counters, histograms, and gauges.
- [x] Track provider-reported token splits without inventing unavailable data.
- [x] Track cost using versioned caller-owned pricing.
- [x] Add structured error and terminal-state dimensions with bounded cardinality.
- [x] Avoid user content in metric labels.

**Acceptance criteria**

- [x] One turn can be followed end-to-end through all participating components.
- [x] Retries and delegated work preserve parent/causation relationships.
- [x] Telemetry schemas have compatibility tests.

## OBS-02 — OpenTelemetry and external backends

- [x] Implement OpenTelemetry trace export.
- [x] Implement OpenTelemetry metrics export.
- [x] Integrate structured logs with trace/span correlation.
- [x] Support OTLP over documented transports.
- [x] Add Prometheus-compatible metric exposure where appropriate.
- [x] Define exporter queue, batching, retry, drop, and shutdown behavior.
- [x] Ensure telemetry failure cannot block critical execution indefinitely.
- [x] Provide local collector configuration and reference dashboards.

**Acceptance criteria**

- [x] A reference deployment exports traces, metrics, and logs to standard tools.
- [x] Exporter backpressure has bounded memory and documented data-loss behavior.
- [x] Graceful shutdown flushes within a configured deadline.

## OBS-03 — Privacy, security, and retention

- [x] Classify telemetry fields by sensitivity.
- [x] Redact secrets, credentials, personal data, and protected content before export.
- [x] Configure content capture separately from operational metadata.
- [x] Apply tenant-specific retention and regional routing policies.
- [x] Encrypt telemetry in transit and at rest through deployment adapters.
- [x] Restrict audit-log mutation and deletion according to policy.
- [x] Add canary-secret and synthetic-PII leakage tests.
- [x] Make sampling decisions trace-consistent.

**Acceptance criteria**

- [x] Sensitive fixtures do not appear in exported telemetry.
- [x] Tenant retention and access restrictions are enforced.
- [x] Audit evidence records redaction without retaining the removed value.

## OBS-04 — Operational diagnostics

- [x] Add liveness, readiness, startup, and dependency health contracts.
- [x] Report provider, storage, queue, knowledge-index, and reasoner health independently.
- [x] Add saturation signals for provider limits, execution workers, Orleans grains, storage pools, and telemetry queues.
- [x] Add dashboards for success, latency, cost, tokens, retries, circuit states, policy blocks, retrieval quality, and evaluation drift.
- [x] Define actionable alerts and runbook links.
- [x] Add diagnostic snapshots that exclude secrets and protected content.

### Exit criteria for evaluation and observability

- [x] EVAL-01 through EVAL-05 are complete for release-critical scenarios.
- [x] OBS-01 through OBS-04 are complete for supported deployments.
- [x] Pull requests, scheduled runs, and production drift feed one versioned quality system.
- [x] Telemetry privacy and reliability tests pass.

[Back to roadmap](../roadmap.md)
