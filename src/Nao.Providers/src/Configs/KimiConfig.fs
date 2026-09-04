namespace Nao.Providers

/// Configuration for Kimi's OpenAI-compatible API
type KimiConfig =
    {
        ApiKey: string
        Model: string
        BaseUrl: string
        /// Request timeout in seconds. None uses the HttpClient default.
        TimeoutSeconds: int option
    }

    static member Default =
        { ApiKey = ""
          Model = "kimi-k2.5"
          BaseUrl = "https://api.moonshot.ai/v1/chat/completions"
          TimeoutSeconds = None }
