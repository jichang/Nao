namespace Nao.Agents

open System
open System.Threading.Tasks
open Nao.Agents

/// Strategy for compacting context when it exceeds budget
[<RequireQualifiedAccess>]
type CompactionStrategy =
    /// Drop oldest messages (simple truncation)
    | DropOldest
    /// Summarize old messages using LLM
    | Summarize of provider: LlmProvider * options: CompletionOptions
    /// Keep only messages matching a relevance filter
    | RelevanceFilter of scorer: (Message -> float) * threshold: float
    /// Hierarchical: summarize in chunks, then summarize summaries
    | Hierarchical of chunkSize: int * provider: LlmProvider * options: CompletionOptions
    /// Composite: apply multiple strategies in sequence
    | Composite of strategies: CompactionStrategy list

/// Result of a context compaction operation
type CompactionResult =
    {
        /// The compacted conversation
        Compacted: Conversation
        /// Number of messages removed
        MessagesRemoved: int
        /// Number of tokens saved (estimated)
        TokensSaved: int
        /// Summary text if summarization was used
        Summary: string option
    }

/// Advanced context management beyond simple windowing
module ContextCompaction =

    let private compactionPrompt =
        Prompt.render
            { Prompt.Empty with
                Objective = "Summarize the following conversation concisely."
                Constraints = [ "Preserve key facts, decisions, and action items." ] }

    /// Estimate token count for a message (rough heuristic: ~4 chars per token)
    let estimateTokens (msg: Message) : int = msg.Content.Length / 4

    /// Estimate total tokens in a conversation
    let estimateConversationTokens (conversation: Conversation) : int =
        conversation |> List.sumBy estimateTokens

    /// Drop oldest messages to fit within token budget
    let dropOldest (tokenBudget: int) (conversation: Conversation) : CompactionResult =
        let totalTokens = estimateConversationTokens conversation

        if totalTokens <= tokenBudget then
            { Compacted = conversation
              MessagesRemoved = 0
              TokensSaved = 0
              Summary = None }
        else
            // Keep messages from the end until we exceed the budget
            let mutable runningTokens = 0

            let kept =
                conversation
                |> List.rev
                |> List.takeWhile (fun msg ->
                    let msgTokens = estimateTokens msg

                    if runningTokens + msgTokens <= tokenBudget then
                        runningTokens <- runningTokens + msgTokens
                        true
                    else
                        false)
                |> List.rev

            { Compacted = kept
              MessagesRemoved = conversation.Length - kept.Length
              TokensSaved = totalTokens - runningTokens
              Summary = None }

    /// Summarize a chunk of messages using LLM
    let summarizeChunkAsync
        (correlation: CorrelationContext)
        (provider: LlmProvider)
        (options: CompletionOptions)
        (messages: Message list)
        : Task<string> =
        task {
            let content =
                messages
                |> List.map (fun m -> sprintf "[%A]: %s" m.Role m.Content)
                |> String.concat "\n"

            let prompt =
                [ { Role = System
                    Content = compactionPrompt }
                  { Role = User; Content = content } ]

            let! result = provider.CompleteAsync correlation prompt options
            return result.Content
        }

    /// Apply hierarchical summarization: chunk -> summarize -> combine
    let hierarchicalCompactAsync
        (correlation: CorrelationContext)
        (chunkSize: int)
        (provider: LlmProvider)
        (options: CompletionOptions)
        (tokenBudget: int)
        (conversation: Conversation)
        : Task<CompactionResult> =
        task {
            if estimateConversationTokens conversation <= tokenBudget then
                return
                    { Compacted = conversation
                      MessagesRemoved = 0
                      TokensSaved = 0
                      Summary = None }
            else
                // Split into chunks and summarize each
                let chunks = conversation |> List.chunkBySize chunkSize

                // Keep the last chunk intact, summarize the rest
                let toSummarize = chunks |> List.take (chunks.Length - 1) |> List.concat
                let toKeep = chunks |> List.last

                let! summary = summarizeChunkAsync correlation provider options toSummarize

                let summaryMsg =
                    { Role = Assistant
                      Content = sprintf "[Previous conversation summary]: %s" summary }

                let compacted = summaryMsg :: toKeep

                return
                    { Compacted = compacted
                      MessagesRemoved = toSummarize.Length
                      TokensSaved = estimateConversationTokens conversation - estimateConversationTokens compacted
                      Summary = Some summary }
        }

    /// Apply a compaction strategy to a conversation
    let rec applyAsync
        (correlation: CorrelationContext)
        (strategy: CompactionStrategy)
        (tokenBudget: int)
        (conversation: Conversation)
        : Task<CompactionResult> =
        task {
            match strategy with
            | CompactionStrategy.DropOldest -> return dropOldest tokenBudget conversation

            | CompactionStrategy.Summarize(provider, options) ->
                if estimateConversationTokens conversation <= tokenBudget then
                    return
                        { Compacted = conversation
                          MessagesRemoved = 0
                          TokensSaved = 0
                          Summary = None }
                else
                    // Keep recent messages, summarize the rest
                    let keepCount = min 5 conversation.Length
                    let toSummarize = conversation |> List.take (conversation.Length - keepCount)
                    let toKeep = conversation |> List.skip (conversation.Length - keepCount)
                    let! summary = summarizeChunkAsync correlation provider options toSummarize

                    let summaryMsg =
                        { Role = Assistant
                          Content = sprintf "[Summary]: %s" summary }

                    let compacted = summaryMsg :: toKeep

                    return
                        { Compacted = compacted
                          MessagesRemoved = toSummarize.Length
                          TokensSaved = estimateConversationTokens conversation - estimateConversationTokens compacted
                          Summary = Some summary }

            | CompactionStrategy.RelevanceFilter(scorer, threshold) ->
                let kept = conversation |> List.filter (fun msg -> scorer msg >= threshold)

                return
                    { Compacted = kept
                      MessagesRemoved = conversation.Length - kept.Length
                      TokensSaved = estimateConversationTokens conversation - estimateConversationTokens kept
                      Summary = None }

            | CompactionStrategy.Hierarchical(chunkSize, provider, options) ->
                return! hierarchicalCompactAsync correlation chunkSize provider options tokenBudget conversation

            | CompactionStrategy.Composite strategies ->
                let mutable current = conversation
                let mutable totalRemoved = 0
                let mutable totalSaved = 0
                let mutable lastSummary = None

                for strat in strategies do
                    let! result = applyAsync correlation strat tokenBudget current
                    current <- result.Compacted
                    totalRemoved <- totalRemoved + result.MessagesRemoved
                    totalSaved <- totalSaved + result.TokensSaved

                    if result.Summary.IsSome then
                        lastSummary <- result.Summary

                return
                    { Compacted = current
                      MessagesRemoved = totalRemoved
                      TokensSaved = totalSaved
                      Summary = lastSummary }
        }
