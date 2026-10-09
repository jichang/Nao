# 2026-09 lexical retrieval

## Scope

`Nao.Knowledge` now executes the lexical leg of an admitted query-understanding result through
`KnowledgeLexicalRetrievalExecutor.executeAsync`.

The executor accepts the vendor-independent `KnowledgeLexicalIndex` contract. PostgreSQL
full-text search remains one adapter rather than becoming part of the orchestration API.

## Execution boundary

Explicitly skipped requests return their existing admission evidence without requiring or invoking
an index. For a retrieval plan, the executor runs every normalized and provenance-bearing query
variant exactly once. Variants execute concurrently.

Every index invocation receives values from the same immutable request:

- complete `AuthorizationScope`;
- namespace;
- typed source, document-version, metadata, and time filter;
- per-query candidate limit.

The executor rejects a vector-only request. Lexical, hybrid, and hybrid-with-graph requests may
execute the lexical leg so later fusion can compose it without changing this boundary.

## Evidence

Successful retrieval records:

- `nao-lexical-retrieval@1`;
- the admission and query-transformation plan;
- every query variant;
- ordered candidates with the adapter's raw score and one-based rank;
- per-variant and total duration;
- query and raw candidate counts;
- zero lexical provider cost.

Candidates are not fused or deduplicated in this layer. Preserving raw per-variant ranks and scores
allows the hybrid layer to apply an explicit, testable fusion policy later.

## Fail-closed behavior

The executor enforces both per-query and aggregate raw-candidate limits and actively cancels index
operations when the retrieval duration budget expires. It rejects null adapter results and
revalidates every returned document against the request's namespace, authorization boundary, and
complete typed filter. Adapter defects therefore cannot silently widen retrieval scope.
