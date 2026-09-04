namespace Nao.Providers

/// Configuration for OpenAI-compatible providers
type OpenAIConfig =
    {
        ApiKey: string
        Model: string
        BaseUrl: string
        /// Request timeout in seconds. None uses the HttpClient default.
        TimeoutSeconds: int option
    }

    static member Default =
        { ApiKey = ""
          Model = "gpt-4"
          BaseUrl = "https://api.openai.com/v1/chat/completions"
          TimeoutSeconds = None }
