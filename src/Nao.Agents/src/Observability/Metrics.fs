namespace Nao.Agents

open System
open System.Threading
open System.Threading.Tasks

/// Cost model for LLM provider pricing
type CostModel =
    { /// Cost per 1K input tokens in USD
      InputCostPer1K: decimal
      /// Cost per 1K output tokens in USD
      OutputCostPer1K: decimal }

/// A single metrics data point
type MetricPoint =
    { /// Metric name
      Name: string
      /// Metric value
      Value: float
      /// When recorded
      Timestamp: DateTimeOffset
      /// Dimension labels
      Labels: Map<string, string> }

/// Aggregated metrics for an agent execution
type ExecutionMetrics =
    { /// Core resource usage (shared with Environment layer)
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
      ToolExecutionTime: TimeSpan }

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
    static member FromUsage (usage: ResourceUsage) =
        { ExecutionMetrics.Zero with
            Usage = usage
            TotalLlmCalls = usage.LlmCalls
            TotalToolCalls = usage.ToolCalls
            TotalCostUsd = usage.EstimatedCostUsd
            TotalDuration = usage.ElapsedTime }

/// Interface for metrics collection
type IMetricsCollector =
    /// Record an LLM call with token counts and latency
    abstract member RecordLlmCall: inputTokens: int -> outputTokens: int -> latencyMs: int64 -> unit
    /// Record a tool invocation with duration
    abstract member RecordToolCall: toolName: string -> durationMs: int64 -> success: bool -> unit
    /// Record a custom metric point
    abstract member RecordMetric: MetricPoint -> unit
    /// Get aggregated metrics
    abstract member GetMetrics: unit -> ExecutionMetrics
    /// Calculate cost using a cost model
    abstract member EstimateCost: CostModel -> decimal

module internal RuntimeMetrics =
    let private current = AsyncLocal<IMetricsCollector option>()

    let get () = current.Value
    let set value = current.Value <- value
