namespace Nao.Providers

/// Configuration for Anthropic Claude
type AnthropicConfig =
    { ApiKey: string
      Model: string }

    static member Default =
        { ApiKey = ""
          Model = "claude-sonnet-4-20250514" }
