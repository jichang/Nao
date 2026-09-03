namespace Nao.Agents

open System
open System.Threading.Tasks

/// A single memory entry stored by an agent
type MemoryEntry =
    { /// Unique key identifying this memory (e.g. "user-name", "preference.theme")
      Key: string
      /// The stored value
      Value: string
      /// When the memory was created or last updated
      Timestamp: DateTimeOffset
      /// Optional classification tags for filtering
      Tags: string list }

/// Functional operations for persisting and retrieving agent memories.
type MemoryStore =
    { /// Save a memory entry for an agent
      SaveAsync: string -> MemoryEntry -> Task<unit>
      /// Recall memories by key prefix match
      RecallAsync: string -> string -> Task<MemoryEntry list>
      /// Recall all memories for an agent
      RecallAllAsync: string -> Task<MemoryEntry list>
      /// Forget (delete) a memory by key
      ForgetAsync: string -> string -> Task<unit>
      /// Clear all memories for an agent
      ClearAsync: string -> Task<unit> }
