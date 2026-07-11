namespace Nao.Providers

/// Identifies supported provider platforms
type ProviderType =
    | OpenAI of OpenAIConfig
    | Anthropic of AnthropicConfig
    | Ollama of OllamaConfig
    | Vllm of VllmConfig
    | LlamaCpp of LlamaCppConfig
