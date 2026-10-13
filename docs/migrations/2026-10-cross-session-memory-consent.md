# Cross-session memory consent and policy

## Boundary

`CrossSessionMemoryApproval` is the second boundary between session events and durable user memory.
It accepts only a previously eligible `DurableSessionMemoryCandidate` and produces a
`CrossSessionUserMemoryCandidate`; it does not write a memory store.

Approval requires both:

- active consent bound to the candidate's tenant, user, and optional group; and
- an active allow-policy decision bound to the exact candidate event and target owner.

Consent may cover one event, one user-asserted event category, one workspace, or the user. Broader
consent changes which candidates it can cover, not the ownership of an individual promotion.

## Scope preservation

The target owner is derived from session-event provenance. Approval removes only the session
identity. Tenant, user, group, and workspace cannot be supplied by the caller or widened by
consent.

Evaluation fails closed when:

- consent is absent, not yet active, expired, revoked, subject-mismatched, or does not cover the
  candidate;
- the caller is not the exact authenticated session owner;
- policy evidence names another candidate or owner, is not yet active, has expired, or denies the
  operation.

The approved candidate retains the original session candidate, consent record, policy decision,
target owner, and approval time.

## Host responsibilities

Hosts remain responsible for durable consent storage, revocation delivery, policy evaluation, and
writing approved candidates to an appropriate memory store. Eligibility, consent, and policy are
separate decisions; generic memory-tier promotion satisfies none of them.

This is a new unreleased contract. There is no fallback that treats durable intent, tier movement,
or a prior session approval as cross-session consent.
