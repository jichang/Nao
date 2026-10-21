namespace Nao.Agents

open System.Threading
open System.Threading.Tasks

/// Immutable functional capability for invoking a language model.
type LlmProvider =
    { Name: unit -> string
      CompleteAsync:
          CorrelationContext -> Conversation -> CompletionOptions -> CancellationToken -> Task<CompletionResult>
      StreamAsync:
          (CorrelationContext
              -> Conversation
              -> CompletionOptions
              -> (CompletionChunk -> unit)
              -> CancellationToken
              -> Task<CompletionResult>) option
      Dispose: unit -> unit }

[<RequireQualifiedAccess>]
module LlmProvider =
    /// Construct a non-streaming provider capability.
    let create name completeAsync =
        { Name = name
          CompleteAsync = completeAsync
          StreamAsync = None
          Dispose = ignore }

    /// Return the provider's current human-readable name.
    let name (provider: LlmProvider) = provider.Name()

    /// Request a buffered completion.
    let completeAsync correlation conversation options cancellationToken (provider: LlmProvider) =
        provider.CompleteAsync correlation conversation options cancellationToken

    /// Stream a completion when supported, otherwise emit one buffered chunk.
    let streamAsync (provider: LlmProvider) correlation conversation options onChunk cancellationToken =
        match provider.StreamAsync with
        | Some stream -> stream correlation conversation options onChunk cancellationToken
        | None ->
            task {
                let! result = provider.CompleteAsync correlation conversation options cancellationToken

                onChunk
                    { Delta = result.Content
                      Termination = Some(ProviderStreamTermination.Completed result.FinishReason)
                      Usage = result.Usage
                      ToolCalls = result.ToolCalls
                      StructuredOutputJson = result.StructuredOutputJson
                      Extensions = result.Extensions }

                return result
            }

    /// Release resources owned by the provider.
    let dispose (provider: LlmProvider) = provider.Dispose()
