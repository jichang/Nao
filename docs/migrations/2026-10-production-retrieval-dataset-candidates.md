# Production retrieval failures as reviewed dataset candidates

## Capture boundary

`KnowledgeDatasetCandidate.capture` converts a production retrieval failure into a version-1
candidate. Capture requires:

- authenticated authorization scope;
- consent-record content hash;
- explicit retention deadline;
- mandatory query redactor with component and configuration provenance;
- typed failure kind, correlation identity, observed chunk identities, and evaluation dimensions.

The raw production query is passed only to the redactor. The candidate retains the redacted query,
removed-value count, redactor provenance, consent hash, owner identity, and expiry. It does not
retain the raw query, removed values, source text, principal claims, or authentication evidence.

Candidates are not evaluation cases and cannot enter a dataset automatically.

## Review boundary

Candidate review requires a permission decision for the candidate's canonical administration
resource. Denial returns no judgment. An authorized reviewer may:

- reject the candidate with a reason; or
- supply a stable case ID, relevant document/chunk judgments, expected facts, prohibited results,
  and mandatory justification.

Approval constructs a normal `KnowledgeEvaluationCase`, so incomplete or contradictory judgments
fail through the same domain validation as hand-authored datasets. Expired candidates cannot be
reviewed.

## Promotion boundary

`KnowledgeDatasetCandidatePromotionWorkflow.promoteAsync` requires a separate permission decision
for the target dataset. It accepts only approved, unique, unexpired candidates. Rejected
candidates fail promotion.

Promotion creates a new `KnowledgeEvaluationDataset` revision and records candidate identities,
authenticated promoter identity, permission evidence, and promotion time. When extending an
existing dataset, the name must match and the revision must increase.

No automatic capture-to-dataset path or compatibility bypass is provided.
