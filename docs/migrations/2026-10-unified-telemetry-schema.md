# Unified telemetry schema

Nao observability now uses one version-1 schema for traces and metrics. This is a direct
replacement because the project is unreleased.

`TelemetryAttribute`, `TelemetrySpan`, and `TelemetryInstrument.definitions` are the canonical
attribute, span, and metric vocabularies. Custom metric points must name a defined instrument and
use only its bounded label allowlist. User content is not an accepted metric label.

Harness, orchestration, provider, and tool spans now share a trace ID derived from the stable
correlation ID. Root spans include tenant, workspace, session, turn, execution, correlation,
causation, attempt, and agent attributes without prompt or response content. Error status uses the
bounded `TelemetryErrorCategory`; terminal state uses `TelemetryTerminalState`.

Orleans session processing now requires `OrleansCorrelationContext`. Hosts must construct it from
their active `CorrelationContext` instead of relying on the grain to create a root. The worker
protocol remains version 1 but now carries `causationExecutionId`, and workers reconstruct the
exact execution, correlation, causation, and retry identity.

`MetricPayload.LlmCall` now accepts `ProviderTokenUsage.Split`, `Aggregate`, or `Unavailable`.
`ExecutionMetrics.TotalInputTokens`, `TotalOutputTokens`, and `TotalProviderTokens` are optional.
Unavailable provider evidence must remain unavailable.

`CostModel` is constructed with a caller-owned pricing ID and version. `EstimateCost` returns an
optional `CostEstimate` that retains both identities; it returns `None` when provider-reported
splits are incomplete.
