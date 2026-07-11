namespace Nao.Agents

/// Output format the agent should produce.
/// Controls the formatting instruction appended to the system prompt.
type OutputFormat =
    /// No format constraint — agent responds in natural language
    | FreeText
    /// JSON output, optionally constrained to a schema
    | Json of schema: string option
    /// Markdown-formatted output
    | Markdown
    /// Custom format described by a freeform instruction
    | Custom of formatInstruction: string
