namespace Nao.Providers

/// Configuration for OpenAI-compatible providers
type OpenAIConfig =
    { ApiKey: string
      Model: string
      BaseUrl: string }

    static member Default =
        { ApiKey = ""
          Model = "gpt-4"
          BaseUrl = "https://api.openai.com/v1" }

