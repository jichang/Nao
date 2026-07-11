namespace Nao.Core.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type RoleTests () =

    [<TestMethod>]
    member _.RoleHasSystemCase () =
        let role = System
        Assert.AreEqual(System, role)

    [<TestMethod>]
    member _.RoleHasUserCase () =
        let role = User
        Assert.AreEqual(User, role)

    [<TestMethod>]
    member _.RoleHasAssistantCase () =
        let role = Assistant
        Assert.AreEqual(Assistant, role)

[<TestClass>]
type MessageTests () =

    [<TestMethod>]
    member _.CanCreateMessage () =
        let msg = { Role = User; Content = "hello" }
        Assert.AreEqual(User, msg.Role)
        Assert.AreEqual("hello", msg.Content)

    [<TestMethod>]
    member _.ConversationIsMessageList () =
        let conv: Conversation =
            [ { Role = System; Content = "You are helpful" }
              { Role = User; Content = "Hi" }
              { Role = Assistant; Content = "Hello!" } ]
        Assert.AreEqual(3, conv.Length)

[<TestClass>]
type CompletionOptionsTests () =

    [<TestMethod>]
    member _.DefaultHasExpectedValues () =
        let opts = CompletionOptions.Default
        Assert.AreEqual(0.7, opts.Temperature)
        Assert.AreEqual(None, opts.MaxTokens)
        Assert.AreEqual([], opts.StopSequences)

    [<TestMethod>]
    member _.CanCustomizeOptions () =
        let opts = { Temperature = 0.0; MaxTokens = Some 100; StopSequences = ["STOP"] }
        Assert.AreEqual(0.0, opts.Temperature)
        Assert.AreEqual(Some 100, opts.MaxTokens)
        Assert.AreEqual(1, opts.StopSequences.Length)

[<TestClass>]
type CompletionResultTests () =

    [<TestMethod>]
    member _.CanCreateResult () =
        let result = { Content = "answer"; FinishReason = "stop"; TokensUsed = Some 42 }
        Assert.AreEqual("answer", result.Content)
        Assert.AreEqual("stop", result.FinishReason)
        Assert.AreEqual(Some 42, result.TokensUsed)

