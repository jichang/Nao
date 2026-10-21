namespace Nao.Agents.Tests

open System
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents
open Nao.Persistence

/// A mock LLM provider that returns predictable summaries
module MockSummarizerProvider =
    let create (observed: CorrelationContext option ref) =
        LlmProvider.create
            (fun () -> "MockSummarizer")
            (fun correlation (conversation: Conversation) (_options: CompletionOptions) _ ->
                observed.Value <- Some correlation

                let lastUserMsg =
                    conversation
                    |> List.tryFindBack (fun m -> m.Role = User)
                    |> Option.map (fun m -> m.Content)
                    |> Option.defaultValue ""
                // Count messages in the input being summarized
                let lineCount = lastUserMsg.Split('\n').Length
                let summary = sprintf "Summarized %d exchanges about various topics." lineCount
                Task.FromResult(CompletionResult.text summary ProviderFinishReason.Stop (ProviderUsage.aggregate 20)))

[<TestClass>]
type SummarizerTests() =

    let correlation = CorrelationContext.root ()
    let observed = ref None
    let provider = MockSummarizerProvider.create observed

    let makeConversation (n: int) =
        [ for i in 1..n do
              yield
                  { Role = User
                    Content = sprintf "Message %d from user" i }

              yield
                  { Role = Assistant
                    Content = sprintf "Response %d from assistant" i } ]

    [<TestMethod>]
    member _.ApplyAsync_DoesNothingWhenBelowThreshold() =
        let config =
            { SummarizationConfig.Default provider with
                Threshold = 20
                KeepRecent = 6 }

        let conversation = makeConversation 5 // 10 messages total
        let result = (Summarizer.applyAsync correlation config conversation).Result
        Assert.AreEqual(10, result.Length)

    [<TestMethod>]
    member _.ApplyAsync_SummarizesWhenAboveThreshold() =
        let config =
            { SummarizationConfig.Default provider with
                Threshold = 6
                KeepRecent = 4 }

        let conversation = makeConversation 5 // 10 messages total, > threshold of 6
        let result = (Summarizer.applyAsync correlation config conversation).Result
        // Should have: 1 summary message + 4 recent messages = 5
        Assert.AreEqual(5, result.Length)
        Assert.IsTrue(result.[0].Content.Contains("[Conversation Summary]"))
        Assert.AreEqual(System, result.[0].Role)

    [<TestMethod>]
    member _.ApplyAsync_SummaryContainsExchangeInfo() =
        let config =
            { SummarizationConfig.Default provider with
                Threshold = 4
                KeepRecent = 2 }

        let conversation = makeConversation 4 // 8 messages
        let result = (Summarizer.applyAsync correlation config conversation).Result
        // Summary should mention the summarized exchanges
        Assert.IsTrue(result.[0].Content.Contains("Summarized"))

    [<TestMethod>]
    member _.SummarizeAsync_ProducesSummaryMessage() =
        let messages = makeConversation 3
        observed.Value <- None

        let result =
            (Summarizer.summarizeAsync correlation provider CompletionOptions.Default messages).Result

        Assert.AreEqual(System, result.Role)
        Assert.IsTrue(result.Content.Contains("[Conversation Summary]"))
        Assert.AreEqual(Some correlation, observed.Value)

    [<TestMethod>]
    member _.ApplyAsync_KeepsRecentMessagesIntact() =
        let config =
            { SummarizationConfig.Default provider with
                Threshold = 4
                KeepRecent = 4 }

        let conversation = makeConversation 4 // 8 messages
        let result = (Summarizer.applyAsync correlation config conversation).Result
        // Last 4 messages should be preserved exactly
        let lastFour = result |> List.skip 1 // skip summary
        Assert.AreEqual("Message 3 from user", lastFour.[0].Content)
        Assert.AreEqual("Response 3 from assistant", lastFour.[1].Content)
        Assert.AreEqual("Message 4 from user", lastFour.[2].Content)
        Assert.AreEqual("Response 4 from assistant", lastFour.[3].Content)

