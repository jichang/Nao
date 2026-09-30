# 2026-09 deterministic query normalization

## Scope

`KnowledgeQueryNormalizer.normalize` converts a `KnowledgeRetrievalRequest` into
`KnowledgeNormalizedQuery` evidence. The result retains:

- the unchanged request and authorization scope;
- the exact original query;
- normalized query text;
- the normalizer component and version;
- content hashes for the original and normalized forms.

## Algorithm

The v1 normalizer applies these operations in order:

1. Unicode compatibility normalization using NFKC (`NormalizationForm.FormKC`);
2. replacement of every Unicode whitespace run with one ASCII space;
3. removal of leading and trailing whitespace through the same canonicalization pass;
4. rejection of non-whitespace Unicode control characters;
5. validation of the normalized length against the request's character limit.

Generated transformation output uses the same algorithm. Rewrites, decomposition results, and
multi-query alternatives use the query-character limit; hypothetical documents use the separate
generated-text limit.

Normalization preserves case and punctuation. Lowercasing would change the text submitted to
embedding models and can discard distinctions that some corpora or languages use. Lexical
adapters may perform their own case-insensitive analysis inside the configured search engine.

## Evidence and security

The normalizer does not create a new retrieval request. `KnowledgeNormalizedQuery.request`
references the original immutable request, so tenant, workspace, session, principal, namespace,
typed filters, strategy, limits, and budget cannot be dropped during normalization.

Hashes use the same `ContentHash` primitive as other Knowledge provenance. They allow evaluations
and traces to compare exact transformation inputs and outputs without treating normalized text as
the original user input.

The operation is deterministic and idempotent for a fixed .NET Unicode implementation. Hosts
should record the `nao-query-normalizer@1` descriptor when persisting evidence.
