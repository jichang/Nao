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

## Provider outage

1. Confirm user-visible availability or latency violations are attributed to `Provider`, then
   inspect the named provider/model circuit, retry, quota, and upstream-status diagnostics.
2. Stop retries that are consuming deadlines or quota without increasing success.
3. Preview and apply routing to an approved healthy provider, or use authenticated emergency
   disablement for the exact failed model or connector.
4. Do not route across residency, capability, safety, or cost policy merely to restore traffic.
5. Verify successful operations and stable error-budget consumption before restoring the route.

## Storage degradation

1. Confirm failures are attributed to `Storage`; distinguish authoritative state, journals,
   artifacts, telemetry, backup storage, and derived indexes.
2. Keep admission fail-closed when authoritative writes or durable terminal outcomes cannot be
   acknowledged. Never return success from volatile fallback state.
3. Reduce admission and retries to protect the dependency. Fail over only to a deployment-approved
   replica with verified freshness and fencing.
4. If integrity is uncertain, invoke the backup/restore procedure and digest verification rather
   than repairing records in place.
5. Restore readiness only after write/read verification, sequence checks, and backlog recovery.

## Worker exhaustion

1. Confirm platform-attributed queue rejection or latency violations and identify the exhausted
   worker class, not only aggregate CPU.
2. Inspect active work, queued work, process limits, memory, provider concurrency, and the bounded
   autoscaling recommendation.
3. Reduce admission or scale within provider and storage safe-capacity limits. Do not make queues
   unbounded or remove isolation limits.
4. Cancel only work permitted by its operation semantics; preserve durable checkpoints and
   terminal outcomes.
5. Confirm queues drain, rejection stops, and utilization remains below 80 percent.

## Policy failure

1. Attribute unavailable, invalid, or incorrectly enforced governance to `Policy`; ordinary denied
   customer requests remain `CustomerInput`.
2. Preserve fail-closed enforcement. Never bypass authorization, approval, safety, residency, or
   tool restrictions to restore availability.
3. Compare the active policy revision with the last-known-good snapshot and audit evidence.
4. Preview and apply an optimistic rollback through the authenticated administration client.
5. Reproduce the failure through deterministic policy tests before reactivating the candidate.

## Index corruption

1. Mark affected retrieval routes unready and attribute the dependency failure to `Storage`.
2. Identify the authoritative source/checkpoint and affected vector, lexical, or graph index
   revision. Do not promote a partial index.
3. Rebuild derived indexes from authoritative records under the current deletion ledger and
   owner/region constraints.
4. Verify record counts, source/checkpoint identities, content digests, ACL filtering, and
   citations before atomically publishing the rebuilt revision.
5. Keep the corrupt generation isolated until incident evidence is retained, then delete it under
   the ordinary retention policy.

## Bad rollout

1. Correlate the first user-visible violation with deployment, configuration, provider, policy,
   knowledge, or evaluation revision changes.
2. Stop promotion and preserve the failing revision, trace identities, and alert evidence.
3. For binaries, drain affected silos/workers and roll back to the previous compatible image. For
   reloadable settings, preview and activate the retained last-known-good snapshot as a new
   monotonic version.
4. Use emergency disablement when the failing component can still receive new work during rollback.
5. Verify availability, latency, safety, and error-budget trajectory before resuming promotion.

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

## Backup or restore failure

1. Keep runtime admission disabled in the restore target and preserve the failed evidence.
2. Verify the provider recovery point, backup manifest, deletion ledger, target region, and
   encryption-key versions before retrying.
3. Reject partial event streams, sequence gaps, hash mismatches, foreign owners, dangling
   references, and missing deletion-ledger entries. Never substitute empty state.
4. Restore authoritative records before rebuilding derived indexes. Keep retrieval unready until
   every required rebuild and citation check succeeds.
5. Fence the old write environment before enabling the restored environment.
6. Record actual data loss and recovery duration against the selected deployment profile. Open a
   corrective issue when either objective is missed.

## Migration failure

1. Fence incompatible writers and inspect the durable migration checkpoint.
2. Verify that the migration identity and immutable plan digest match the deployed code.
3. Resume only incomplete steps. Never rerun a completed non-idempotent effect outside the runner.
4. If forward recovery is unsafe, execute rollback; completed steps roll back in reverse order.
5. Restore the verified preflight backup when rollback fails or the storage layout is uncertain.
6. Keep the prior deployment and source data until post-migration integrity, authorization,
   deletion-suppression, and readiness checks pass.

## Alert exercise and incident closure

1. Inject bounded synthetic violations for platform, provider, policy, storage, and customer-input
   domains without sending real customer content.
2. Confirm the alert requires user-visible violations and error-budget burn, identifies the right
   objective and dominant domain, and links to working diagnostics and the matching runbook.
3. Execute the non-destructive mitigation or preview the authenticated rollback. Never exercise an
   unreviewed production mutation.
4. Record timestamps, alert configuration revision, observed routing, and recovery evidence.
5. After an incident, record corrective actions and at least one named regression test. Do not
   close the review until the test or operational exercise passes.
