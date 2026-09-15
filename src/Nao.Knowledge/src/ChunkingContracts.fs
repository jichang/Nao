namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<Struct>]
type ChunkerContractVersion = private ChunkerContractVersion of int

[<RequireQualifiedAccess>]
module ChunkerContractVersion =
    let current = ChunkerContractVersion 1
    let value (ChunkerContractVersion value) = value

    let parse value =
        if value = 1 then
            current
        else
            invalidArg (nameof value) "Unsupported chunker contract version."

type ChunkerDescriptor =
    { ContractVersion: ChunkerContractVersion
      Component: ComponentVersion
      ConfigurationHash: ContentHash
      Deterministic: bool }

type TokenSpan =
    { CharacterOffset: int
      CharacterLength: int }

type TokenizerDescriptor =
    { Component: ComponentVersion
      ConfigurationHash: ContentHash
      Deterministic: bool }

type KnowledgeTokenizer =
    { Descriptor: TokenizerDescriptor
      Tokenize: string -> TokenSpan list }

type ChunkingRequest =
    { Authorization: AuthorizationScope
      DocumentVersionId: KnowledgeDocumentVersionId
      Units: ContentUnit list
      Lifecycle: KnowledgeLifecycle
      ChunkedAt: DateTimeOffset }

type ChunkSizeDistribution =
    { Count: int
      Minimum: int
      Maximum: int
      Mean: float
      P50: int
      P95: int }

type ChunkingMetrics =
    { InputUnits: int
      OutputChunks: int
      ParentChunks: int
      LeafChunks: int
      CharacterSizes: ChunkSizeDistribution
      TokenSizes: ChunkSizeDistribution option
      InputCharacters: int64
      TruncatedUnits: int
      TruncatedCharacters: int64
      TruncatedUnitRate: float
      TruncatedCharacterRate: float }

type ChunkingResult =
    { Chunks: KnowledgeChunk list
      Metrics: ChunkingMetrics }

type KnowledgeChunker =
    { Descriptor: ChunkerDescriptor
      ChunkAsync: ChunkingRequest -> CancellationToken -> Task<ChunkingResult> }
