# Architecture decisions

Architecture decision records (ADRs) capture choices that establish or materially change a platform boundary, durable contract, or security model.

## When an ADR is required

Create an ADR when a change:

- creates, removes, or changes a package, runtime, adapter, or ownership boundary;
- creates or incompatibly changes durable events, files, tables, Orleans state, or wire contracts;
- changes authentication, authorization, tenancy, isolation, secret handling, or trust boundaries.

Routine implementation details within an accepted boundary do not require an ADR. When uncertain, record the decision: a short explicit rationale is cheaper than reconstructing one later.

## Workflow

1. Copy [the template](TEMPLATE.md) to `NNNN-short-title.md` using the next four-digit sequence.
2. Set the status to `Proposed` and link the roadmap task or issue.
3. Describe considered options, consequences, compatibility, security, and validation before implementation is merged.
4. Change the status to `Accepted`, `Rejected`, or `Superseded` when the decision is resolved.
5. Never rewrite an accepted decision to change history. Add a new ADR and link it through `Supersedes` and `Superseded by`.

ADRs describe why a contract exists. Current behavior remains documented in the owning architecture or capability page.