namespace Nao.Agents

open Nao.Agents

/// Strategy for managing conversation history length
type WindowStrategy =
    /// Keep only the last N messages
    | LastN of int
    /// Keep messages that fit within an estimated token budget
    | TokenBudget of maxTokens: int
    /// Summarize messages older than the threshold count, keeping recent ones
    | SummarizeAfter of threshold: int

module ConversationWindow =

    /// Estimate token count for a message (approximation: ~4 chars per token)
    let estimateTokens (msg: Message) = (msg.Content.Length + 3) / 4

    /// Apply LastN strategy: keep the last N messages
    let applyLastN (n: int) (conversation: Conversation) : Conversation =
        if conversation.Length <= n then
            conversation
        else
            conversation |> List.skip (conversation.Length - n)

    /// Apply TokenBudget strategy: keep as many recent messages as fit within budget
    let applyTokenBudget (maxTokens: int) (conversation: Conversation) : Conversation =
        let reversed = conversation |> List.rev
        let mutable budget = maxTokens
        let mutable result = []

        for msg in reversed do
            let tokens = estimateTokens msg

            if budget >= tokens then
                budget <- budget - tokens
                result <- msg :: result

        result

    /// Mark messages that should be summarized (those beyond the threshold)
    let partitionForSummary (threshold: int) (conversation: Conversation) : Conversation * Conversation =
        if conversation.Length <= threshold then
            ([], conversation)
        else
            let splitAt = conversation.Length - threshold
            let toSummarize = conversation |> List.take splitAt
            let toKeep = conversation |> List.skip splitAt
            (toSummarize, toKeep)

    /// Apply a window strategy to a conversation
    let apply (strategy: WindowStrategy) (conversation: Conversation) : Conversation =
        match strategy with
        | LastN n -> applyLastN n conversation
        | TokenBudget maxTokens -> applyTokenBudget maxTokens conversation
        | SummarizeAfter threshold ->
            // Without a summarizer, just keep the recent messages
            let (_, recent) = partitionForSummary threshold conversation
            recent
