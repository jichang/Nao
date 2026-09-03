namespace Nao.Providers

open System
open Nao.Agents

/// Factory for creating LLM providers
module ProviderFactory =

    let private optionalKey (key: string) =
        if String.IsNullOrWhiteSpace key then None else Some key

    let private openAi name baseUrl model apiKey timeout =
        OpenAICompatibleProvider.create name baseUrl model apiKey timeout

    let create (providerType: ProviderType) : LlmProvider =
        match providerType with
        // These providers all speak the same OpenAI-compatible
        // /v1/chat/completions API, so they share one client.
        | OpenAI config ->
            openAi "OpenAI" config.BaseUrl config.Model (optionalKey config.ApiKey) config.TimeoutSeconds
        | DeepSeek config ->
            openAi "DeepSeek" config.BaseUrl config.Model (optionalKey config.ApiKey) config.TimeoutSeconds
        | Kimi config ->
            openAi "Kimi" config.BaseUrl config.Model (optionalKey config.ApiKey) config.TimeoutSeconds
        | Vllm config ->
            openAi "vLLM" config.BaseUrl config.Model config.ApiKey config.TimeoutSeconds
        | LlamaCpp config ->
            openAi "llama.cpp" config.BaseUrl config.Model None config.TimeoutSeconds
        | Ollama config ->
            OllamaProvider.create config
        | Anthropic config ->
            AnthropicProvider.create config
