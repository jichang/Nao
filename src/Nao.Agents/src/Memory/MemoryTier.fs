namespace Nao.Agents

open System
open System.Threading.Tasks
open Nao.Agents

/// Three-tier memory model: Short-term (active context), Mid-term (session), Long-term (persistent)
[<RequireQualifiedAccess>]
type MemoryTier =
    /// Active context window — messages in the current LLM call
    | ShortTerm
    /// Session state — persists within a conversation session
    | MidTerm
    /// Long-term persistent memory — survives across sessions
    | LongTerm

/// A memory item with tier classification
type TieredMemoryEntry =
    { /// The memory content
      Key: string
      Value: string
      /// Which tier this memory belongs to
      Tier: MemoryTier
      /// When created or last accessed
      Timestamp: DateTimeOffset
      /// Access count for LRU/frequency-based eviction
      AccessCount: int
      /// Relevance score (updated by retrieval)
      Relevance: float
      /// Tags for filtering
      Tags: string list }

/// Policy for memory promotion/demotion between tiers
[<RequireQualifiedAccess>]
type MemoryPromotionPolicy =
    /// Promote to higher tier after N accesses
    | AccessThreshold of count: int
    /// Promote based on recency
    | RecencyBased of maxAge: TimeSpan
    /// Never promote automatically
    | Manual

/// Configuration for tiered memory management
type TieredMemoryConfig =
    { /// Maximum items in short-term memory
      ShortTermCapacity: int
      /// Maximum items in mid-term memory
      MidTermCapacity: int
      /// Policy for promoting mid-term -> long-term
      PromotionPolicy: MemoryPromotionPolicy
      /// How long before mid-term memories expire
      MidTermTtl: TimeSpan option
      /// Whether to auto-evict on capacity overflow
      AutoEvict: bool }

    static member Default =
        { ShortTermCapacity = 20
          MidTermCapacity = 100
          PromotionPolicy = MemoryPromotionPolicy.AccessThreshold 5
          MidTermTtl = Some (TimeSpan.FromHours 24.0)
          AutoEvict = true }

/// Functional tiered-memory operations.
type TieredMemory =
    { /// Store a memory at a specific tier
      StoreAsync: TieredMemoryEntry -> Task<unit>
      /// Retrieve relevant memories across all tiers
      RetrieveAsync: string -> int -> Task<TieredMemoryEntry list>
      /// Retrieve memories from a specific tier
      RetrieveFromTierAsync: MemoryTier -> int -> Task<TieredMemoryEntry list>
      /// Promote a memory to a higher tier
      PromoteAsync: string -> MemoryTier -> Task<unit>
      /// Evict memories that exceed capacity or TTL
      EvictAsync: unit -> Task<int> }
