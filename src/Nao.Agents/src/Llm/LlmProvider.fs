namespace Nao.Agents

open System.Threading.Tasks

/// Immutable functional capability for invoking a language model.
type LlmProvider =
    { Name: unit -> string
      CompleteAsync: Conversation -> CompletionOptions -> Task<CompletionResult>
      StreamAsync: (Conversation -> CompletionOptions -> (CompletionChunk -> unit) -> Task<CompletionResult>) option
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
    let completeAsync conversation options (provider: LlmProvider) =
        provider.CompleteAsync conversation options

    /// Stream a completion when supported, otherwise emit one buffered chunk.
    let streamAsync (provider: LlmProvider) conversation options onChunk =
        match provider.StreamAsync with
        | Some stream -> stream conversation options onChunk
        | None ->
            task {
                let! result = provider.CompleteAsync conversation options

                onChunk (
                    CompletionChunk.create result.Content (Some result.FinishReason) result.TokensUsed result.Usage
                )

                return result
            }

    /// Release resources owned by the provider.
    let dispose (provider: LlmProvider) = provider.Dispose()
