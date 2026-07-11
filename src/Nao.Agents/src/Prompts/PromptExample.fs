namespace Nao.Agents

/// A single few-shot example demonstrating expected input/output behavior.
/// Used within a Prompt to guide the LLM's responses.
type PromptExample =
    { /// The example user input
      Input: string
      /// The expected agent output for the given input
      Output: string
      /// Optional explanation of why this output is correct
      Explanation: string option }
