namespace Nao.Providers

open Nao.Agents

/// Configuration for DeepSeek's OpenAI-compatible API
type DeepSeekConfig =
    {
        ApiKeyReference: SecretReference option
        Model: string
        BaseUrl: string
        /// Request timeout in seconds. None uses the HttpClient default.
        TimeoutSeconds: int option
    }

    static member Default =
        { ApiKeyReference = None
          Model = "deepseek-chat"
          BaseUrl = "https://api.deepseek.com/v1/chat/completions"
          TimeoutSeconds = None }
