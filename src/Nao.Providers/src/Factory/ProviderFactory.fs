namespace Nao.Providers

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

type ProviderActivation =
    { Provider: LlmProvider
      CredentialReference: SecretReference option }

/// Factory for creating LLM providers
module ProviderFactory =

    let private openAi name baseUrl model apiKey timeout =
        OpenAICompatibleProvider.create name baseUrl model apiKey timeout

    let private requireCredential reference credential =
        match reference, credential with
        | None, None -> None
        | Some reference, Some value when SecretValue.reference value = reference -> Some value
        | Some reference, Some _ ->
            invalidArg
                (nameof credential)
                $"Resolved provider credential does not match reference '{SecretReference.value reference}'."
        | Some reference, None ->
            invalidOp $"Provider credential reference '{SecretReference.value reference}' has not been resolved."
        | None, Some _ ->
            invalidArg (nameof credential) "A provider credential was supplied without a configured secret reference."

    let internal createWithCredential credential (providerType: ProviderType) : LlmProvider =
        match providerType with
        // These providers all speak the same OpenAI-compatible
        // /v1/chat/completions API, so they share one client.
        | OpenAI config ->
            openAi
                "OpenAI"
                config.BaseUrl
                config.Model
                (requireCredential config.ApiKeyReference credential)
                config.TimeoutSeconds
        | DeepSeek config ->
            openAi
                "DeepSeek"
                config.BaseUrl
                config.Model
                (requireCredential config.ApiKeyReference credential)
                config.TimeoutSeconds
        | Kimi config ->
            openAi
                "Kimi"
                config.BaseUrl
                config.Model
                (requireCredential config.ApiKeyReference credential)
                config.TimeoutSeconds
        | Vllm config ->
            openAi
                "vLLM"
                config.BaseUrl
                config.Model
                (requireCredential config.ApiKeyReference credential)
                config.TimeoutSeconds
        | LlamaCpp config -> openAi "llama.cpp" config.BaseUrl config.Model None config.TimeoutSeconds
        | Ollama config ->
            requireCredential None credential |> ignore
            OllamaProvider.create config
        | Anthropic config -> AnthropicProvider.create config (requireCredential config.ApiKeyReference credential)

    let private redactConversation =
        List.map (fun (message: Message) ->
            { message with
                Content = SecretRedaction.redact message.Content })

    let private redactResult (result: CompletionResult) =
        { result with
            Content = SecretRedaction.redact result.Content
            FinishReason = SecretRedaction.redact result.FinishReason }

    let private redactingProvider (provider: LlmProvider) =
        let protect operation =
            task {
                try
                    let! result = operation ()
                    return redactResult result
                with
                | :? OperationCanceledException as error -> return raise error
                | error ->
                    return
                        error
                        |> PlatformFailure.fromException PlatformFailureBoundary.Provider None
                        |> PlatformFailure.raiseException
            }

        { Name = provider.Name
          CompleteAsync =
            fun correlation conversation options ->
                protect (fun () -> provider.CompleteAsync correlation (redactConversation conversation) options)
          StreamAsync =
            Some(fun correlation conversation options onChunk ->
                let streamRedactor = SecretRedaction.stream ()

                protect (fun () ->
                    task {
                        let! result =
                            LlmProvider.streamAsync
                                provider
                                correlation
                                (redactConversation conversation)
                                options
                                (fun chunk ->
                                    onChunk
                                        { chunk with
                                            Delta = streamRedactor.Push(chunk.Delta, chunk.FinishReason.IsSome)
                                            FinishReason = chunk.FinishReason |> Option.map SecretRedaction.redact })

                        let trailing = streamRedactor.Flush()

                        if trailing.Length > 0 then
                            onChunk (CompletionChunk.create trailing None None None)

                        return result
                    }))
          Dispose = provider.Dispose }

    let create providerType =
        createWithCredential None providerType |> redactingProvider

    let private credentialReference =
        function
        | OpenAI config -> config.ApiKeyReference
        | DeepSeek config -> config.ApiKeyReference
        | Kimi config -> config.ApiKeyReference
        | Anthropic config -> config.ApiKeyReference
        | Vllm config -> config.ApiKeyReference
        | LlamaCpp _
        | Ollama _ -> None

    let private providerName =
        function
        | OpenAI config -> $"OpenAI({config.Model})"
        | DeepSeek config -> $"DeepSeek({config.Model})"
        | Kimi config -> $"Kimi({config.Model})"
        | Anthropic config -> $"Anthropic({config.Model})"
        | Vllm config -> $"vLLM({config.Model})"
        | LlamaCpp config -> $"llama.cpp({config.Model})"
        | Ollama config -> $"Ollama({config.Model})"

    let internal operationScopedProviderWith createProvider providerName authorization reference secretProvider =
        let withProvider operation =
            SecretRedaction.runAsync (fun () ->
                task {
                    let! resolution =
                        SecretProvider.resolveAsync authorization reference CancellationToken.None secretProvider

                    let provider =
                        createProvider (SecretResolution.value resolution) |> redactingProvider

                    try
                        return! operation provider
                    finally
                        LlmProvider.dispose provider
                })

        { Name = fun () -> providerName
          CompleteAsync =
            fun correlation conversation options ->
                withProvider (fun provider -> provider.CompleteAsync correlation conversation options)
          StreamAsync =
            Some(fun correlation conversation options onChunk ->
                withProvider (fun provider ->
                    LlmProvider.streamAsync provider correlation conversation options onChunk))
          Dispose = ignore }

    let private operationScopedProvider authorization reference secretProvider providerType =
        operationScopedProviderWith
            (fun credential -> createWithCredential (Some credential) providerType)
            (providerName providerType)
            authorization
            reference
            secretProvider

    let activateAsync
        (secretProvider: SecretProvider option)
        authorization
        providerType
        (cancellationToken: CancellationToken)
        : Task<ProviderActivation> =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            match credentialReference providerType, secretProvider with
            | None, _ ->
                return
                    { Provider = create providerType
                      CredentialReference = None }
            | Some reference, None ->
                return
                    raise (
                        InvalidOperationException(
                            $"Provider credential reference '{SecretReference.value reference}' requires a secret provider."
                        )
                    )
            | Some reference, Some secretProvider ->
                return
                    { Provider = operationScopedProvider authorization reference secretProvider providerType
                      CredentialReference = Some reference }
        }
