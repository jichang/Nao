namespace Nao.Providers

open Nao.Agents

/// Configuration for Anthropic Claude
type AnthropicConfig =
    {
        ApiKeyReference: SecretReference option
        Model: string
        BaseUrl: string
        /// Request timeout in seconds. None uses the HttpClient default.
        TimeoutSeconds: int option
    }

    static member Default =
        { ApiKeyReference = None
          Model = "claude-sonnet-4-20250514"
          BaseUrl = "https://api.anthropic.com"
          TimeoutSeconds = None }
