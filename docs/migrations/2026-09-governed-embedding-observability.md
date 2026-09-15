# 2026-09 governed embedding observability

## Scope

- Current unreleased formats remain version 1.
- Affected APIs: `ResourceLimits`, `ResourceUsage`, `LimitExceeded`,
  `WorkerExecutionBudget`, `MetricPayload`, `ExecutionMetrics`, and `TraceAction`.
- Affected durable data: worker protocol frames, metric events, spans, and execution traces.

## Breaking changes

Embedding batches are now a distinct governed resource. Budgets expose `MaxEmbeddingCalls`,
usage exposes `EmbeddingCalls`, and worker frames carry both fields. Metrics add
`EmbeddingCall`, aggregate execution metrics add embedding calls, tokens, cost, and wait time,
and evaluation traces add `EmbeddingCall`.

## Migration

Nao is unreleased, so update current v1 worker readers and writers in place. Do not interpret
embedding work as `LlmCalls`. Reset development worker frames or durable metric/trace fixtures
that use the previous v1 shape.

Hosts must preserve these rules:

1. Reserve the embedding-call budget before provider transport.
2. Record provider-reported tokens and cost after a successful batch.
3. Keep total-token and cost limits shared across LLM and embedding work.
4. Emit only profile ID, stable batch ID, numeric counts, latency, cost, attempts, and status.
5. Never add chunk text, vectors, provider bodies, principal evidence, or credentials to metrics,
   spans, or evaluation trace steps.

## Validation

Run the Agents, Knowledge, Persistence, E2E, and Eval test projects. Verify zero-call rejection
before transport, separate LLM/embedding counts, shared token and cost enforcement, persistent
metric replay, text-free child spans, and `EmbeddingCall` steps in captured evaluation traces.

## Rollback

Restore resource, worker, metric, trace, and evaluation types together. Do not collapse retained
embedding calls into historical LLM-call counts.
