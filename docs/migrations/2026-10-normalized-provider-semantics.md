# Normalized provider semantics

Nao now uses one typed completion and failure contract across provider adapters. This is a
breaking replacement; the previous string and optional-field shapes are not retained.

## Completion results

Replace free-form finish-reason strings with `ProviderFinishReason` and replace `TokensUsed` plus
optional split usage with one `ProviderUsage` value.

```fsharp
CompletionResult.text
    content
    ProviderFinishReason.Stop
    (ProviderUsage.full inputTokens outputTokens)
```

Use `ProviderUsage.aggregate totalTokens` only when the provider reports no split. Use
`ProviderUsage.Partial` when only part of the split is known and `ProviderUsage.None` when no
usage was reported. Call `ProviderUsage.total` rather than reading or reconstructing totals.

Tool calls are `ProviderToolCall` values with an ID, name, and canonical JSON arguments string.
Structured provider output belongs in `StructuredOutputJson`. Preserve real provider-specific
response metadata in `Extensions`; common consumers must ignore keys they do not recognize.

## Streaming

`CompletionChunk.Termination` replaces inferred end-of-stream behavior:

- `Completed finishReason` is a successful terminal chunk.
- `Partial providerErrorCategory` means content was emitted before failure.
- `None` is a non-terminal chunk.

Adapters emit a partial terminal chunk and then raise a structured failure. Callers must not
concatenate partial content with a retry or fallback response.

## Failures and retry

Provider-boundary `PlatformFailure` values now include `ProviderCategory`. Branch on
`ProviderErrorCategory` or the structured `Retryable` flag, never on message text. HTTP failure
messages intentionally omit response bodies.

Caller cancellation remains `OperationCanceledException`. A provider or internal timeout that is
not caused by the caller token becomes `ProviderErrorCategory.Timeout`.

## Adapter implementations

An adapter must return the normalized buffered shape, emit an equivalent normalized streaming
result, preserve only actual provider metadata, and pass the shared conformance suite. Do not add
compatibility constructors or duplicate fields for the removed contract.
