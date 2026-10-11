# 2026-10 grounded support policy

## Policy boundary

`KnowledgeGroundedSupportPolicy` converts semantic claim-support evaluation into an explicit
presentation or remediation outcome. Supported answers are always accepted. When at least one
material claim is unsupported, the configured `KnowledgeUnsupportedClaimBehavior` selects exactly
one outcome:

- `Abstain` returns typed policy insufficiency and no accepted answer;
- `Repair` returns a repair request containing the complete evaluation, unsupported assessments,
  and deterministic repair instruction;
- `Warn` retains the evaluation only inside an explicitly warned result;
- `Reject` retains the evaluation only inside an explicitly rejected result.

The default policy rejects unsupported answers.

## Safety properties

Policy decisions derive unsupported claims from the assessments rather than trusting the
support-result union case supplied by a caller. A result with no unsupported assessments is
accepted; any result containing unsupported assessments must pass through the configured behavior.

Warning is the only behavior that permits a caller to deliberately present the structurally
verified answer while retaining an unsupported-claim signal. Abstention, repair, and rejection do
not expose an accepted-answer result.

Repair does not mutate the existing proposal or silently remove claims. It requests a new grounded
answer proposal that must pass citation verification and semantic support evaluation again.
Callers are responsible for bounding repair attempts when they execute that subsequent generation
step.
