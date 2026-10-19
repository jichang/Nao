namespace Nao.Agents

open System
open System.Threading
open System.Threading.Tasks

/// Versioned caller-owned LLM pricing. Prices are never embedded in provider adapters.
type CostModel =
    private
        { PricingId: string
          Version: string
          InputCostPer1K: decimal
          OutputCostPer1K: decimal }

[<RequireQualifiedAccess>]
module CostModel =
    let create pricingId version inputCostPer1K outputCostPer1K =
        if String.IsNullOrWhiteSpace pricingId || pricingId <> pricingId.Trim() then
            invalidArg (nameof pricingId) "Pricing identity must be canonical and non-blank."

        if String.IsNullOrWhiteSpace version || version <> version.Trim() then
            invalidArg (nameof version) "Pricing version must be canonical and non-blank."

        if inputCostPer1K < 0m || outputCostPer1K < 0m then
            invalidArg (nameof inputCostPer1K) "Token prices cannot be negative."

        { PricingId = pricingId
          Version = version
          InputCostPer1K = inputCostPer1K
          OutputCostPer1K = outputCostPer1K }

    let pricingId value = value.PricingId
    let version value = value.Version
    let inputCostPer1K value = value.InputCostPer1K
    let outputCostPer1K value = value.OutputCostPer1K

type CostEstimate =
    { PricingId: string
      PricingVersion: string
      AmountUsd: decimal }

/// A single metrics data point
type MetricPoint =
    {
        /// Metric name
        Name: string
        /// Metric value
        Value: float
        /// Dimension labels
        Labels: Map<string, string>
    }

/// Provider-reported usage fidelity. Missing splits are never represented as zero.
[<RequireQualifiedAccess>]
type ProviderTokenUsage =
    | Split of inputTokens: int * outputTokens: int
    | Aggregate of totalTokens: int
    | Unavailable

/// One accepted metric observation.
[<RequireQualifiedAccess>]
type MetricPayload =
    | LlmCall of tokens: ProviderTokenUsage * latencyMs: int64
    | EmbeddingCall of
        profileId: string *
        batchId: string *
        inputCount: int *
        inputTokens: int *
        latencyMs: int64 *
        estimatedCostUsd: decimal *
        providerAttempts: int
    | ToolCall of toolName: string * durationMs: int64 * success: bool
    | Custom of MetricPoint

/// One metric observation. Authorization identity is supplied separately by the host.
type MetricRecord =
    { Id: Guid
      Correlation: CorrelationContext
      Timestamp: DateTimeOffset
      Payload: MetricPayload }

module MetricRecord =
    let llmCall correlation timestamp tokens latencyMs : MetricRecord =
        match tokens with
        | ProviderTokenUsage.Split(inputTokens, outputTokens) when inputTokens < 0 || outputTokens < 0 ->
            invalidArg (nameof tokens) "Provider token splits cannot be negative."
        | ProviderTokenUsage.Aggregate totalTokens when totalTokens < 0 ->
            invalidArg (nameof tokens) "Provider aggregate token usage cannot be negative."
        | _ -> ()

        if latencyMs < 0L then
            invalidArg (nameof latencyMs) "Provider latency cannot be negative."

        { Id = Guid.NewGuid()
          Correlation = correlation
          Timestamp = timestamp
          Payload = MetricPayload.LlmCall(tokens, latencyMs) }

    let toolCall correlation timestamp toolName durationMs success : MetricRecord =
        { Id = Guid.NewGuid()
          Correlation = correlation
          Timestamp = timestamp
          Payload = MetricPayload.ToolCall(toolName, durationMs, success) }

    let embeddingCall
        correlation
        timestamp
        profileId
        batchId
        inputCount
        inputTokens
        latencyMs
        estimatedCostUsd
        providerAttempts
        : MetricRecord =
        { Id = Guid.NewGuid()
          Correlation = correlation
          Timestamp = timestamp
          Payload =
            MetricPayload.EmbeddingCall(
                profileId,
                batchId,
                inputCount,
                inputTokens,
                latencyMs,
                estimatedCostUsd,
                providerAttempts
            ) }

    let custom correlation timestamp point : MetricRecord =
        let instrument =
            TelemetryInstrument.tryFind point.Name
            |> Option.defaultWith (fun () ->
                invalidArg (nameof point) "Custom metrics must use a canonical telemetry instrument.")

        TelemetryAttributes.validateMetricLabels instrument point.Labels |> ignore

        { Id = Guid.NewGuid()
          Correlation = correlation
          Timestamp = timestamp
          Payload = MetricPayload.Custom point }

