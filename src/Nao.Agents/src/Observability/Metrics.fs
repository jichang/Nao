namespace Nao.Agents

open System
open System.Threading
open System.Threading.Tasks

/// Cost model for LLM provider pricing
type CostModel =
    {
        /// Cost per 1K input tokens in USD
        InputCostPer1K: decimal
        /// Cost per 1K output tokens in USD
        OutputCostPer1K: decimal
    }

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

/// One accepted metric observation.
[<RequireQualifiedAccess>]
type MetricPayload =
    | LlmCall of inputTokens: int * outputTokens: int * latencyMs: int64
    | ToolCall of toolName: string * durationMs: int64 * success: bool
    | Custom of MetricPoint

/// Durable identity and ownership for a metric observation.
type MetricRecord =
    { Id: Guid
      Owner: string
      Timestamp: DateTimeOffset
      Payload: MetricPayload }

module MetricRecord =
    let llmCall owner timestamp inputTokens outputTokens latencyMs : MetricRecord =
        { Id = Guid.NewGuid()
          Owner = owner
          Timestamp = timestamp
          Payload = MetricPayload.LlmCall(inputTokens, outputTokens, latencyMs) }

    let toolCall owner timestamp toolName durationMs success : MetricRecord =
        { Id = Guid.NewGuid()
          Owner = owner
          Timestamp = timestamp
          Payload = MetricPayload.ToolCall(toolName, durationMs, success) }

    let custom owner timestamp point : MetricRecord =
        { Id = Guid.NewGuid()
          Owner = owner
          Timestamp = timestamp
          Payload = MetricPayload.Custom point }

/// Aggregated metrics for an agent execution
type ExecutionMetrics =
    {
        /// Core resource usage (shared with Environment layer)
        Usage: ResourceUsage
        /// Total LLM calls made
        TotalLlmCalls: int
        /// Total input tokens consumed
        TotalInputTokens: int
        /// Total output tokens generated
        TotalOutputTokens: int
        /// Total estimated cost in USD
        TotalCostUsd: decimal
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
        /// Time spent in tool execution
        ToolExecutionTime: TimeSpan
    }

    static member Zero =
        { Usage = ResourceUsage.Zero
          TotalLlmCalls = 0
          TotalInputTokens = 0
          TotalOutputTokens = 0
          TotalCostUsd = 0m
          TotalToolCalls = 0
          AvgLatencyMs = 0.0
          P95LatencyMs = 0.0
          TotalDuration = TimeSpan.Zero
          LlmWaitTime = TimeSpan.Zero
          ToolExecutionTime = TimeSpan.Zero }

    /// Create from ResourceUsage (bridge from Environment layer)
    static member FromUsage(usage: ResourceUsage) =
        { ExecutionMetrics.Zero with
            Usage = usage
            TotalLlmCalls = usage.LlmCalls
            TotalToolCalls = usage.ToolCalls
            TotalCostUsd = usage.EstimatedCostUsd
            TotalDuration = usage.ElapsedTime }

/// Functional metrics collection operations.
type MetricsCollector =
    {
        /// Accept one complete metric observation
        Record: MetricRecord -> unit
        /// Get aggregated metrics for one owner
        GetMetrics: string -> ExecutionMetrics
        /// Calculate one owner's cost using a cost model
        EstimateCost: string -> CostModel -> decimal
        /// Delete every metric observation owned by one scope
        DeleteOwnerAsync: string -> Task<Result<int, PlatformFailure>>
        /// Delete observations before a strict cutoff
        DeleteExpiredAsync: string -> DateTimeOffset -> Task<Result<int, PlatformFailure>>
    }

module internal RuntimeMetrics =
    let private current = AsyncLocal<MetricsCollector option>()

    let get () = current.Value
    let set value = current.Value <- value
