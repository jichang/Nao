namespace Nao.Providers

/// Configuration for vLLM-served models (OpenAI-compatible API)
type VllmConfig =
    { BaseUrl: string
      Model: string
      ApiKey: string option }

    static member Default =
        { BaseUrl = "http://localhost:8000/v1"
          Model = "default"
          ApiKey = None }
