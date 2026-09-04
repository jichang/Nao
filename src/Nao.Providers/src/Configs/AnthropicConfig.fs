namespace Nao.Providers

/// Configuration for Anthropic Claude
type AnthropicConfig =
    {
        ApiKey: string
        Model: string
        BaseUrl: string
        /// Request timeout in seconds. None uses the HttpClient default.
        TimeoutSeconds: int option
    }

    static member Default =
        { ApiKey = ""
          Model = "claude-sonnet-4-20250514"
          BaseUrl = "https://api.anthropic.com"
          TimeoutSeconds = None }
