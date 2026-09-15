namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks

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

type ChunkingRequest =
    { DocumentVersionId: KnowledgeDocumentVersionId
      Units: ContentUnit list
      Lifecycle: KnowledgeLifecycle
      ChunkedAt: DateTimeOffset }

type ChunkingResult =
    { Chunks: KnowledgeChunk list
      InputUnits: int
      ParentChunks: int
      LeafChunks: int }

type KnowledgeChunker =
    { Descriptor: ChunkerDescriptor
      ChunkAsync: ChunkingRequest -> CancellationToken -> Task<ChunkingResult> }
