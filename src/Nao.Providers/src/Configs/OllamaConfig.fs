namespace Nao.Providers

/// Configuration for Ollama (local LLM server)
type OllamaConfig =
    { BaseUrl: string
      Model: string
      /// OpenAI-compatible reasoning effort. Use "none" for deterministic tool protocols.
      ReasoningEffort: string option
      /// Request timeout in seconds. None uses the HttpClient default.
      TimeoutSeconds: int option }

    static member Default =
        { BaseUrl = "http://localhost:11434"
          Model = "qwen2.5:3b"
          ReasoningEffort = Some "none"
          TimeoutSeconds = None }
