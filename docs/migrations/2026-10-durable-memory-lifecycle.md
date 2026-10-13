# Durable memory lifecycle

## Versioned records

`DurableMemoryLifecycle` converts accepted extraction assessments into versioned durable-memory
records. Every mutation requires the exact sessionless owner and expected record version.
Production stores must persist the returned record with an equivalent atomic compare-and-swap.

Active records retain:

- the canonical key and current value;
- a SHA-256 value digest;
- confidence and bounded extraction provenance;
- an explicit retention deadline.

These records remain learned memory and are not authoritative knowledge.

## Reinforcement, contradictions, and corrections

A matching value reinforces the active record, retains bounded provenance, keeps the highest
confidence, and refreshes retention from the latest direct user assertion.

A different ordinary user assertion changes the record to `Contested`. Contested records preserve
the alternatives and their provenance but must not be treated as an active retrievable value.

A directly asserted `UserCorrection` supersedes either active or contested state. The corrected
value becomes active with the correction's provenance and a new retention deadline. Assistant,
tool, sub-agent, and system output cannot enter this workflow because the earlier eligibility
boundary excludes them.

## Expiry and forgetting

Expiry is permitted only at or after the current retention deadline. It is terminal and scrubs the
key, value, confidence, and extraction provenance from the returned record.

User-requested forgetting requires:

- an authenticated session;
- the exact requesting session at application time;
- the same tenant, user, optional group, and workspace as the durable owner;
- the exact memory record identity;
- a non-blank reason and correlation evidence;
- an application time at or after the request.

Forgetting is terminal and scrubs the key, value, confidence, and extraction provenance. The
remaining evidence records only who requested deletion, correlation, reason, request time, and
application time.

This lifecycle contract does not itself provide persistence or physical backup erasure. Hosts must
apply transitions atomically and honor deletion in replicas, indexes, caches, and backup-retention
policy.
