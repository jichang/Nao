namespace Nao.Providers

open System

/// Identifies supported provider platforms
type ProviderType =
    | OpenAI of OpenAIConfig
    | DeepSeek of DeepSeekConfig
    | Kimi of KimiConfig
    | Anthropic of AnthropicConfig
    | Ollama of OllamaConfig
    | Vllm of VllmConfig
    | LlamaCpp of LlamaCppConfig

[<RequireQualifiedAccess>]
module ProviderType =
    let private validateCommon baseUrl model timeoutSeconds =
        match Uri.TryCreate(baseUrl, UriKind.Absolute) with
        | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps -> ()
        | _ -> invalidArg (nameof baseUrl) "Provider URL must be an absolute HTTP or HTTPS endpoint."

        if String.IsNullOrWhiteSpace model || model <> model.Trim() then
            invalidArg (nameof model) "Provider model identity must be canonical and non-blank."

        timeoutSeconds
        |> Option.iter (fun seconds ->
            if seconds <= 0 then
                invalidArg (nameof timeoutSeconds) "Provider timeout must be positive when configured.")

    let validate providerType =
        match providerType with
        | OpenAI value -> validateCommon value.BaseUrl value.Model value.TimeoutSeconds
        | DeepSeek value -> validateCommon value.BaseUrl value.Model value.TimeoutSeconds
        | Kimi value -> validateCommon value.BaseUrl value.Model value.TimeoutSeconds
        | Anthropic value -> validateCommon value.BaseUrl value.Model value.TimeoutSeconds
        | Ollama value ->
            validateCommon value.BaseUrl value.Model value.TimeoutSeconds

            value.ReasoningEffort
            |> Option.iter (fun effort ->
                if String.IsNullOrWhiteSpace effort || effort <> effort.Trim() then
                    invalidArg (nameof value.ReasoningEffort) "Reasoning effort must be canonical and non-blank.")
        | Vllm value -> validateCommon value.BaseUrl value.Model value.TimeoutSeconds
        | LlamaCpp value ->
            validateCommon value.BaseUrl value.Model value.TimeoutSeconds

            value.NPredict
            |> Option.iter (fun tokens ->
                if tokens <= 0 then
                    invalidArg (nameof value.NPredict) "llama.cpp prediction limit must be positive when configured.")

    let modelId =
        function
        | OpenAI value -> value.Model
        | DeepSeek value -> value.Model
        | Kimi value -> value.Model
        | Anthropic value -> value.Model
        | Ollama value -> value.Model
        | Vllm value -> value.Model
        | LlamaCpp value -> value.Model
