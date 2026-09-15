namespace Nao.Providers

open Nao.Agents
open Nao.Knowledge

type OpenAICompatibleEmbeddingConfig =
    { Endpoint: string
      ApiKeyReference: SecretReference option
      Model: string
      Descriptor: EmbeddingCapabilityDescriptor
      CountTokens: string -> int
      CostPerMillionInputTokensUsd: decimal
      TimeoutSeconds: int option }
