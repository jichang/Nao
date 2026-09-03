namespace Nao.Agents

open System
open System.Threading.Tasks
open Nao.Agents

/// A memory entry with an embedding vector for semantic retrieval
type SemanticEntry =
    { Key: string
      Content: string
      Embedding: float array
      Timestamp: DateTimeOffset
      Tags: string list }

/// Functional text-embedding capability.
type EmbeddingProvider =
    { /// Generate an embedding vector for the given text
      EmbedAsync: string -> Task<float array> }

/// Functional semantic memory that uses embeddings for similarity-based retrieval.
type SemanticMemory =
    { /// Store a memory with its embedding
      StoreAsync: string -> string -> string -> Task<unit>
      /// Retrieve the top-k most relevant memories for a query
      RetrieveAsync: string -> string -> int -> Task<SemanticEntry list>
      /// Remove a memory by key
      RemoveAsync: string -> string -> Task<unit> }

module SemanticSimilarity =

    /// Compute cosine similarity between two vectors (handles different lengths by zero-padding)
    let cosineSimilarity (a: float array) (b: float array) =
        if a.Length = 0 && b.Length = 0 then 0.0
        else
            let maxLen = max a.Length b.Length
            let mutable dot = 0.0
            let mutable normA = 0.0
            let mutable normB = 0.0
            for i in 0 .. maxLen - 1 do
                let ai = if i < a.Length then a.[i] else 0.0
                let bi = if i < b.Length then b.[i] else 0.0
                dot <- dot + ai * bi
                normA <- normA + ai * ai
                normB <- normB + bi * bi
            if normA = 0.0 || normB = 0.0 then 0.0
            else dot / (sqrt normA * sqrt normB)
