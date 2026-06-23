namespace Nao.Assistant

open System
open Nao.Providers

/// Catalogue of the LLM providers the app actually supports, plus a handful of common
/// open-source model suggestions. This is the single source of truth shared by the
/// settings dropdown (which provider options to offer), the "setup on change" behaviour
/// (the default endpoint to pre-fill) and the server (mapping the stored settings onto a
/// concrete `Nao.Providers.ProviderType`). Keeping it in one place means a new provider is
/// added once rather than in three disconnected spots.
[<RequireQualifiedAccess>]
module ProviderCatalog =

    /// A user-selectable provider: the stored id, its display label, and the endpoint to
    /// auto-fill when the user picks it.
    type Provider =
        { Id: string
          Label: string
          DefaultEndpoint: string }

    /// The supported providers, in the order shown in the dropdown. All four speak the
    /// OpenAI-compatible chat API, so each maps onto a working client.
    let supported : Provider list =
        [ { Id = "Ollama"; Label = "Ollama"; DefaultEndpoint = "http://localhost:11434" }
          { Id = "Vllm"; Label = "vLLM"; DefaultEndpoint = "http://localhost:8000/v1" }
          { Id = "LlamaCpp"; Label = "llama.cpp"; DefaultEndpoint = "http://localhost:8080" }
          { Id = "OpenAI"; Label = "OpenAI"; DefaultEndpoint = "https://api.openai.com/v1" } ]

    /// Display labels for the provider dropdown.
    let labels : string list = supported |> List.map (fun p -> p.Label)

    /// Common open-source models for the local providers (Ollama / vLLM / llama.cpp).
    /// Names follow Ollama-style tags.
    let commonModels : string list =
        [ "deepseek-r1"
          "deepseek-v3"
          "llama3.3"
          "llama3.2"
          "llama3.1"
          "qwen2.5"
          "qwen2.5-coder"
          "qwq"
          "mistral"
          "mixtral"
          "gemma2"
          "phi4" ]

    /// OpenAI's hosted models — different namespace from the open-source tags above.
    let private openAiModels : string list =
        [ "gpt-4o"
          "gpt-4o-mini"
          "gpt-4-turbo"
          "gpt-4"
          "o1"
          "o1-mini"
          "o3-mini" ]

    /// The models to offer in the dropdown for a given provider. OpenAI gets its hosted
    /// model names; every other (OpenAI-compatible) provider gets the open-source list.
    let modelsFor (providerId: string) : string list =
        match providerId.ToLowerInvariant() with
        | "openai" -> openAiModels
        | _ -> commonModels

    /// A sensible default model for a provider — the first in its model list. Used when
    /// switching providers so the model selection is never left empty/mismatched.
    let defaultModelFor (providerId: string) : string =
        modelsFor providerId |> List.tryHead |> Option.defaultValue ""

    let private byId (id: string) : Provider option =
        supported |> List.tryFind (fun p -> p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))

    let private fallback = List.head supported

    /// The display label for a stored provider id (defaults to the first provider).
    let labelFor (id: string) : string =
        byId id |> Option.map (fun p -> p.Label) |> Option.defaultValue fallback.Label

    /// The stored provider id for a display label (defaults to the first provider).
    let idForLabel (label: string) : string =
        supported
        |> List.tryFind (fun p -> p.Label = label)
        |> Option.map (fun p -> p.Id)
        |> Option.defaultValue fallback.Id

    /// The endpoint to pre-fill when a provider is selected.
    let defaultEndpointFor (id: string) : string =
        byId id |> Option.map (fun p -> p.DefaultEndpoint) |> Option.defaultValue fallback.DefaultEndpoint

    /// Map the stored provider settings onto a concrete provider, falling back to each
    /// provider's defaults for any blank endpoint/model so the server always gets a usable
    /// configuration.
    let toProviderType (settings: ProviderSettings) : ProviderType =
        let blank = String.IsNullOrWhiteSpace
        let endpoint =
            if blank settings.Endpoint then defaultEndpointFor settings.ProviderType else settings.Endpoint
        let modelOr (fallbackModel: string) =
            if blank settings.Model then fallbackModel else settings.Model
        match settings.ProviderType.ToLowerInvariant() with
        | "openai" ->
            OpenAI { OpenAIConfig.Default with BaseUrl = endpoint; Model = modelOr "gpt-4" }
        | "vllm" ->
            Vllm { VllmConfig.Default with BaseUrl = endpoint; Model = modelOr "default" }
        | "llamacpp" | "llama.cpp" ->
            LlamaCpp { LlamaCppConfig.Default with BaseUrl = endpoint; Model = modelOr "default" }
        | _ ->
            Ollama { OllamaConfig.Default with BaseUrl = endpoint; Model = modelOr "llama3.2" }
