# Memory-to-knowledge authority

## Separation by construction

Durable memory remains learned, user-scoped state. It is not a `KnowledgeSource`, document,
document version, content unit, chunk, or index record, regardless of extraction confidence.

`MemoryKnowledgeAuthority.promote` is the explicit bridge. It produces a
`VerifiedMemoryKnowledgeCandidate`, not an ingested knowledge bundle. A subsequent ingestion
workflow must preserve its verification and authorization evidence when constructing source-backed
knowledge artifacts.

## Verification

Memory knowledge verification is bound to:

- exact durable-memory ID and positive version;
- exact sessionless tenant, user, optional group, and workspace owner;
- SHA-256 hash of the active value;
- organization-verified or authoritative target trust;
- one or more unique external references with absolute URI and content hash;
- verifier component/version and configuration hash;
- rationale, verification time, and expiry.

`Unverified` and `SourceAsserted` are invalid promotion-verification outcomes. Memory confidence,
repetition, correction, consent, and user assertion are not substitutes for external verification.

## Promotion

Promotion also requires an allow decision for the canonical resource
`memory-knowledge-promotion/<memory-id>/<memory-version>`. Permission evidence for another resource
is rejected, and promotion cannot precede either verification or permission evaluation.

Only active, unexpired, uncontested memory can be promoted. Verification must still be valid and
must exactly match the current owner, version, and value hash. A correction, reinforcement, or
other version change therefore invalidates older verification evidence.

The candidate retains the complete durable record, verification, derived trust assessment,
reviewer identity, permission evidence, and promotion time. This boundary does not ingest or index
the candidate.
