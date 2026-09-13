namespace Nao.Agents

open System
open System.Threading.Tasks

/// A scratchpad item in working memory with priority/attention weight
type WorkingMemoryItem =
    {
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

/// Authenticated ownership plus one host-issued execution identifier.
type WorkingMemoryAddress =
    private
        { Authorization: AuthorizationScope
          ExecutionId: ExecutionId }

module WorkingMemoryAddress =
    let create authorization executionId =
        { Authorization = authorization
          ExecutionId = executionId }

    let authorization address = address.Authorization
    let executionId address = address.ExecutionId

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
    { SetAsync: WorkingMemoryAddress -> WorkingMemoryItem -> Task<unit>
      GetAsync: WorkingMemoryAddress -> string -> Task<WorkingMemoryItem option>
      GetAllAsync: WorkingMemoryAddress -> Task<WorkingMemoryItem list>
      GetActiveAsync: WorkingMemoryAddress -> float -> Task<WorkingMemoryItem list>
      FocusAsync: WorkingMemoryAddress -> string -> float -> Task<unit>
      DecayAsync: WorkingMemoryAddress -> DateTimeOffset -> Task<int>
      PinAsync: WorkingMemoryAddress -> string -> Task<unit>
      UnpinAsync: WorkingMemoryAddress -> string -> DateTimeOffset -> Task<unit>
      RemoveAsync: WorkingMemoryAddress -> string -> Task<unit>
      DeleteOwnerAsync: WorkingMemoryAddress -> Task<Result<int, PlatformFailure>>
      DeleteExpiredAsync: WorkingMemoryAddress -> DateTimeOffset -> Task<Result<int, PlatformFailure>>
      RenderContextAsync: WorkingMemoryAddress -> int -> Task<string> }
