namespace Nao.Providers

/// Configuration for Ollama (local LLM server)
type OllamaConfig =
    { BaseUrl: string
      Model: string }

    static member Default =
        { BaseUrl = "http://localhost:11434"
          Model = "qwen2.5:3b" }
