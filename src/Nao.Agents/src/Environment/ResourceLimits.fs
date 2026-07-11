namespace Nao.Agents

open System

/// Resource budget constraints for agent execution
type ResourceLimits =
    { /// Maximum wall-clock time allowed
      MaxDuration: TimeSpan
      /// Maximum number of LLM calls permitted
      MaxLlmCalls: int
      /// Maximum total tokens (input + output) across all calls
      MaxTotalTokens: int
      /// Maximum monetary cost in USD (approximate)
      MaxCostUsd: decimal
      /// Maximum number of tool invocations
      MaxToolCalls: int
      /// Maximum memory (bytes) the sandbox may consume
      MaxMemoryBytes: int64 option }

    static member Unlimited =
        { MaxDuration = TimeSpan.FromHours 1.0
          MaxLlmCalls = Int32.MaxValue
          MaxTotalTokens = Int32.MaxValue
          MaxCostUsd = Decimal.MaxValue
          MaxToolCalls = Int32.MaxValue
          MaxMemoryBytes = None }

    static member Constrained (durationSeconds: int) (llmCalls: int) (tokens: int) =
        { ResourceLimits.Unlimited with
            MaxDuration = TimeSpan.FromSeconds(float durationSeconds)
            MaxLlmCalls = llmCalls
            MaxTotalTokens = tokens }

/// Tracks current resource consumption against limits
type ResourceUsage =
    { LlmCalls: int
      TotalTokens: int
      ToolCalls: int
      EstimatedCostUsd: decimal
      ElapsedTime: TimeSpan }

    static member Zero =
        { LlmCalls = 0
          TotalTokens = 0
          ToolCalls = 0
          EstimatedCostUsd = 0m
          ElapsedTime = TimeSpan.Zero }

    member this.Exceeds(limits: ResourceLimits) =
        this.LlmCalls > limits.MaxLlmCalls
        || this.TotalTokens > limits.MaxTotalTokens
        || this.ToolCalls > limits.MaxToolCalls
        || this.EstimatedCostUsd > limits.MaxCostUsd
        || this.ElapsedTime > limits.MaxDuration

/// The specific limit that was exceeded
[<RequireQualifiedAccess>]
type LimitExceeded =
    | Duration
    | LlmCalls
    | TotalTokens
    | Cost
    | ToolCalls
    | Memory

module ResourceUsage =
    let check (limits: ResourceLimits) (usage: ResourceUsage) : LimitExceeded option =
        if usage.ElapsedTime > limits.MaxDuration then Some LimitExceeded.Duration
        elif usage.LlmCalls > limits.MaxLlmCalls then Some LimitExceeded.LlmCalls
        elif usage.TotalTokens > limits.MaxTotalTokens then Some LimitExceeded.TotalTokens
        elif usage.EstimatedCostUsd > limits.MaxCostUsd then Some LimitExceeded.Cost
        elif usage.ToolCalls > limits.MaxToolCalls then Some LimitExceeded.ToolCalls
        else None
