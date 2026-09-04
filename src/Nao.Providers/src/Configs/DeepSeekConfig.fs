namespace Nao.Providers

/// Configuration for DeepSeek's OpenAI-compatible API
type DeepSeekConfig =
    {
        ApiKey: string
        Model: string
        BaseUrl: string
        /// Request timeout in seconds. None uses the HttpClient default.
        TimeoutSeconds: int option
    }

    static member Default =
        { ApiKey = ""
          Model = "deepseek-chat"
          BaseUrl = "https://api.deepseek.com/v1/chat/completions"
          TimeoutSeconds = None }
