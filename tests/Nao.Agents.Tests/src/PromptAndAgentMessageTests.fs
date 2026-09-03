namespace Nao.Agents.Tests

open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Agents

[<TestClass>]
type PromptTests () =

    [<TestMethod>]
    member _.EmptyPromptRendersEmpty () =
        let result = Prompt.render Prompt.Empty
        Assert.AreEqual("", result)

    [<TestMethod>]
    member _.RenderIncludesRole () =
        let prompt = { Prompt.Empty with Role = "You are a helpful assistant" }
        let result = Prompt.render prompt
        Assert.IsTrue(result.Contains("# Role"))
        Assert.IsTrue(result.Contains("You are a helpful assistant"))

    [<TestMethod>]
    member _.RenderIncludesObjective () =
        let prompt = { Prompt.Empty with Objective = "Summarize text" }
        let result = Prompt.render prompt
        Assert.IsTrue(result.Contains("# Objective"))
        Assert.IsTrue(result.Contains("Summarize text"))

    [<TestMethod>]
    member _.RenderIncludesDomainKnowledge () =
        let prompt = { Prompt.Empty with DomainKnowledge = [ "Fact 1"; "Fact 2" ] }
        let result = Prompt.render prompt
        Assert.IsTrue(result.Contains("# Domain Knowledge"))
        Assert.IsTrue(result.Contains("- Fact 1"))
        Assert.IsTrue(result.Contains("- Fact 2"))

    [<TestMethod>]
    member _.RenderIncludesConstraints () =
        let prompt = { Prompt.Empty with Constraints = [ "Be concise"; "No speculation" ] }
        let result = Prompt.render prompt
        Assert.IsTrue(result.Contains("# Constraints"))
        Assert.IsTrue(result.Contains("- Be concise"))

    [<TestMethod>]
    member _.RenderIncludesExamples () =
        let example = { Input = "Hello"; Output = "Hi there"; Explanation = Some "Greeting" }
        let prompt = { Prompt.Empty with Examples = [ example ] }
        let result = Prompt.render prompt
        Assert.IsTrue(result.Contains("# Examples"))
        Assert.IsTrue(result.Contains("Input: Hello"))
        Assert.IsTrue(result.Contains("Output: Hi there"))
        Assert.IsTrue(result.Contains("Explanation: Greeting"))

    [<TestMethod>]
    member _.RenderOutputSchema () =
        let schema = """{"type":"object"}"""
        let prompt = { Prompt.Empty with OutputFormat = Schema schema }
        let result = Prompt.render prompt
        Assert.AreEqual(sprintf "# Output Format\nFollow this schema:\n%s" schema, result)

    [<TestMethod>]
    member _.RenderContextBeforeOutputSchema () =
        let prompt =
            { Prompt.Empty with
                Context = [ "Document A" ]
                OutputFormat = Schema "Markdown" }
        let result = Prompt.render prompt
        Assert.AreEqual("# Context\n- Document A\n\n# Output Format\nFollow this schema:\nMarkdown", result)

    [<TestMethod>]
    member _.RenderContextSection () =
        let prompt = { Prompt.Empty with Context = [ "Document A"; "Document B" ] }
        let result = Prompt.render prompt
        Assert.IsTrue(result.Contains("# Context"))
        Assert.IsTrue(result.Contains("- Document A"))

[<TestClass>]
type AgentMessageTests () =

    [<TestMethod>]
    member _.CreateDirectedMessage () =
        let from = { Name = "agent1"; Description = "" }
        let toAgent = { Name = "agent2"; Description = "" }
        let message = AgentMessage.create from toAgent "hello"
        Assert.AreEqual("agent1", message.From.Name)
        Assert.AreEqual(Some toAgent, message.To)
        Assert.AreEqual("hello", message.Content)

    [<TestMethod>]
    member _.BroadcastHasNoRecipient () =
        let from = { Name = "agent1"; Description = "" }
        let message = AgentMessage.broadcast from "hello all"
        Assert.AreEqual(None, message.To)
        Assert.AreEqual("hello all", message.Content)
