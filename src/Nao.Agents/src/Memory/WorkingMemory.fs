namespace Nao.Agents

open System
open System.Threading.Tasks

/// A scratchpad item in working memory with priority/attention weight
type WorkingMemoryItem =
    { Key: string
      Content: string
      /// Priority/attention weight — higher means more relevant to current task
      Attention: float
      /// Source of this item (e.g., "tool:search", "memory:long-term", "user:input")
      Source: string
      /// When added to working memory
      AddedAt: DateTimeOffset
      /// TTL in working memory before auto-decay
      ExpiresAt: DateTimeOffset option
      /// Whether this is pinned (immune to eviction)
      Pinned: bool }

/// Configuration for working memory
type WorkingMemoryConfig =
    { /// Maximum number of items in working memory
      Capacity: int
      /// Default TTL for unpinned items
      DefaultTtl: TimeSpan
      /// Attention decay rate per retrieval cycle (0.0 - 1.0)
      DecayRate: float
      /// Minimum attention threshold before eviction
      EvictionThreshold: float }

    static member Default =
        { Capacity = 15
          DefaultTtl = TimeSpan.FromMinutes 30.0
          DecayRate = 0.05
          EvictionThreshold = 0.1 }

/// Interface for task-scoped working memory (scratchpad)
type IWorkingMemory =
    /// Add or update an item in working memory
    abstract member SetAsync: WorkingMemoryItem -> Task<unit>
    /// Get an item by key (boosts its attention)
    abstract member GetAsync: key: string -> Task<WorkingMemoryItem option>
    /// Get all items sorted by attention (highest first)
    abstract member GetAllAsync: unit -> Task<WorkingMemoryItem list>
    /// Get items above an attention threshold
    abstract member GetActiveAsync: minAttention: float -> Task<WorkingMemoryItem list>
    /// Boost attention for a specific item
    abstract member FocusAsync: key: string -> boost: float -> Task<unit>
    /// Apply decay to all non-pinned items and evict expired/below-threshold
    abstract member DecayAsync: unit -> Task<int>
    /// Pin an item (prevent eviction)
    abstract member PinAsync: key: string -> Task<unit>
    /// Unpin an item
    abstract member UnpinAsync: key: string -> Task<unit>
    /// Remove a specific item
    abstract member RemoveAsync: key: string -> Task<unit>
    /// Clear all working memory
    abstract member ClearAsync: unit -> Task<unit>
    /// Render working memory as context for LLM (top-K by attention)
    abstract member RenderContextAsync: topK: int -> Task<string>
