# 2026-10 grounded output provenance

## Output artifacts

`KnowledgeGroundedOutputProjector` converts the final support-policy result into an immutable
output/audit pair.

Only accepted and explicitly warned outcomes produce a `KnowledgeGroundedOutputArtifact`.
Abstained, repair-required, and rejected outcomes produce no presentable artifact. Every artifact
uses the shared `ArtifactId` identity and retains:

- the exact answer text and confidence;
- accepted or warned disposition;
- every exact claim contract and support assessment;
- every citation reference;
- exact quoted source text;
- source, document, numeric version, immutable version, chunk, source-location, and quoted-content
  hash provenance.

## Audit records

Every policy outcome produces a version-1 `KnowledgeGroundedAuditRecord`, including outcomes that
have no output artifact. Audit records retain:

- optional output artifact correlation;
- accepted, abstained, repair-required, warned, or rejected outcome;
- answer and claim content hashes rather than answer or quotation text;
- exact claim ranges and presentation kinds;
- support verdicts and confidence;
- complete citation provenance and source locations;
- evaluator component/model/configuration, metered usage, and evaluation duration.

Authorization and authentication evidence are not copied into the owner-free record. Hosts receive
the authenticated `AuthorizationScope` separately at the recorder boundary and must partition,
retain, and delete records according to their storage policy.

## Fail-closed emission

`KnowledgeGroundedOutputEmitter.emitAsync` records the audit entry before returning the projected
artifact. Recorder cancellation or failure propagates to the caller, so a presentable grounded
artifact cannot be returned without its corresponding audit record.

Nao does not yet provide a grounded-audit persistence adapter or encoding. The versioned record is
a storage-neutral contract supplied to the host recorder, not a new Nao-owned durable format.