[<TestClass>]
type MemoryConsolidationTests() =

    [<TestMethod>]
    member _.EveryConsolidationStrategyRemainsInsideAuthenticatedScope() =
        let scope tenant =
            let principal =
                SecurityPrincipal.createAuthenticated
                    (TenantId.parse tenant)
                    (UserId.parse "user")
                    (Some(ServiceId.parse "private-service"))
                    []
                    [ "private-role" ]
                    (Map.ofList [ "private-claim", "private-value" ])
                    AuthenticationStrength.MultiFactor

            AuthorizationScope.tryCreate
                principal
                None
                (WorkspaceId.parse "workspace")
                (Some(SessionId.parse "session"))
            |> Option.get

        let own, other = scope "tenant-a", scope "tenant-b"

        let provider =
            LlmProvider.create (fun () -> "scoped-summary") (fun _ messages _ _ ->
                let prompt = messages |> List.map _.Content |> String.concat "\n"

                for excluded in
                    [ "foreign"
                      "private-service"
                      "private-role"
                      "private-claim"
                      "private-value" ] do
                    Assert.IsFalse(prompt.Contains excluded, sprintf "Summary prompt leaked %s." excluded)

                Task.FromResult(CompletionResult.text "own summary" ProviderFinishReason.Stop ProviderUsage.None))

        let strategies =
            [ ConsolidationStrategy.Deduplicate
              ConsolidationStrategy.MergeSimilar 0.5
              ConsolidationStrategy.ImportanceDecay(0.01, TimeSpan.Zero)
              ConsolidationStrategy.Summarize(provider, CompletionOptions.Default)
              ConsolidationStrategy.Composite
                  [ ConsolidationStrategy.Deduplicate; ConsolidationStrategy.MergeSimilar 0.5 ] ]

        for strategy in strategies do
            let store = InMemoryStore.create ()

            for index in 1..10 do
                let entry =
                    { Key = string index
                      Value = "own fact"
                      Timestamp = DateTimeOffset.UtcNow.AddDays(-1)
                      Tags = [ "facts" ] }

                store.SaveAsync own entry |> _.Wait()
                store.SaveAsync other { entry with Value = "foreign fact" } |> _.Wait()

            let foreign = store.RecallAllAsync other |> _.Result

            let result =
                MemoryConsolidation.consolidateAsync (CorrelationContext.root ()) store own strategy
                |> _.Result

            Assert.AreEqual(10, result.TotalBefore)
            Assert.AreEqual(foreign, store.RecallAllAsync other |> _.Result)

    [<TestMethod>]
    member _.SummarizeUsesSuppliedCorrelation() =
        let correlation = CorrelationContext.root ()
        let observed = ref None
        let store = InMemoryStore.create ()

        let principal =
            SecurityPrincipal.create (TenantId.parse "tenant") (UserId.parse "user") []

        let owner =
            AuthorizationScope.tryCreate
                principal
                None
                (WorkspaceId.parse "workspace")
                (Some(SessionId.parse "consolidation"))
            |> Option.get

        for index in 1..10 do
            store.SaveAsync
                owner
                { Key = sprintf "fact-%d" index
                  Value = sprintf "value-%d" index
                  Timestamp = DateTimeOffset.UtcNow
                  Tags = [ "facts" ] }
            |> _.Wait()

        let provider =
            LlmProvider.create (fun () -> "capturing") (fun actual _conversation _options _ ->
                observed.Value <- Some actual
                Task.FromResult(CompletionResult.text "consolidated" ProviderFinishReason.Stop ProviderUsage.None))

        let strategy = ConsolidationStrategy.Summarize(provider, CompletionOptions.Default)

        let result =
            MemoryConsolidation.consolidateAsync correlation store owner strategy
            |> _.Result

        Assert.AreEqual(10, result.Summarized)
        Assert.AreEqual(Some correlation, observed.Value)
