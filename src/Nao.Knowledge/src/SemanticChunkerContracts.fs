namespace Nao.Knowledge

open System.Threading
open System.Threading.Tasks
open Nao.Agents

type SemanticBoundaryModelDescriptor =
    { Model: ComponentVersion
      Dimensions: int
      ConfigurationHash: ContentHash
      Deterministic: bool }

type SemanticBoundaryModel =
    { Descriptor: SemanticBoundaryModelDescriptor
      EmbedAsync: AuthorizationScope -> string list -> CancellationToken -> Task<float array list> }

type SemanticChunkerConfig =
    { BoundarySimilarityThreshold: float
      MaximumUnitsPerChunk: int
      Model: SemanticBoundaryModel }