/// Aggregated metrics for an agent execution
type ExecutionMetrics =
    {
        /// Core resource usage (shared with Environment layer)
        Usage: ResourceUsage
        /// Total LLM calls made
        TotalLlmCalls: int
        /// Total embedding batches executed
        TotalEmbeddingCalls: int
        /// Total input tokens when every provider call reported a split.
        TotalInputTokens: int option
        /// Total output tokens when every provider call reported a split.
        TotalOutputTokens: int option
        /// Total provider tokens when every provider call reported at least an aggregate.
        TotalProviderTokens: int option
        /// Total embedding input tokens consumed
        TotalEmbeddingInputTokens: int
        /// Total estimated cost in USD
        TotalCostUsd: decimal
        /// Total embedding cost in USD
        TotalEmbeddingCostUsd: decimal
        /// Total tool invocations
        TotalToolCalls: int
        /// Average latency per LLM call in milliseconds
        AvgLatencyMs: float
        /// P95 latency in milliseconds
        P95LatencyMs: float
        /// Total execution time
        TotalDuration: TimeSpan
        /// Time spent waiting for LLM responses
        LlmWaitTime: TimeSpan
        /// Time spent waiting for embedding responses
        EmbeddingWaitTime: TimeSpan
        /// Time spent in tool execution
        ToolExecutionTime: TimeSpan
    }

    static member Zero =
        { Usage = ResourceUsage.Zero
          TotalLlmCalls = 0
          TotalEmbeddingCalls = 0
          TotalInputTokens = Some 0
          TotalOutputTokens = Some 0
          TotalProviderTokens = Some 0
          TotalEmbeddingInputTokens = 0
          TotalCostUsd = 0m
          TotalEmbeddingCostUsd = 0m
          TotalToolCalls = 0
          AvgLatencyMs = 0.0
          P95LatencyMs = 0.0
          TotalDuration = TimeSpan.Zero
          LlmWaitTime = TimeSpan.Zero
          EmbeddingWaitTime = TimeSpan.Zero
          ToolExecutionTime = TimeSpan.Zero }

    /// Create from ResourceUsage (bridge from Environment layer)
    static member FromUsage(usage: ResourceUsage) =
        { ExecutionMetrics.Zero with
            Usage = usage
            TotalLlmCalls = usage.LlmCalls
            TotalEmbeddingCalls = usage.EmbeddingCalls
            TotalToolCalls = usage.ToolCalls
            TotalCostUsd = usage.EstimatedCostUsd
            TotalDuration = usage.ElapsedTime }

/// Functional metrics collection operations.
type MetricsCollector =
    {
        /// Accept one complete metric observation
        Record: AuthorizationScope -> MetricRecord -> unit
        /// Get aggregated metrics in one authenticated scope
        GetMetrics: AuthorizationScope -> ExecutionMetrics
        /// Get observations produced by one execution in one authenticated scope
        GetByExecution: AuthorizationScope -> ExecutionId -> MetricRecord list
        /// Calculate cost only when every provider call reported input/output splits.
        EstimateCost: AuthorizationScope -> CostModel -> CostEstimate option
        /// Delete every metric observation in one authenticated scope
        DeleteScopeAsync: AuthorizationScope -> Task<Result<int, PlatformFailure>>
        /// Delete observations in one authenticated scope before a strict cutoff
        DeleteExpiredAsync: AuthorizationScope -> DateTimeOffset -> Task<Result<int, PlatformFailure>>
    }

module internal RuntimeMetrics =
    let private current = AsyncLocal<MetricsCollector option>()

    let get () = current.Value
    let set value = current.Value <- value
