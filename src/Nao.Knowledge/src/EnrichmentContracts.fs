namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

type ChunkEnricherDescriptor =
    { Component: ComponentVersion
      ConfigurationHash: ContentHash
      Deterministic: bool }

type ChunkEnrichmentRequest =
    { Authorization: AuthorizationScope
      DocumentVersionId: KnowledgeDocumentVersionId
      Units: ContentUnit list
      Chunks: KnowledgeChunk list
      EnrichedAt: DateTimeOffset }

type ChunkEnrichmentResult =
    { Chunks: KnowledgeChunk list
      EnrichedChunks: int }

type KnowledgeChunkEnricher =
    { Descriptor: ChunkEnricherDescriptor
      EnrichAsync: ChunkEnrichmentRequest -> CancellationToken -> Task<ChunkEnrichmentResult> }
