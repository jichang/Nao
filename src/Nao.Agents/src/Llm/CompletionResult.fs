namespace Nao.Agents

/// The result of an LLM completion
type CompletionResult =
    { /// The generated text content
      Content: string
      /// Why generation stopped (e.g. "stop", "length", "tool_call")
      FinishReason: string
      /// Number of tokens consumed (prompt + completion), if reported by the provider
      TokensUsed: int option }

/// One incremental piece of a streamed completion. A provider that supports streaming emits a
/// sequence of these as the model generates: non-terminal chunks carry a text `Delta`, and the
/// terminal chunk reports how generation finished plus token usage when the backend provides it.
type CompletionChunk =
    { /// Text produced since the previous chunk ("" for a terminal/usage-only chunk).
      Delta: string
      /// Why generation stopped; set only on the terminal chunk (e.g. "stop", "length").
      FinishReason: string option
      /// Total tokens used; set only on the terminal chunk when the provider reports it.
      TokensUsed: int option }
