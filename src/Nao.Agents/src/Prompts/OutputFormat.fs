namespace Nao.Agents

/// Output format the agent should produce.
/// Controls the formatting instruction appended to the system prompt.
type OutputFormat =
    /// No format constraint — agent responds in natural language
    | FreeText
    /// Output constrained by a schema description
    | Schema of description: string
