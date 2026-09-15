namespace Nao.Providers

open Nao.Agents

/// Configuration for vLLM-served models (OpenAI-compatible API)
type VllmConfig =
    {
        BaseUrl: string
        Model: string
        ApiKeyReference: SecretReference option
        /// Request timeout in seconds. None uses the HttpClient default.
        TimeoutSeconds: int option
    }

    static member Default =
        { BaseUrl = "http://localhost:8000/v1/chat/completions"
          Model = "default"
          ApiKeyReference = None
          TimeoutSeconds = None }
