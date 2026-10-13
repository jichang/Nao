# Grounded production qualification runner

## Change

`KnowledgeGroundedProductionQualification` now executes the complete grounded-generation
qualification chain for a supplied evaluation dataset:

1. invoke the configured grounded-generation adapter;
2. parse its typed answer or insufficiency result;
3. verify every citation deterministically against the assembled context;
4. invoke the configured semantic claim-support evaluator for verified answers;
5. project answer, abstention, rejection, citation, and support evidence into evaluation cases;
6. calculate grounded-generation quality;
7. execute the grounded release profile.

The runner requires exactly one authorization-bound assembled context for every dataset case.
Missing, duplicate, or unexpected case identities fail before any release decision. Generation
rejections and citation-verification failures become unresolved evaluation outcomes rather than
success-shaped answers.

## Evidence

Every case retains:

- its authorization, correlation, question, and assembled context;
- the complete generation adapter result and usage;
- deterministic citation-verification evidence;
- semantic support evidence when an answer was verified;
- the exact evaluation result used by the release gate.

The aggregate retains the measured quality report and final release qualification.

## Live execution boundary

Credentials, provider endpoint, model, retrieval pipeline, and case authorization remain
host-supplied. Nao does not embed a vendor credential or silently substitute deterministic
evidence when live configuration is absent. The full production-adapter-set R2 criterion remains
open until all five built-in adversarial cases are executed through a real configured model and
the returned qualification is `Qualified`.

Nao is unreleased, so this version-1 runner is introduced directly without compatibility
constructors or partial-case fallback behavior.
