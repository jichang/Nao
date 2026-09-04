namespace Nao.Agents

/// Configuration for an LLM completion request
type CompletionOptions =
    {
        /// Sampling temperature (0.0 = deterministic, 1.0+ = creative). Default: 0.7
        Temperature: float
        /// Maximum number of tokens to generate. None means provider default.
        MaxTokens: int option
        /// Sequences that cause the model to stop generating further tokens
        StopSequences: string list
    }

    /// Sensible defaults: temperature 0.7, no token limit, no stop sequences
    static member Default =
        { Temperature = 0.7
          MaxTokens = None
          StopSequences = [] }
