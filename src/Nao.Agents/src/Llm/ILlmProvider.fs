namespace Nao.Agents

open System.Threading.Tasks

/// Abstract interface for LLM providers.
/// Implementations wrap specific backends (Ollama, OpenAI, Anthropic, etc.)
type ILlmProvider =
    /// Send a conversation to the LLM and return the completion result
    abstract member CompleteAsync: Conversation -> CompletionOptions -> Task<CompletionResult>
    /// Human-readable name identifying this provider instance (e.g. "ollama", "openai")
    abstract member Name: string

/// Optional capability a provider implements when it can return the completion incrementally
/// (token by token) instead of buffering the whole response. `onChunk` is invoked on the
/// calling task for each delta as it arrives; the returned `CompletionResult` is the fully
/// aggregated completion (identical in shape to `CompleteAsync`), so a caller that streams to a
/// UI and also needs the final text requires no extra bookkeeping.
type IStreamingLlmProvider =
    abstract member StreamAsync: Conversation -> CompletionOptions -> (CompletionChunk -> unit) -> Task<CompletionResult>

/// Helpers for invoking providers.
[<RequireQualifiedAccess>]
module LlmProvider =
    /// Stream a completion, delivering each delta to `onChunk`. When the provider implements
    /// `IStreamingLlmProvider` the real token stream is used; otherwise this falls back to a
    /// single buffered `CompleteAsync` call and emits the whole response as one chunk. Callers
    /// can therefore always use the streaming path regardless of the provider's capability.
    let streamAsync (provider: ILlmProvider) (conversation: Conversation) (options: CompletionOptions) (onChunk: CompletionChunk -> unit) : Task<CompletionResult> =
        match provider with
        | :? IStreamingLlmProvider as streaming -> streaming.StreamAsync conversation options onChunk
        | _ ->
            task {
                let! result = provider.CompleteAsync conversation options
                onChunk { Delta = result.Content; FinishReason = Some result.FinishReason; TokensUsed = result.TokensUsed; Usage = result.Usage }
                return result
            }
