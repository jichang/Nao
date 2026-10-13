# Grounded generation adapter

Nao now has a provider-neutral production boundary between assembled knowledge context and the
grounded response protocol.

## Breaking provider contract

`LlmProvider.CompleteAsync` and `LlmProvider.StreamAsync` now require a `CancellationToken`.
Providers and callers must pass the operation token through every network send and response read.
Cancellation is no longer implemented by abandoning a wait while provider I/O continues.

## Generation contract

`KnowledgeGroundedGenerationAdapter` binds:

- the exact authorization scope used to assemble context;
- a versioned adapter component and configuration hash;
- the grounded response protocol descriptor;
- an `LlmProvider` and completion options;
- duration, cumulative token, and repair-attempt budgets.

Successful executions retain the assembled context, typed grounded result, protocol identity,
provider-call count, token usage, and elapsed duration. Missing token evidence, excessive usage,
invalid protocol cardinality, and exhausted repair attempts return typed rejection evidence.
Caller cancellation reaches provider I/O; an adapter duration overrun raises `TimeoutException`.

Callers must replace old provider callbacks with cancellation-aware callbacks and pass an
operation token to direct completion and streaming calls. There is no compatibility overload.

## Qualification boundary

The deterministic RAG release fixture remains threshold evidence only. The adapter establishes
the production generation architecture, but the R2 production-adapter-set exit criterion remains
open until a real production model executes and passes the adversarial release profile.
