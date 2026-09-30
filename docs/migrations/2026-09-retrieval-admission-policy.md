# 2026-09 retrieval admission policy

## Scope

`KnowledgeRetrievalRequest` now declares `KnowledgeTaskIntent`:

- `KnowledgeSeeking`;
- `NonKnowledge`;
- `Unspecified`.

`KnowledgeRetrievalAdmissionPolicy` turns normalized intent into an evidenced retrieve-or-skip
decision before model-assisted query transformation.

## Policy v1

The `nao-retrieval-admission@1` policy is deliberately conservative:

| Intent | Decision | Reason |
|---|---|---|
| `KnowledgeSeeking` | retrieve | explicit knowledge intent |
| `NonKnowledge` | skip | explicit non-knowledge intent |
| `Unspecified` | retrieve | unspecified intent |

Unspecified intent retrieves because an uncertain classifier must not silently suppress relevant
knowledge. Hosts should use `NonKnowledge` only when their trusted task-routing policy has positive
evidence that retrieval is unnecessary.

## Execution

Call `KnowledgeQueryUnderstanding.planAsync` after deterministic normalization:

```fsharp
let! understanding =
    KnowledgeQueryUnderstanding.planAsync
        transformationModel
        normalizedQuery
        cancellationToken
```

The result is either:

- `RetrievalSkipped admission`; or
- `RetrievalPlanned (admission, transformationPlan)`.

Admission occurs before transformation. A skipped request therefore consumes no transformation
model calls, tokens, or cost, even when the request selected a model-assisted strategy.

## Evidence and security

Admission evidence records the normalized query, exact request, policy descriptor, decision, and
typed reason. It does not create a replacement request. Authorization, namespace, filters,
strategy, limits, and budget remain the original immutable values.

Cancellation is checked before policy evaluation and produces no decision.
