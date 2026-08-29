namespace Nao.Providers

/// Identifies supported provider platforms
type ProviderType =
    | OpenAI of OpenAIConfig
    | DeepSeek of DeepSeekConfig
    | Kimi of KimiConfig
    | Anthropic of AnthropicConfig
    | Ollama of OllamaConfig
    | Vllm of VllmConfig
    | LlamaCpp of LlamaCppConfig
