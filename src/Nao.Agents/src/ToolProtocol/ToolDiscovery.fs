namespace Nao.Agents

open System
open System.Threading.Tasks

/// Dynamic tool discovery and pruning for context-window efficiency
[<RequireQualifiedAccess>]
type DiscoverySource =
    /// Local tools registered in-process
    | Local
    /// MCP servers
    | Mcp of serverName: string
    /// Plugin assembly
    | Assembly of path: string

/// Tool availability status
[<RequireQualifiedAccess>]
type ToolAvailability =
    | Available
    | Unavailable of reason: string
    | Degraded of reason: string
    | RateLimited of retryAfter: TimeSpan

/// Tool usage statistics for ranking/pruning
type ToolUsageStats =
    { ToolName: string
      InvocationCount: int
      SuccessCount: int
      FailureCount: int
      AverageLatencyMs: float
      LastUsed: DateTimeOffset option
      TotalCost: float }

/// Configuration for tool discovery and pruning
type ToolDiscoveryConfig =
    { /// Maximum tools to include in LLM context
      MaxToolsInContext: int
      /// Minimum relevance score to include a tool
      RelevanceThreshold: float
      /// Whether to refresh tool availability periodically
      AutoRefresh: bool
      /// Refresh interval
      RefreshInterval: TimeSpan }

    static member Default =
        { MaxToolsInContext = 20
          RelevanceThreshold = 0.1
          AutoRefresh = false
          RefreshInterval = TimeSpan.FromMinutes 5.0 }

/// Interface for dynamic tool discovery, ranking, and context-window pruning
type IToolDiscovery =
    /// Discover tools from all registered sources
    abstract member DiscoverAsync: unit -> Task<ToolSchema list>
    /// Rank tools by relevance to a given query/task
    abstract member RankForTaskAsync: taskDescription: string -> maxTools: int -> Task<(ToolSchema * float) list>
    /// Check availability of a specific tool
    abstract member CheckAvailabilityAsync: toolName: string -> Task<ToolAvailability>
    /// Get usage statistics
    abstract member GetStatsAsync: toolName: string -> Task<ToolUsageStats option>
    /// Record a tool invocation (for stats tracking)
    abstract member RecordInvocationAsync: toolName: string -> success: bool -> latencyMs: int64 -> cost: float -> Task<unit>
    /// Prune tools for context window — returns the most relevant subset
    abstract member PruneForContextAsync: taskDescription: string -> availableTokenBudget: int -> Task<ToolSchema list>
