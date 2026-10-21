# Operational runbooks

Nao alerts identify a bounded operational component and never include prompts, tool payloads,
credentials, or personal data. Start with the correlated trace and component-level health signal;
do not paste protected content into incident systems.

## Component unhealthy

1. Identify the failing probe, component category, and component name.
2. Compare liveness, startup, readiness, and dependency results. Restart only when liveness or
   startup is unhealthy; dependency failures require investigation of the named dependency.
3. Check saturation before increasing retries, because retries amplify an exhausted dependency.
4. Escalate when the dependency remains unhealthy after its documented recovery objective.

## Resource saturation

1. Identify whether provider limits, execution workers, Orleans grains, storage pools, or telemetry
   queues are saturated.
2. For Orleans saturation, inspect the separate `session_activation` and `turn_execution` signals:
   active plus queued work, configured capacity, rejection count, and the current scaling
   recommendation. A nonzero rejection count is an overload event, not a normal queue condition.
3. If cluster membership or durable storage is degraded, keep runtime admission fail-closed. Do not
   override `MembershipUnavailable`, `StorageUnavailable`, or `Draining` to force new work through.
4. During rolling maintenance, mark the silo draining, wait for active turn leases to reach zero,
   stop it, deploy only a build with the same deployment compatibility identity, then return it to
   healthy admission.
5. Check rejected-work signals and execution latency over the same interval.
6. Reduce admission or scale the named resource. Do not increase an in-process queue without a
   memory-capacity review.
7. Confirm utilization remains below 80 percent after mitigation.

## Execution failures

1. Split failures by bounded error category and terminal state.
2. Inspect correlated traces for the dominant category without enabling protected-content capture.
3. Compare provider retries, circuit state, policy blocks, and dependency health.
4. Roll back the responsible deployment or disable the failing dependency route when the increase
   aligns with a release.

## Provider latency

1. Compare provider and model latency with retry rate and provider-limit saturation.
2. Check the provider circuit state and upstream status.
3. Route to an approved alternative provider or reduce concurrency when limits are saturated.
4. Do not relax execution deadlines without validating aggregate duration budgets.

## Evaluation drift

1. Identify the governed quality metric that crossed its configured drift threshold.
2. Open the matching evaluation dataset, configuration identity, and production sample window.
3. Reproduce the drift through the production-equivalent evaluation harness.
4. Block release promotion or roll back when a release-critical gate regressed.

## Emergency administrative disablement

1. Authenticate with multi-factor or phishing-resistant evidence and both a control-plane
   administration role and `nao.control.emergency`. Never use impersonated authority.
2. Call `PreviewAsync` for the exact model, tool, agent, connector, or tenant identity and review
   the affected resources and warnings. A preview does not change the resource revision.
3. Call `MutateAsync` with a new stable operation ID and the previewed expected revision. Reuse that
   operation ID only to retry the identical request.
4. If propagation or audit delivery fails, do not submit a different mutation. Retry the pending
   operation until the host adapter, all silo observers, and durable audit storage acknowledge it.
5. Confirm `IsDisabledAsync` from more than one silo-facing client and verify new work fails closed.
   Terminal turn replay remains available because it performs no new execution.
6. Correct the incident, preview `enable`, and submit it against the current revision. Do not bypass
   the control plane by changing process-local caches.
