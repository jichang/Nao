namespace Nao.Providers

open System
open System.Threading.Tasks
open Nao.Agents

/// Factory for creating LLM providers
module ProviderFactory =

    let private optionalKey (key: string) =
        if String.IsNullOrWhiteSpace key then None else Some key

    let create (providerType: ProviderType) : ILlmProvider =
        match providerType with
        // OpenAI, vLLM and llama.cpp all speak the same OpenAI-compatible
        // /v1/chat/completions API, so they share one client.
        | OpenAI config ->
            new OpenAICompatibleProvider("OpenAI", config.BaseUrl, config.Model, optionalKey config.ApiKey) :> ILlmProvider
        | Vllm config ->
            new OpenAICompatibleProvider("vLLM", config.BaseUrl, config.Model, config.ApiKey) :> ILlmProvider
        | LlamaCpp config ->
            new OpenAICompatibleProvider("llama.cpp", config.BaseUrl, config.Model, None) :> ILlmProvider
        | Ollama config ->
            new OllamaProvider(config) :> ILlmProvider
        | Anthropic _config ->
            { new ILlmProvider with
                member _.Name = "Anthropic"
                member _.CompleteAsync _conversation _options =
                    Task.FromResult { Content = ""; FinishReason = "stop"; TokensUsed = None } }
