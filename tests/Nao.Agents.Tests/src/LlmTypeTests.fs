namespace Nao.Core.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type RoleTests() =

    [<TestMethod>]
    member _.RoleHasSystemCase() =
        let role = System
        Assert.AreEqual(System, role)

    [<TestMethod>]
    member _.RoleHasUserCase() =
        let role = User
        Assert.AreEqual(User, role)

    [<TestMethod>]
    member _.RoleHasAssistantCase() =
        let role = Assistant
        Assert.AreEqual(Assistant, role)

[<TestClass>]
type MessageTests() =

    [<TestMethod>]
    member _.CanCreateMessage() =
        let msg = { Role = User; Content = "hello" }
        Assert.AreEqual(User, msg.Role)
        Assert.AreEqual("hello", msg.Content)

    [<TestMethod>]
    member _.ConversationIsMessageList() =
        let conv: Conversation =
            [ { Role = System
                Content = "You are helpful" }
              { Role = User; Content = "Hi" }
              { Role = Assistant; Content = "Hello!" } ]

        Assert.AreEqual(3, conv.Length)

[<TestClass>]
type CompletionOptionsTests() =

    [<TestMethod>]
    member _.DefaultHasExpectedValues() =
        let opts = CompletionOptions.Default
        Assert.AreEqual(0.7, opts.Temperature)
        Assert.AreEqual(None, opts.MaxTokens)
        Assert.AreEqual([], opts.StopSequences)

    [<TestMethod>]
    member _.CanCustomizeOptions() =
        let opts =
            { Temperature = 0.0
              MaxTokens = Some 100
              StopSequences = [ "STOP" ] }

        Assert.AreEqual(0.0, opts.Temperature)
        Assert.AreEqual(Some 100, opts.MaxTokens)
        Assert.AreEqual(1, opts.StopSequences.Length)

[<TestClass>]
type CompletionResultTests() =

    [<TestMethod>]
    member _.CanCreateResult() =
        let result =
            CompletionResult.text "answer" ProviderFinishReason.Stop (ProviderUsage.full 30 12)

        Assert.AreEqual("answer", result.Content)
        Assert.AreEqual(ProviderFinishReason.Stop, result.FinishReason)
        Assert.AreEqual(ProviderUsage.Full(30, 12), result.Usage)

    [<TestMethod>]
    member _.``Preserves normalized tool calls structured output and extensions``() =
        let toolCall =
            { Id = "call-1"
              Name = "search"
              ArgumentsJson = """{"query":"Nao"}""" }

        let result =
            { Content = ""
              FinishReason = ProviderFinishReason.ToolCalls
              Usage = ProviderUsage.Partial(Some 7, None)
              ToolCalls = [ toolCall ]
              StructuredOutputJson = Some """{"answer":"Nao"}"""
              Extensions = Map [ "provider.request_id", "request-1" ] }

        Assert.AreEqual([ toolCall ], result.ToolCalls)
        Assert.AreEqual(Some """{"answer":"Nao"}""", result.StructuredOutputJson)
        Assert.AreEqual(Some "request-1", result.Extensions |> Map.tryFind "provider.request_id")
        Assert.AreEqual(None, ProviderUsage.total result.Usage)

    [<TestMethod>]
    member _.``Usage total reflects reporting fidelity``() =
        let cases =
            [ ProviderUsage.None, None
              ProviderUsage.Aggregate 12, Some 12
              ProviderUsage.Partial(Some 7, None), None
              ProviderUsage.Partial(Some 7, Some 5), Some 12
              ProviderUsage.Full(7, 5), Some 12 ]

        for usage, expected in cases do
            Assert.AreEqual(expected, ProviderUsage.total usage)
