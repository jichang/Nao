namespace Nao.Agents

open System.Threading.Tasks
open Nao.Agents

/// Configuration for conversation summarization
type SummarizationConfig =
    { /// Maximum messages before triggering summarization
      Threshold: int
      /// Number of recent messages to always keep unsummarized
      KeepRecent: int
      /// The LLM provider used to generate summaries
      Provider: ILlmProvider
      /// Completion options for the summarization call
      Options: CompletionOptions }

    static member Default provider =
        { Threshold = 20
          KeepRecent = 6
          Provider = provider
          Options = { CompletionOptions.Default with MaxTokens = Some 300 } }

module Summarizer =

    let private summaryPrompt =
        Prompt.render
            { Prompt.Empty with
                Objective = "Summarize the following conversation concisely."
                Constraints =
                    [ "Preserve key facts, decisions, and context needed to continue the conversation."
                      "Be brief." ] }

    /// Summarize a list of messages into a single condensed message
    let summarizeAsync (provider: ILlmProvider) (options: CompletionOptions) (messages: Conversation) : Task<Message> =
        task {
            let formatted =
                messages
                |> List.map (fun m ->
                    let role = match m.Role with System -> "System" | User -> "User" | Assistant -> "Assistant"
                    sprintf "%s: %s" role m.Content)
                |> String.concat "\n"

            let conversation = [
                { Role = System; Content = summaryPrompt }
                { Role = User; Content = formatted }
            ]

            let! result = provider.CompleteAsync conversation options
            return { Role = System; Content = sprintf "[Conversation Summary] %s" result.Content }
        }

    /// Apply summarization to a conversation if it exceeds the threshold.
    /// Returns the trimmed conversation with a summary message prepended.
    let applyAsync (config: SummarizationConfig) (conversation: Conversation) : Task<Conversation> =
        task {
            if conversation.Length <= config.Threshold then
                return conversation
            else
                let (toSummarize, recent) =
                    ConversationWindow.partitionForSummary config.KeepRecent conversation

                if toSummarize.IsEmpty then
                    return conversation
                else
                    let! summary = summarizeAsync config.Provider config.Options toSummarize
                    return summary :: recent
        }
