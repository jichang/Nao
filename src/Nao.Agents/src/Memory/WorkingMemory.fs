namespace Nao.Agents

open System
open System.Threading.Tasks

/// A scratchpad item in working memory with priority/attention weight
type WorkingMemoryItem =
    {
        ExecutionId: ExecutionId
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
      GetAsync: ExecutionId -> string -> Task<WorkingMemoryItem option>
      GetAllAsync: ExecutionId -> Task<WorkingMemoryItem list>
      GetActiveAsync: ExecutionId -> float -> Task<WorkingMemoryItem list>
      FocusAsync: ExecutionId -> string -> float -> Task<unit>
      DecayAsync: ExecutionId -> DateTimeOffset -> Task<int>
      PinAsync: ExecutionId -> string -> Task<unit>
      UnpinAsync: ExecutionId -> string -> DateTimeOffset -> Task<unit>
      RemoveAsync: ExecutionId -> string -> Task<unit>
      DeleteOwnerAsync: ExecutionId -> Task<Result<int, PlatformFailure>>
      DeleteExpiredAsync: ExecutionId -> DateTimeOffset -> Task<Result<int, PlatformFailure>>
      RenderContextAsync: ExecutionId -> int -> Task<string> }
