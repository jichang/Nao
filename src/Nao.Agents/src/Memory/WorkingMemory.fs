namespace Nao.Agents

open System
open System.Threading.Tasks

/// A scratchpad item in working memory with priority/attention weight
type WorkingMemoryItem =
    {
        ExecutionId: string
        Key: string
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
        Pinned: bool
    }

/// Configuration for working memory
type WorkingMemoryConfig =
    {
        /// Maximum number of items in working memory
        Capacity: int
        /// Default TTL for unpinned items
        DefaultTtl: TimeSpan
        /// Attention decay rate per retrieval cycle (0.0 - 1.0)
        DecayRate: float
        /// Minimum attention threshold before eviction
        EvictionThreshold: float
    }

    static member Default =
        { Capacity = 15
          DefaultTtl = TimeSpan.FromMinutes 30.0
          DecayRate = 0.05
          EvictionThreshold = 0.1 }

/// Functional task-scoped working-memory (scratchpad) operations.
type WorkingMemory =
    { SetAsync: WorkingMemoryItem -> Task<unit>
      GetAsync: string -> string -> Task<WorkingMemoryItem option>
      GetAllAsync: string -> Task<WorkingMemoryItem list>
      GetActiveAsync: string -> float -> Task<WorkingMemoryItem list>
      FocusAsync: string -> string -> float -> Task<unit>
      DecayAsync: string -> DateTimeOffset -> Task<int>
      PinAsync: string -> string -> Task<unit>
      UnpinAsync: string -> string -> DateTimeOffset -> Task<unit>
      RemoveAsync: string -> string -> Task<unit>
      DeleteOwnerAsync: string -> Task<Result<int, PlatformFailure>>
      DeleteExpiredAsync: string -> DateTimeOffset -> Task<Result<int, PlatformFailure>>
      RenderContextAsync: string -> int -> Task<string> }
