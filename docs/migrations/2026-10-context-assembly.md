# 2026-10 authorized bounded context assembly

## Authorization boundary

`KnowledgeContextAssembler.assemble` requires the exact `AuthorizationScope` retained by the
original retrieval request. A reconstructed or substituted scope is rejected even if some
identity fields appear equivalent.

Before selection, every reranked document is revalidated against:

- namespace;
- owner and ACL;
- source and immutable document-version filters;
- metadata filters;
- source-modification ranges;
- ingestion ranges.

This check is defense in depth after index queries, expansion, candidate assembly, and reranking.
An unauthorized candidate fails the operation rather than being silently omitted.

## Deterministic allocation

Candidates are considered in the order produced by the configured source-preference policy, with
reranker rank as the final deterministic tie-breaker. `KnowledgeContextAssemblyConfig` pins that
policy, the tokenizer, and bounds:

- total rendered tokens;
- total included candidates;
- candidates per source;
- minimum evidence candidates.

The token count is recomputed over the complete candidate context after each proposed inclusion,
including stable chunk markers and separators. Context therefore cannot exceed its declared
rendered-token budget because of formatting overhead.

Each candidate receives one decision:

- included with one-based context rank;
- suppressed by total candidate limit;
- suppressed by per-source limit;
- suppressed by token limit.

## Insufficiency and citations

Assembly returns a typed insufficiency when no candidate fits or when the selected evidence count
is below the configured minimum. It does not produce an empty success-shaped context.

Each included item is rendered with:

```text
[nao-citation:<chunk-id>]
<exact indexed chunk text>
```

Searchable index documents retain the chunk's validated `SourceLocation` records, including byte
and character ranges and optional page, line, and path evidence. PostgreSQL vector and lexical
indexes persist these records in a required `source_locations` JSONB column. They also persist the
document identity and positive numeric document version independently from the immutable
document-version identity, plus complete source and document-version trust assessments in required
`source_trust` and `version_trust` JSONB columns. Existing development indexes must be rebuilt
because Nao does not provide a compatibility schema for the old shape.

Each included context item carries citation evidence binding:

- the rendered marker;
- source, immutable document-version, and chunk identities;
- the indexed source locations;
- the exact statement offset and length in the final rendered context;
- the SHA-256 hash of the exact quoted statement.

Formatting offsets include all preceding markers and blank-line separators. Assembly includes
whole chunks rather than truncating statements; a chunk that would exceed the token budget is
suppressed. This preserves markers, source ranges, statement ranges, and quoted hashes as one
consistent unit.

## Stale and conflicting versions

Before allocating context budgets, assembly reauthorizes every candidate and analyzes all
authorized document provenance:

- a candidate is stale when another candidate for the same document has a higher numeric version;
- document identity conflicts when the same document identity is claimed by multiple sources;
- an immutable version fork exists when multiple document-version identities claim the same
  document identity and numeric version.

Stale candidates and every branch of either conflict are suppressed before candidate, source, and
token limits are applied. Each suppression retains typed evidence with the document identity,
observed versions, sources, or immutable version identities. Multiple chunks from one immutable
document version are not conflicts.

## Source preference

`KnowledgeContextSourcePreferencePolicy` requires callers to choose one ordering explicitly:

- `AuthorityThenCurrent` orders by effective trust, then freshness;
- `CurrentThenAuthority` orders by freshness, then effective trust.

Effective trust is the less trusted of the source and immutable document-version assessments, so a
high-trust source cannot elevate an unverified version. Freshness is the source-modification
timestamp when present and otherwise the ingestion timestamp. Reranker rank resolves remaining
ties.

Every candidate decision records the selected policy, both trust assessments, effective trust,
freshness timestamp, original reranker rank, and final preference rank. Freshness/conflict
suppression is evaluated against the complete authorized candidate set; source preferences then
determine which eligible evidence consumes candidate, source, and token budgets.
