namespace Nao.Agents.Tests

open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Agents

/// A mock LLM provider that returns predictable summaries
type MockSummarizerProvider() =
    interface ILlmProvider with
        member _.Name = "MockSummarizer"
        member _.CompleteAsync (conversation: Conversation) (_options: CompletionOptions) =
            let lastUserMsg =
                conversation
                |> List.tryFindBack (fun m -> m.Role = User)
                |> Option.map (fun m -> m.Content)
                |> Option.defaultValue ""
            // Count messages in the input being summarized
            let lineCount = lastUserMsg.Split('\n').Length
            let summary = sprintf "Summarized %d exchanges about various topics." lineCount
            Task.FromResult(CompletionResult.create summary "stop" (Some 20) None)

[<TestClass>]
type SummarizerTests() =

    let provider = MockSummarizerProvider() :> ILlmProvider

    let makeConversation (n: int) =
        [ for i in 1 .. n do
            yield { Role = User; Content = sprintf "Message %d from user" i }
            yield { Role = Assistant; Content = sprintf "Response %d from assistant" i } ]

    [<TestMethod>]
    member _.ApplyAsync_DoesNothingWhenBelowThreshold() =
        let config = { SummarizationConfig.Default provider with Threshold = 20; KeepRecent = 6 }
        let conversation = makeConversation 5 // 10 messages total
        let result = (Summarizer.applyAsync config conversation).Result
        Assert.AreEqual(10, result.Length)

    [<TestMethod>]
    member _.ApplyAsync_SummarizesWhenAboveThreshold() =
        let config = { SummarizationConfig.Default provider with Threshold = 6; KeepRecent = 4 }
        let conversation = makeConversation 5 // 10 messages total, > threshold of 6
        let result = (Summarizer.applyAsync config conversation).Result
        // Should have: 1 summary message + 4 recent messages = 5
        Assert.AreEqual(5, result.Length)
        Assert.IsTrue(result.[0].Content.Contains("[Conversation Summary]"))
        Assert.AreEqual(System, result.[0].Role)

    [<TestMethod>]
    member _.ApplyAsync_SummaryContainsExchangeInfo() =
        let config = { SummarizationConfig.Default provider with Threshold = 4; KeepRecent = 2 }
        let conversation = makeConversation 4 // 8 messages
        let result = (Summarizer.applyAsync config conversation).Result
        // Summary should mention the summarized exchanges
        Assert.IsTrue(result.[0].Content.Contains("Summarized"))

    [<TestMethod>]
    member _.SummarizeAsync_ProducesSummaryMessage() =
        let messages = makeConversation 3
        let result = (Summarizer.summarizeAsync provider CompletionOptions.Default messages).Result
        Assert.AreEqual(System, result.Role)
        Assert.IsTrue(result.Content.Contains("[Conversation Summary]"))

    [<TestMethod>]
    member _.ApplyAsync_KeepsRecentMessagesIntact() =
        let config = { SummarizationConfig.Default provider with Threshold = 4; KeepRecent = 4 }
        let conversation = makeConversation 4 // 8 messages
        let result = (Summarizer.applyAsync config conversation).Result
        // Last 4 messages should be preserved exactly
        let lastFour = result |> List.skip 1 // skip summary
        Assert.AreEqual("Message 3 from user", lastFour.[0].Content)
        Assert.AreEqual("Response 3 from assistant", lastFour.[1].Content)
        Assert.AreEqual("Message 4 from user", lastFour.[2].Content)
        Assert.AreEqual("Response 4 from assistant", lastFour.[3].Content)
