namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
type ContentDuplicateKind =
    | Exact
    | Near

type NearDuplicateDetectorDescriptor =
    { Component: ComponentVersion
      ConfigurationHash: ContentHash
      Deterministic: bool }

type NearDuplicateDetection =
    { SchemaVersion: KnowledgeSchemaVersion
      Id: NearDuplicateId
      CanonicalChunkId: KnowledgeChunkId
      DuplicateChunkId: KnowledgeChunkId
      Kind: ContentDuplicateKind
      Similarity: Confidence
      Detector: ComponentVersion
      DetectorConfigurationHash: ContentHash
      DetectedAt: DateTimeOffset }

type NearDuplicateRequest =
    { Authorization: AuthorizationScope
      Chunks: KnowledgeChunk list
      DetectedAt: DateTimeOffset }

type NearDuplicateResult =
    { Duplicates: NearDuplicateDetection list
      AnalyzedChunks: int
      CandidatePairs: int }

type NearDuplicateDetector =
    { Descriptor: NearDuplicateDetectorDescriptor
      DetectAsync: NearDuplicateRequest -> CancellationToken -> Task<NearDuplicateResult> }

type ShingleNearDuplicateDetectorConfig =
    { Tokenizer: KnowledgeTokenizer
      ShingleTokens: int
      SimilarityThreshold: Confidence
      MaximumChunks: int
      MaximumCandidatePairs: int
      MaximumDetectedPairs: int }
