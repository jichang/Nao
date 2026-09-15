namespace Nao.Providers

open Nao.Knowledge

type OllamaEmbeddingConfig =
    { BaseUrl: string
      Model: string
      Descriptor: EmbeddingCapabilityDescriptor
      CountTokens: string -> int
      TimeoutSeconds: int option }
