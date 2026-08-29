namespace Nao.Providers

open System
open Nao.Agents

/// Factory for creating LLM providers
module ProviderFactory =

    let private optionalKey (key: string) =
        if String.IsNullOrWhiteSpace key then None else Some key

    let create (providerType: ProviderType) : ILlmProvider =
        match providerType with
        // These providers all speak the same OpenAI-compatible
        // /v1/chat/completions API, so they share one client.
        | OpenAI config ->
            new OpenAICompatibleProvider("OpenAI", config.BaseUrl, config.Model, optionalKey config.ApiKey, ?timeoutSeconds = config.TimeoutSeconds) :> ILlmProvider
        | DeepSeek config ->
            new OpenAICompatibleProvider("DeepSeek", config.BaseUrl, config.Model, optionalKey config.ApiKey, ?timeoutSeconds = config.TimeoutSeconds) :> ILlmProvider
        | Kimi config ->
            new OpenAICompatibleProvider("Kimi", config.BaseUrl, config.Model, optionalKey config.ApiKey, ?timeoutSeconds = config.TimeoutSeconds) :> ILlmProvider
        | Vllm config ->
            new OpenAICompatibleProvider("vLLM", config.BaseUrl, config.Model, config.ApiKey, ?timeoutSeconds = config.TimeoutSeconds) :> ILlmProvider
        | LlamaCpp config ->
            new OpenAICompatibleProvider("llama.cpp", config.BaseUrl, config.Model, None, ?timeoutSeconds = config.TimeoutSeconds) :> ILlmProvider
        | Ollama config ->
            new OllamaProvider(config) :> ILlmProvider
        | Anthropic config ->
            new AnthropicProvider(config) :> ILlmProvider
