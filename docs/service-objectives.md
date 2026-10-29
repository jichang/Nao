# Service Objectives and Error Budgets

Nao measures reliability at user-visible operation boundaries. Infrastructure health is diagnostic
evidence, not a substitute for whether an authenticated operation completed with the required
latency, durability, quality, safety, and cost.

## Operations and indicators

The objective catalog covers agent execution, knowledge query, knowledge ingestion,
administrative mutation, and recovery restore. Every operation has these indicators:

| Indicator | Measurement |
|---|---|
| Availability | The operation produced its documented terminal response rather than a platform, provider, policy, or storage failure. |
| Latency | End-to-end elapsed milliseconds from accepted request to durable terminal response. |
| Durability | The acknowledged state and required evidence can be reread with matching identity and digest. |
| Quality | The operation meets its pinned deterministic or governed evaluation threshold. |
| Safety | Required authorization, policy, isolation, citation, and output checks completed without bypass. |
| Cost | Normalized operation cost remains at or below its approved budget. |

`ServiceLevelObjective` binds an operation and indicator to a deployment profile, threshold,
compliance target, rolling window, and optional exclusion policy. `ServiceObjectiveCatalog`
produces all six objectives for every supported profile and operation. Local development uses a
one-day 90% diagnostic target. Single-node production and isolated workers use a 28-day 99%
target. Clustered production uses a 28-day 99.9% target. Hosts may create stricter objectives, but
must not silently weaken the published profile target.

Latency thresholds are 30 seconds for interactive execution and query operations, five seconds
for administrative mutations, and five minutes for ingestion and recovery orchestration. A
recovery objective measures Nao's orchestration operation; the separate profile RPO and RTO in
[Data lifecycle and disaster recovery](data-lifecycle-recovery.md) remain authoritative for the
complete physical recovery process.

## Error-budget accounting

For a report containing `N` eligible samples and target `T`, the allocated error budget is
`N * (1 - T)`. Consumption is violations divided by that budget. Consumption greater than `1`
means the objective exhausted its budget. A perfect 100% target has no violation allowance; any
violation yields infinite consumption rather than being hidden by division fallback.
An empty window is reported as unmeasured, never as 100% compliant.

Reports include eligible, compliant, violated, and excluded counts plus violations grouped by:

- `Platform`: Nao code, runtime admission, deployment, or internal orchestration;
- `Provider`: upstream model, embedding, connector, or external service;
- `Policy`: unavailable, invalid, or incorrectly enforced governance configuration;
- `Storage`: authoritative database, journal, artifact, backup, or index dependency;
- `CustomerInput`: malformed, unauthorized, unsupported, or caller-cancelled demand.

The domain describes the cause, not the observable symptom. An upstream timeout is `Provider`;
a Nao queue rejection is `Platform`; an unavailable database is `Storage`.

## Exclusions

Samples are eligible by default. An exclusion is valid only when all of these are recorded:

1. a versioned `SloExclusionPolicy` identity;
2. a category allowed by that exact policy;
3. a canonical reason on the sample.

The built-in categories are approved planned maintenance, customer cancellation, and invalid
customer input. The reporter rejects unknown policy identities or categories not authorized by
the objective's policy. Provider outages, storage degradation, policy failures, worker
exhaustion, bad rollouts, retries, and internal cancellations are never planned exclusions.
Maintenance that exceeds its approved window is eligible from the first unapproved minute.

Changes to exclusions are administrative policy changes and require review, versioning, audit,
and application only to future reports. Never rewrite a historical report under a newer policy.

## Actionable alerts

`ReliabilityAlertRule` requires:

- a minimum number of user-visible violations;
- an error-budget consumption threshold;
- severity;
- a diagnostics reference;
- a runbook reference;
- an optional rollback or disablement procedure.

An alert fires only when both violation and budget thresholds are crossed. This prevents a
healthy dependency probe from paging without user impact and prevents one low-volume sample from
creating an unbounded percentage alert. The resulting alert carries the dominant failure domain
so operators can distinguish platform, provider, policy, storage, and customer-input causes.

Recommended production rules are a warning at 50% budget consumption and a critical alert at
100%, each with volume chosen for the operation. Hosts should additionally use multi-window burn
rules in their telemetry backend, but both windows must resolve to the same objective identity and
runbook.

## Exercises and incident review

Before enabling paging, inject representative samples for every failure domain and prove that:

1. the expected rule fires only after user impact;
2. severity and objective identity are correct;
3. the dominant failure domain is correct;
4. the diagnostics reference resolves to the relevant trace or dashboard;
5. the runbook reaches a concrete mitigation;
6. rollback or emergency disablement uses the authenticated control plane;
7. recovery returns budget consumption to a stable trajectory.

Run this exercise after changing an objective, alert rule, telemetry label, runbook, or deployment
profile. Record the evidence with the alert configuration revision.

Every production incident must produce an `IncidentReviewRecord` with a failure domain, corrective
actions, and at least one named regression test. A review cannot close with an empty regression
test list. If no code defect existed, the regression may exercise alert routing, operational
policy, deployment validation, or the runbook itself.
