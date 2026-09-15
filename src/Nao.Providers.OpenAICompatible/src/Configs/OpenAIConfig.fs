namespace Nao.Providers

open Nao.Agents

/// Configuration for OpenAI-compatible providers
type OpenAIConfig =
    {
        ApiKeyReference: SecretReference option
        Model: string
        BaseUrl: string
        /// Request timeout in seconds. None uses the HttpClient default.
        TimeoutSeconds: int option
    }

    static member Default =
        { ApiKeyReference = None
          Model = "gpt-4"
          BaseUrl = "https://api.openai.com/v1/chat/completions"
          TimeoutSeconds = None }
