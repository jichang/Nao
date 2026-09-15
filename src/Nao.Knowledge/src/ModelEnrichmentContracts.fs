namespace Nao.Knowledge

open System.Threading
open System.Threading.Tasks
open Nao.Agents

type ProposedEnrichmentEvidence =
    { ContentUnitId: ContentUnitId
      Quote: string }

type ProposedKnowledgeEnrichment =
    { Kind: KnowledgeEnrichmentKind
      Value: string
      Confidence: float
      Evidence: ProposedEnrichmentEvidence list }

type ModelEnrichmentInputUnit = { Id: ContentUnitId; Text: string }

type ModelEnrichmentInput =
    { ChunkId: KnowledgeChunkId
      Units: ModelEnrichmentInputUnit list }

type ModelEnrichmentOutput =
    { ChunkId: KnowledgeChunkId
      Enrichments: ProposedKnowledgeEnrichment list }

type KnowledgeEnrichmentModelDescriptor =
    { Model: ComponentVersion
      ConfigurationHash: ContentHash
      Deterministic: bool }

type KnowledgeEnrichmentModel =
    { Descriptor: KnowledgeEnrichmentModelDescriptor
      EnrichAsync:
          AuthorizationScope -> ModelEnrichmentInput list -> CancellationToken -> Task<ModelEnrichmentOutput list> }

type ModelAssistedEnricherConfig =
    { Model: KnowledgeEnrichmentModel
      MinimumConfidence: Confidence
      MaximumChunksPerRequest: int
      MaximumEnrichmentsPerChunk: int
      MaximumValueCharacters: int
      MaximumEvidenceItems: int }
