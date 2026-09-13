namespace Nao.Agents

open System
open System.Threading.Tasks

/// A single memory entry stored by an agent
type MemoryEntry =
    {
        /// Unique key identifying this memory (e.g. "user-name", "preference.theme")
        Key: string
        /// The stored value
        Value: string
        /// When the memory was created or last updated
        Timestamp: DateTimeOffset
        /// Optional classification tags for filtering
        Tags: string list
    }

/// Key/value memory owned by the complete authenticated scope, not a caller-supplied owner string.
type MemoryStore =
    {
        /// Save a memory entry in the authenticated scope.
        SaveAsync: AuthorizationScope -> MemoryEntry -> Task<unit>
        /// Recall memories by case-insensitive key substring match.
        RecallAsync: AuthorizationScope -> string -> Task<MemoryEntry list>
        /// Recall all memories in the authenticated scope.
        RecallAllAsync: AuthorizationScope -> Task<MemoryEntry list>
        /// Forget (delete) a memory by key
        ForgetAsync: AuthorizationScope -> string -> Task<unit>
        /// Delete every memory in the authenticated scope.
        DeleteOwnerAsync: AuthorizationScope -> Task<Result<int, PlatformFailure>>
        /// Delete memories in the authenticated scope that precede a retention cutoff.
        DeleteExpiredAsync: AuthorizationScope -> DateTimeOffset -> Task<Result<int, PlatformFailure>>
    }
